using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Bot.Scenarios.Weekly;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Bot.Match;

// ===========================================================================
// 架构锁 · 模块层 Core/Bot/Scenarios（v7.2）
// 本层职责：单个挂机模块自己的菜单流程与私有状态机（只认自己的界面）。
// 硬约束（构建期 tools/arch-guard/arch-guard.ps1 强制，违反即编译失败）：
//   · 不许引用别的模块的 *Scenario（只读借用别人的 *Brain.Classify 认残留页面是允许的）
//   · 不许自己写场次记账 / 比赛内挂机节奏 —— 一律走 MatchPlay.Tick / IdleTapAsync
//   · 共享界面判定与决策一律用 Core/Bot/Match 的 MatchBrain，不要复制一份
// 新增模块：ModuleIds 加常量 + BotModuleCatalog 加一条记录，再写本目录的状态机。
// 除非用户明确要求更换写法，否则按本层约定写。
// ===========================================================================

namespace EfootballBot.Core.Bot.Scenarios.CnWeekly;

/// <summary>
/// 国服周活动挂机场景（实况足球在线）。菜单侧状态机：主页（推荐页签）→ 饱和归位进比赛页签
/// → 卡行 下→左 选「比赛活动」→ 活动列表（对阵电脑）进活动 → 介绍页「进入」→ 按活动形态分叉：
/// ①杯赛型（冠军赛）：对阵表 A/RT 下一步 → 活动主页：首次进「球队阵容」RS 自动配置选「活动加成」
///   → B 完成 → RT 教练入场开赛；赛后回活动主页循环，打满冠军赛全程（小组赛×3+淘汰赛×4=7 场）。
/// ②巡回赛型（球星训练，2026-10-17 实机）：活动主页（前往比赛 + X 活动设置）——首次 X 打开
///   活动设置只读核验 教练模式=开 + 教练模式内控制=完全托管（不盲改：面板里 A 可能误开换队；
///   OCR 漏读则提示后照常开赛）→ B 完成 → A 前往比赛；赛后回主页循环。主页右栏主要奖励
///   带勾时右移进 i 信息弹窗读 x/x：全满=拿满，B 退上上页回活动列表换下一个活动（无卡自停）；
///   没满=回前往比赛继续刷（勾≠拿满，以弹窗 x/x 为准）。
/// ③挑战型（挑战清单，2026-10-17 实机）：介绍页进入后弹「挑战清单」选卡页——目标卡=
///   最后带勾的后一张=第一张上锁的前一张；★游标可能停在刚打完的勾卡上（白色聚焦），
///   探测聚焦白卡差值导航过去再 A；看见绿勾直接拉黑；进入可能弹「参加新的挑战？」
///   （默认焦点在取消，上移选进入）。通关（胜场读满/打满 2 局/奖励为「首次以后的奖励」
///   =选错卡）后 B 回清单换下一张；无可挑战的卡（全勾/全锁）重进清单刷新，重进仍无 =
///   全部完成自动停止。卡详情页（独立分类 ChallengeCardHome，词为「完成挑战…」）
///   ★绝不进 i 弹窗（合计进度无意义）；挑战模式跳过巡回赛的活动设置核验。
/// ④教练签下链（2026-11-04 实机 晋级挑战）：介绍/「加入」进活动后可能先弹「请选择您的教练」
///   ——按卡片上的评价数字选更大的一位（左×3 归位 → 右×Slot → A）→ 教练资料页 A（不生效改 RT
///   底栏「确认选择」）→ 「签下确认」弹窗 A（默认焦点在确认；A 未生效再上移纠正）。签完继续
///   既有开赛流程（阵容/设置/难度/前往比赛按活动形态自会接住）。
/// 比赛中流程与我的联赛完全共用：InMatch 随机点击、终场、结算弹窗、满级检测、场次计数。
/// 达到「完成场次停止」设定或主要奖励打完后由用户手动停止（完成态探针待校准）。
/// 判定只用固定 UI 词（对阵电脑/教练入场/前往比赛/活动设置/球队阵容列表/活动加成…），
/// 活动名每周轮换禁作判定点。
/// </summary>
public sealed partial class CnWeeklyScenario : IScenario
{
    /// <summary>菜单侧流程阶段（屏幕驱动；每阶段独立失败预算，超限 B 逃生回 Flow 重导航）。</summary>
    private enum CwPhase
    {
        /// <summary>菜单导航：主页→比赛活动列表→活动链→活动主页（屏幕驱动）。</summary>
        Flow,
        /// <summary>阵容配置：球队阵容页 RS 呼出自动配置 → 选「活动加成」→ B 完成。</summary>
        SquadSetup,
        /// <summary>活动主页就绪：RT 教练入场，之后交给共用比赛流程。</summary>
        LaunchReady,
        /// <summary>主要奖励已获得：B 清介绍页/弹窗回活动列表，槽位 +1 换下一个活动。</summary>
        Retreating,
        /// <summary>挑战型「挑战清单」：归位第一张后逐卡右扫，找第一张（无锁且无勾且非
        /// 本运行内已通关/已拉黑）的卡按 A 进入；扫到尾全勾 = 全部通关退出停止。</summary>
        ChallengeList,
        /// <summary>挑战全部完成/锁态异常放弃：B 退介绍页退活动列表（或 B 后 A 重进清单刷新锁态）。</summary>
        ChallengeRetreat,
        /// <summary>全部活动刷完且配置了接力模块：先 B 退回主页（活动页的词会干扰目标模块的
        /// 界面判定），到主页后换场景继续挂机。</summary>
        Handoff,
    }

    private readonly BotEngine _engine;

    private readonly Action<string, LogLevel> _log;
    /// <summary>后台鼠标（教练选择页只认鼠标点击，2026-11-05 实机）。</summary>
    private readonly BackgroundMouse _mouse = new();

    private CwPhase _phase = CwPhase.Flow;
    /// <summary>
    /// 本场已放弃跳过（2026-11-06 起挑战赛也允许跳）：暂停菜单里没读到「对局获胜」时
    /// 按 B 回比赛，并把本场标记为"不再尝试"——否则每一帧横幅都会开一次暂停菜单，
    /// 比赛被反复暂停、永远打不完。新一场开始/本场结束时复位。
    /// </summary>
    private bool _skipBlockedThisMatch;
    /// <summary>当前阶段累计行动次数（SetPhase 清零；超限 B 逃生防死循环）。</summary>
    private int _attempts;
    /// <summary>阵容页已按 RS 呼出菜单并成功对「活动加成」按 A（阵容页→B 完成 阶段的门）。</summary>
    private bool _menuApplied;
    /// <summary>本次运行阵容配置已完成（成功/放弃均置位：每次运行只配一次，赛后循环直接开赛）。</summary>
    private bool _squadConfigured;
    /// <summary>进入阵容页的尝试次数（超 3 次放弃配置，照常开赛——活动加成是加分优化非阻塞项）。</summary>
    private int _squadTries;
    /// <summary>巡回赛活动设置已核验/已放弃（教练模式=开+完全托管；每次运行核验一次）。</summary>
    private bool _tourReady;
    /// <summary>为核验设置打开活动设置的次数（超 3 次放弃核验照常开赛）。</summary>
    private int _tourSettingsTries;
    /// <summary>核验通过后活动设置面板又被打开的次数（≥2 = 主页焦点停在底栏「活动设置」上，
    /// A 前须先上移焦点到「前往比赛」，否则 A↔设置面板乒乓循环）。</summary>
    private int _tourPanelReopens;

    // ---- 教练签下链（2026-11-04 实机 晋级挑战：进入活动先签教练再开赛） ----
    /// <summary>本活动已签下教练（同一活动只签一次；换活动/重进活动时复位）。</summary>
    private bool _coachSigned;
    /// <summary>本次运行签下教练的累计次数（&gt;3 = 签下后仍反复回到选择页，停止挂机防死循环）。</summary>
    private int _coachSigns;

    // ---- 挑战型活动子流程（挑战清单选卡页出现时激活；退出活动/停止时复位） ----
    /// <summary>当前活动是挑战型（以「挑战清单」选卡页或卡详情页「完成的条件」识别）。</summary>
    private bool _challengeMode;
    /// <summary>本运行内已确认通关/已跳过的卡号（胜场读满或重复进入已通关卡）。</summary>
    private readonly HashSet<int> _chLocalDone = new();
    /// <summary>多次进入未生效的卡号（拉黑防死循环）。</summary>
    private readonly HashSet<int> _chBlocked = new();
    /// <summary>最近一次按 A 时预期的卡号（=扫描出的第一张可挑战卡；默认光标即在该卡上）。</summary>
    private int _chCurrentCard;
    /// <summary>当前卡已开局数（通关探针漏读时打满 2 局按通关处理，防同卡无限重打）。</summary>
    private int _chCurPlays;
    /// <summary>连续按 A 却未离开清单的次数（光标与扫描不符等异常；≥3 改为重进清单刷新）。</summary>
    private int _chStaleA;
    /// <summary>B 重进清单刷新的次数（光标复位/锁态刷新共用；超 3 次停止挂机）。</summary>
    private int _chRefreshs;
    /// <summary>ChallengeRetreat 子模式：B 退到介绍页后按 A 重进清单（刷新锁态/复位光标）。</summary>
    private bool _chRetreatReenter;
    /// <summary>同一张卡反复进入却没有比赛开始的次数（≥2 = 该卡应已通关而勾/胜场探针
    /// 均未读出，按通关跳过——防 清单↔卡页 无限打转，挑战全部打完后的唯一空转形态）。</summary>
    private int _chRepeatEnters;
    /// <summary>上次进卡后到本次再扫描之间是否有比赛结束过（false=没有，供 _chRepeatEnters 判定）。</summary>
    private bool _chMatchSinceEnter;
    /// <summary>「可见卡无可挑战」后的盲点右移翻页探测次数：清单卡数不固定且可见卡≠全部卡
    /// （实机 2026-10-17 晚：01-03 全勾仍有第 4 卡屏外残边），必须右移到真末尾确认后才能
    /// 重进刷新/报全部完成。可见卡集一变即归零；连续 3 次右移无变化=真末尾，上限 8 防异常页。</summary>
    private int _chEdgeProbes;
    /// <summary>上次扫描可见的卡号集合（翻页探测比较用）。</summary>
    private string _chLastVisibleCards = "";
    /// <summary>当前挂机的活动槽位（0 起，活动列表卡行从左数；主要奖励刷满退列表后 +1）。</summary>
    private int _slotIndex;
    /// <summary>活动列表卡位上限：一周活动卡数远小于此。右移次数按 _slotIndex 给，光标到卡行末尾后
    /// 多余右移只是饱和（停在最后一张卡），所以槽位涨过上限就说明「再右移也不会换卡」——按全部刷完收尾。
    /// 实机 2026-11-20 教训：没有这道闸，槽位从 1 涨到 306，每轮都进同一张已满卡，挂机 7 小时没接力。</summary>
    private const int MaxEventSlots = 6;
    /// <summary>上一张「主要奖励全满、退场」活动卡的指纹（<see cref="CnWeeklyBrain.CardSignature"/>）。
    /// 又回到同一张卡 = 右移已到卡行末尾饱和，已没有未刷满的新卡 → 全部完成走接力。
    /// 比赛结束后清零（进度已变，旧指纹不再可比）。注意：不要放进 ResetRewardCheckState——
    /// 那个方法每换一张卡都会被调用，清零就等于关掉这道保护。</summary>
    private string _lastFullCardSig = "";

    // ---- 主页 → 比赛活动列表 导航（2026-11-05 实机：老配方三拍永远到不了目的地） ----
    /// <summary>比赛页卡行策略阶梯（先下 N 次再左 M 次）。新版 UI 卡序/行数变了就换下一档，
    /// 不把某一版布局写死成一个魔法序列。</summary>
    private static readonly (int Down, int Left)[] HomeCardStrategies =
        { (1, 1), (1, 2), (1, 3), (2, 1), (2, 2), (1, 0) };
    /// <summary>主页导航累计尝试次数（按阶梯取策略；到达活动列表即清零）。</summary>
    private int _navTries;
    /// <summary>连续落在「识别不出的页面」的次数（B 逃生预算；认出任何页面即清零）。</summary>
    private int _navUnknown;
    /// <summary>上次打印未知页面 OCR 原文的时间（限流，未知页每 20s 一条）。</summary>
    private DateTime _unknownDumpAt;

    // ---- 巡回赛主要奖励核验（主页勾 → i 信息弹窗 x/x，用户 2026-10-17 流程） ----
    /// <summary>核验步：0=未在核验；1=已右移到 i；2=已按 A 等弹窗；3=已确认拿满（退场）。</summary>
    private int _rewardCheckStage;
    /// <summary>i 信息弹窗多次打不开：放弃 x/x 核验，按未拿满继续刷（保守不提前退场）。</summary>
    private bool _rewardPopupGaveUp;
    /// <summary>弹窗已判「未拿满」：本场打完前不再重复核验（勾一直挂在主页上，
    /// 不加此门控会 主页→弹窗→主页 原地循环，永远走不到前往比赛——2026-10-17 实机教训）。
    /// 比赛结束后复位，下次回主页再核验一次。</summary>
    private bool _rewardContinueUntilMatch;

    private string _lastScreenName = "";

    private int _streak;

    private readonly MatchFlowMemory _memory = new();
    /// <summary>赛中匹配（共享层）：开赛/终场判定 + 场内挂机节奏 + 场次记账，五个模块同一份实现。</summary>
    private readonly MatchPlay _matchPlay;
    /// <summary>底层场次跟踪器（模块内读用时/关键比赛等细节用；比赛怎么算由 MatchPlay 决定）。</summary>
    private MatchSessionTracker _matchSession => _matchPlay.Session;

    private DateTime _lastMaxLevelAt = DateTime.MinValue;

    private bool _takeoverLogged;
    /// <summary>TitleScreen 首次出现时间（比赛上下文中按 X 前 30s 宽限，区分加载画面与真标题）。</summary>
    private DateTime? _titleSince;

    public string Name => "国服周活动";
    /// <summary>比赛中（供引擎把手柄诊断挡在比赛之外）。</summary>
    public bool InMatch => _matchSession.Active;

    public CnWeeklyScenario(BotEngine engine, Action<string, LogLevel> log)
    {
        _engine = engine;
        _log = log;
        _matchPlay = new MatchPlay(engine, "国服周活动", log);
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var obs = await _engine.ObserveAsync(ct);
        var now = DateTime.Now;
        // 角区探针兜底 InMatch 需比赛上下文（同我的联赛场景）
        bool corner = ScreenProbe.CornerHasContent(obs.Frame);
        bool light = ScreenProbe.IsMostlyLightPage(obs.Frame);
        // 画面还是球场（进球庆祝/回放过场）→ 共享分类一律按比赛中处理（2026-11-20 虚计场次事故）
        bool pitch = ScreenProbe.LooksLikePitch(obs.Frame);
        bool matchContext = _memory.SawWithin(MatchScreen.InMatch, TimeSpan.FromSeconds(90), now) ||
                            _memory.SawWithin(MatchScreen.MatchStartPage, TimeSpan.FromSeconds(90), now);
        // 国服周活动界面先判：命中时 ml 记 Unknown（由状态机处理，不走共享决策），
        // 未命中交 MyLeagueBrain（比赛中/终场/共用弹窗/标题画面等）
        var cw = CnWeeklyBrain.Classify(obs.Detections, obs.Ocr);
        var ml = cw == CwScreen.None
            ? MatchBrain.Classify(obs.Detections, obs.Ocr, corner, matchContext, light,
                MatchShared.LoginRecovery(_memory, now), pitch)
            : MatchScreen.Unknown;
        _memory.Record(ml, now);
        if (!_takeoverLogged)
        {
            _takeoverLogged = true;
            _log($"[国服周活动] 智能接管：当前界面「{(cw != CwScreen.None ? cw.ToString() : ml.ToString())}」，从该阶段继续", LogLevel.Success);
        }
        // ---- 赛中匹配（共享层 MatchPlay）：开赛/终场/超时判定 + 场次与关键比赛记账
        var tick = _matchPlay.Tick(ml, now,
            sawKeyMatchHint: ml == MatchScreen.KeyMatchDialog || obs.Ocr.ContainsJoined("关键比赛"),
            isHome: false);
        if (tick.Started)
        {
            _tourPanelReopens = 0; // 比赛开打：游戏会重置主页焦点，乒乓探测复位
            _skipBlockedThisMatch = false; // 新一场：重新给一次跳过机会
        }
        if (tick.Ended && tick.EndedTrusted)
        {
            _skipBlockedThisMatch = false;
            // 主要奖励进度只在比赛后变化：解除「未拿满」门控，下次回主页再核验一次
            _rewardContinueUntilMatch = false;
            // 打完比赛进度已变：上一张满卡的指纹不再可比，清掉（下一张全满卡重新记）
            _lastFullCardSig = "";
            if (_challengeMode && _chCurrentCard > 0)
            {
                _chCurPlays++;
                _chMatchSinceEnter = true;
                _log($"[国服周活动·挑战] 卡 {_chCurrentCard:00} 第 {_chCurPlays} 局结束", LogLevel.Info);
            }
            var cwCfg = _engine.Config.CnWeekly;
            if (cwCfg.StopAfterMatches > 0 && _engine.ModuleMatchesDone >= cwCfg.StopAfterMatches)
            {
                _log($"[国服周活动] 已完成设定场次（{_engine.ModuleMatchesDone}/{cwCfg.StopAfterMatches} 场），自动停止挂机", LogLevel.Success);
                _engine.Stop();
                return 0;
            }
        }
        // ---- 共用：球员满级提示（60s 节流，常开） ----
        if (obs.Ocr.ContainsJoined("满级") && (now - _lastMaxLevelAt).TotalSeconds > 60)
        {
            _lastMaxLevelAt = now;
            _engine.Stats.PlayersMaxed++;
            _log($"[国服周活动] 球员满级提示（累计 {_engine.Stats.PlayersMaxed} 次）", LogLevel.Success);
            _engine.ReportMaxLevel("比赛中满级提示");
        }
        string screenName = cw != CwScreen.None ? $"国服周活动.{cw}" : ml.ToString();
        if (screenName == _lastScreenName) _streak++;
        else
        {
            _streak = 0;
            _lastScreenName = screenName;
            _attempts = 0; // 换屏即重置当前阶段预算：Flow 各屏幕的失败预算互不侵占
            _log($"[国服周活动] 界面：{screenName}", LogLevel.Info);
        }
        // ---- 国服菜单侧状态机：接管国服周活动界面与阶段内无特征界面 ----
        // ---- 国服比赛起始页：底栏印「设置|球场|RT 下一步」。按用户 2026-11-21 规则，国服
        //      「下一步/确认/确定」一律走国服键序：① RT（页面所印提示键）→ ② A → ③ 后台鼠标点击
        //      （共享层 CnAdvance）。拦截仍先于状态机：画面不变的起始页会让共享决策转静默等待
        //      （实机 2026-10-14 00:53：起始页无人按键卡死 6 分钟）。
        if (ml == MatchScreen.MatchStartPage)
        {
            if (_streak < 1) return 800; // 过渡帧防抖
            return await CnAdvance.AdvanceAsync(_engine, _mouse, _streak + 1, "起始页", "国服周活动", _log,
                obs.Ocr, obs.Detections, (0.850, 0.920), ct);
        }
        int? cnWait = await HandleCnAsync(cw, ml, obs, ct);
        if (cnWait is > 0) return cnWait.Value;
        if (cnWait is not null) return 1500;
        // cw==None 且阶段已交回共享流程：走共用决策（比赛中/终场/弹窗/逃生）
        // 跳过比赛：2026-11-06 用户决定挑战赛也允许跳（旧版按「跳过不计积分」全网禁用）。
        // 安全性由 MyLeagueBrain 的暂停菜单二次把关保证：只有菜单里出现「对局获胜」
        // 才真的按跳过，否则 B 返回比赛；且本场只尝试一次（_skipBlockedThisMatch）。
        var action = MatchBrain.Decide(ml, obs.Ocr, _streak, matchContext, obs.Frame,
            allowMatchSkip: !_skipBlockedThisMatch);
        // 暂停菜单没读到可跳依据（Decide 给了 B 返回比赛）→ 本场不再尝试跳过。
        // 失败一次就收手：新 UI 的跳过小字若不再是「对局获胜」，继续尝试只会让
        // 暂停菜单每几秒开一次、比赛永远打不完。
        if (ml == MatchScreen.CnMatchPause && action.Kind == MatchActionKind.GamepadBack && !_skipBlockedThisMatch)
        {
            _skipBlockedThisMatch = true;
            _log("[国服周活动] 暂停菜单里没有「对局获胜」（当前不可跳或措辞已变），本场不再尝试跳过", LogLevel.Warn);
        }
        // 标题画面宽限（同国际服周活动场景）：比赛上下文中的「eFootball™+X」帧多为加载画面。
        // 国服赛后品牌过场帧会连续出现 30s 以上，宽限拉长到 90s
        if (action.Kind != MatchActionKind.PadXPress)
        {
            _titleSince = null;
        }
        else
        {
            bool recentFlow =
                _memory.SawWithin(MatchScreen.InMatch, TimeSpan.FromMinutes(3), now) ||
                _memory.SawWithin(MatchScreen.MatchStartPage, TimeSpan.FromMinutes(3), now);
            bool postMatchChain = _memory.SawWithin(MatchScreen.FullTime, TimeSpan.FromMinutes(2), now);
            var grace = postMatchChain ? TimeSpan.FromSeconds(90) : TimeSpan.FromSeconds(30);
            _titleSince ??= now;
            if (recentFlow && now - _titleSince.Value < grace)
            {
                _log("[国服周活动] eFootball 标志帧（疑似比赛加载/赛后过场画面），暂不按 X，继续观察", LogLevel.Info);
                return 2000;
            }
        }
        switch (action.Kind)
        {
            case MatchActionKind.PadEnterMatch:
                // 国服主页不走国际服的下→左×6：国服主页是页签行（推荐/比赛/签约/商店），
                // 上×2 进页签行 → 左×4 饱和归位「推荐」→ 右×1 到「比赛」。
                // ★2026-11-05 实机：这里只按三拍就等下一帧，一旦下一帧不是 HomeMatch 也不是
                // 活动列表（新版 UI 页签/卡行读不全），共享流程会把同一三拍无限重放，永远到不了
                // 活动列表。改为一次按完整序列（进页签→进卡行→A），并做页面安全检查。
                if (CnWeeklyBrain.LooksLikeCnHome(obs.Ocr))
                    return await NavigateToEventListAsync("主菜单");
                return await EscapeUnknownPageAsync(obs, "不是主页特征");
            case MatchActionKind.PadConfirm:
                // 国服键序（用户 2026-11-21）：① RT（页面所印提示键）→ ② A → ③ 后台鼠标点击。
                // 段位积分页 / 赛后小结这类国服网页层页面只吃鼠标，旧逻辑每轮空按 A 就卡死在这里。
                // 比赛中的回放/中场小弹窗不掺进来——比赛里 RT 有游戏内含义，只按 A。
                if (_matchSession.Active)
                {
                    _log($"[国服周活动] 比赛中弹窗 A：{action.Target}", LogLevel.Info);
                    await _engine.Pad.Confirm();
                    break;
                }
                return await CnAdvance.AdvanceAsync(_engine, _mouse, _streak + 1, action.Target,
                    "国服周活动", _log, obs.Ocr, obs.Detections, null, ct);
            case MatchActionKind.PadUpConfirm:
                _log($"[国服周活动] 弹窗上移确认：{action.Target}", LogLevel.Info);
                await _engine.Pad.Dpad(PadDir.Up);
                await _engine.Pad.Confirm();
                break;
            case MatchActionKind.PadDownConfirm:
                _log($"[国服周活动] 弹窗下移确认：{action.Target}", LogLevel.Info);
                await _engine.Pad.Dpad(PadDir.Down);
                await _engine.Pad.Confirm();
                break;
            case MatchActionKind.PadStartPress:
                _log($"[国服周活动] {action.Target}", LogLevel.Info);
                await _engine.Pad.Start();
                break;
            case MatchActionKind.PadXPress:
                _log($"[国服周活动] {action.Target}", LogLevel.Info);
                await _engine.Pad.X();
                break;
            case MatchActionKind.PadLoginNav:
                // eFootball 2027 标题页登录序列：下→右→A（A 落在「登录」上，用户实机序列）
                _log($"[国服周活动] {action.Target}", LogLevel.Info);
                await _engine.Pad.Dpad(PadDir.Down);
                await _engine.Pad.Dpad(PadDir.Right);
                await _engine.Pad.Confirm();
                break;
            case MatchActionKind.GamepadBack:
                _log($"[国服周活动] {action.Target}", LogLevel.Warn);
                await _engine.Pad.Back();
                break;
            case MatchActionKind.GamepadBackAfterConfirm:
                _log($"[国服周活动] {action.Target}", LogLevel.Warn);
                await _engine.Pad.Dpad(PadDir.Up); // B 多次无效：先上移把焦点从「取消」移到确认键
                await _engine.Pad.Confirm(); // 再 A 确认（直接 A 可能落在取消键上继续死循环）
                await _engine.Pad.Back(); // 最后 B 返回
                break;
            case MatchActionKind.MatchIdleTap:
                // 比赛中挂机点击：按 A + 用户「挂机节奏」区间随机间隔（共享层 MatchPlay，拟人化）
                return await _matchPlay.IdleTapAsync(ct);
            case MatchActionKind.PadMoveRight:
                await _engine.Pad.Dpad(PadDir.Right);
                break;
        }
        return action.WaitMs;
    }

    // ---------------- 国服菜单侧状态机 ----------------
    /// <summary>返回正数/0=本轮已处理（下一轮延时）；null=未接管（走共享决策）。</summary>
    private async Task<int?> HandleCnAsync(CwScreen cw, MatchScreen ml, Observation obs, CancellationToken ct)
    {
        if (cw != CwScreen.None) _navUnknown = 0; // 认出任何国服界面 = 脱离未知页，逃生预算清零
        switch (_phase)
        {
            case CwPhase.Flow:
            {
                switch (cw)
                {
                    case CwScreen.HomeRecommended:
                    case CwScreen.HomeMatch:
                        if (++_attempts > 4)
                        {
                            _log("[国服周活动] 主页导航多轮未进入比赛活动，暂停 8s 后重试（若反复出现请回报日志）", LogLevel.Error);
                            _attempts = 0;
                            return 8000;
                        }
                        // 饱和归位（任意焦点/页签状态皆可达）：上×2 进页签行 → 左×4 到最左「推荐」
                        // → 右×1 到「比赛」页签 → 进卡行选「比赛活动」→ A
                        // 卡行步数按策略阶梯逐轮换档（新版 UI 卡序变化时不死循环）
                        return await NavigateToEventListAsync(cw == CwScreen.HomeMatch ? "比赛页签" : "推荐页签");
                    case CwScreen.EventsList:
                        if (_navTries > 0)
                        {
                            _navTries = 0;
                            _navUnknown = 0;
                            _log("[国服周活动] 已到比赛活动列表（目的地）", LogLevel.Success);
                        }
                        return await EnterEventAsync();
                    case CwScreen.EventIntro:
                        // 过渡帧防抖：介绍页稳定一拍再按（按 A 进入对阵表）
                        if (_streak < 1) return 800;
                        if (++_attempts > 4)
                        {
                            _log("[国服周活动] 介绍页进入多次未生效，B 关闭退回活动列表重导航", LogLevel.Warn);
                            SetPhase(CwPhase.Flow);
                            await _engine.Pad.Back();
                            return 2400;
                        }
                        _log("[国服周活动] 介绍页：A 进入", LogLevel.Info);
                        await _engine.Pad.Confirm();
                        return 2800;
                    case CwScreen.Bracket:
                        if (_streak < 2) return 900; // 过渡帧防抖：防连按两次跳过页面
                        if (++_attempts > 5)
                        {
                            _log("[国服周活动] 对阵表下一步多次未生效，B 返回介绍页重导航", LogLevel.Warn);
                            SetPhase(CwPhase.Flow);
                            await _engine.Pad.Back();
                            return 2400;
                        }
                        // 国服键序（用户 2026-11-21）：① RT（底栏所印「RT 下一步」）→ ② A →
                        // ③ 后台鼠标点右下按钮（对阵表也是国服网页层，A 被页签吃掉时只能点）
                        return await CnAdvance.AdvanceAsync(_engine, _mouse, _attempts, "对阵表下一步",
                            "国服周活动", _log, obs.Ocr, obs.Detections, (0.900, 0.920), ct);
                    case CwScreen.EventHome:
                        if (_streak < 1) return 800; // 过渡帧防抖
                        if (_challengeMode || WeeklyBrain.IsChallengeCardHome(obs.Ocr) ||
                            CnWeeklyBrain.IsCnChallengeCardHome(obs.Ocr))
                        {
                            // 挑战卡详情页（杯赛主页样式）：通关判定步完成后跳过阵容配置直接开赛
                            int? done = await ChallengeCardDoneStepAsync(obs);
                            if (done is not null) return done.Value;
                            SetPhase(CwPhase.LaunchReady);
                            return 700; // 下一轮 RT 教练入场
                        }
                        if (!_squadConfigured && _squadTries < 3)
                        {
                            _squadTries++;
                            SetPhase(CwPhase.SquadSetup);
                            _log($"[国服周活动] 活动主页：A 进球队阵容配置活动加成阵容（第 {_squadTries} 次）", LogLevel.Info);
                            await _engine.Pad.Confirm(); // 光标默认在左栏第 1 项「球队阵容」
                            return 2600;
                        }
                        if (!_squadConfigured)
                        {
                            _squadConfigured = true;
                            _log("[国服周活动] 球队阵容多次未进入，跳过阵容配置照常开赛（仅少活动加成，不影响比赛）", LogLevel.Warn);
                        }
                        SetPhase(CwPhase.LaunchReady);
                        return 700; // 下一轮 RT 教练入场
                    case CwScreen.EventHomeTour:
                        if (_streak < 1) return 800; // 过渡帧防抖
                        if (_challengeMode || WeeklyBrain.IsChallengeCardHome(obs.Ocr) ||
                            CnWeeklyBrain.IsCnChallengeCardHome(obs.Ocr))
                        {
                            // 挑战卡详情页（巡回赛主页样式；2026-10-17 第二周实机 绿茵传奇挑战赛
                            // 卡页同构且无「完成的条件」）：已通关 → B 回清单；未通关落入下方
                            // 既有流程（主要奖励核验 → A 前往比赛）
                            int? done = await ChallengeCardDoneStepAsync(obs);
                            if (done is not null) return done.Value;
                        }
                        {
                            // 主要奖励拿满核验（用户 2026-10-17 流程）：主页勾 → i 弹窗读 x/x，
                            // 仅巡回赛型——★挑战卡页绝不进 i（弹窗是本周全部活动卡合计进度，
                            // 对单个活动卡永远读不满），核验步内已按 _challengeMode 短路
                            int? reward = await TourRewardCheckStepAsync(obs);
                            if (reward is not null) return reward.Value;
                        }
                        if (!_challengeMode)
                        {
                            // 活动设置核验是巡回赛专属；挑战卡页 X 核验无意义（面板词不同，
                            // 只会白按 3 次再误报「未能核验」），跳过直接 A——挑战卡页按 A 由
                            // 游戏分叉进清单/难度页/比赛，挑战子流程自会接住
                            if (!_tourReady && _tourSettingsTries < 3)
                            {
                                _tourSettingsTries++;
                                _log($"[国服周活动] 巡回赛主页：X 打开活动设置核验 教练模式=开+完全托管（第 {_tourSettingsTries} 次）", LogLevel.Info);
                                await _engine.Pad.X();
                                return 2600;
                            }
                            if (!_tourReady)
                            {
                                _tourReady = true; // 放弃核验：不再反复开设置，按当前设置开赛
                                _log("[国服周活动] 活动设置多次未能核验，照常开赛；若比赛无法自动进行，请手动在活动设置开启 教练模式=开 + 完全托管", LogLevel.Error);
                            }
                        }
                        if (++_attempts > 4)
                        {
                            _log("[国服周活动] 前往比赛多次未生效，B 返回活动列表重导航", LogLevel.Warn);
                            SetPhase(CwPhase.Flow);
                            await _engine.Pad.Back();
                            return 2400;
                        }
                        // A 未生效多因焦点停在底栏「活动设置」上（B 关面板后焦点不在前往比赛，
                        // A 会再开面板形成乒乓）：先上移焦点再 A
                        if (_attempts >= 3 || _tourPanelReopens >= 2)
                        {
                            _log("[国服周活动] 巡回赛主页：上移焦点避开活动设置 → A 前往比赛", LogLevel.Info);
                            await _engine.Pad.Dpad(PadDir.Up);
                        }
                        else
                        {
                            _log("[国服周活动] 巡回赛主页：A 前往比赛", LogLevel.Info);
                        }
                        await _engine.Pad.Confirm();
                        return 3400;
                    case CwScreen.ChallengeCardHome:
                    {
                        // 挑战卡详情页（独立分类，2026-10-17 第二周实机 绿茵传奇挑战赛）：
                        // ★绝不进 i 信息弹窗（主要奖励 x/x 是本周全部活动卡合计进度，对单个
                        // 活动卡永远读不满）、不核验巡回赛活动设置——直接 A 前往比赛，由游戏
                        // 分叉进清单/难度页/比赛，挑战子流程自会接住
                        if (_streak < 1) return 800; // 过渡帧防抖
                        int? done = await ChallengeCardDoneStepAsync(obs);
                        if (done is not null) return done.Value;
                        if (++_attempts > 4)
                        {
                            _log("[国服周活动] 挑战卡前往比赛多次未生效，B 返回重走流程", LogLevel.Warn);
                            SetPhase(CwPhase.Flow);
                            await _engine.Pad.Back();
                            return 2400;
                        }
                        _log("[国服周活动] 挑战卡详情页：A 前往比赛", LogLevel.Info);
                        await _engine.Pad.Confirm();
                        return 3400;
                    }
                    case CwScreen.EventSettings:
                        if (_streak < 1) return 800; // 面板打开过渡帧
                        if (_challengeMode)
                        {
                            // 挑战卡页弹出的设置面板：核验是巡回赛专属（面板词不同），B 返回即可
                            await _engine.Pad.Back();
                            return 2200;
                        }
                        if (CnWeeklyBrain.TourSettingsReady(obs.Ocr))
                        {
                            if (_tourReady) _tourPanelReopens++; // 未请求却又见面板=焦点停在活动设置上
                            else
                            {
                                _tourReady = true;
                                _log("[国服周活动] 活动设置核验通过：教练模式=开 + 完全托管，B 完成返回", LogLevel.Success);
                            }
                            _log("[国服周活动] 活动设置：B 完成", LogLevel.Debug);
                            await _engine.Pad.Back();
                            return 2200;
                        }
                        // 未读全/未达标：绝不盲按（面板里 A 可能落在「选择的球队」上误开换队），
                        // 只读不改；两轮后 B 退出照常开赛并提示
                        if (++_attempts > 2)
                        {
                            _tourReady = true;
                            _log("[国服周活动] 活动设置未确认 教练模式=开 + 完全托管（OCR 漏读或设置不对），B 返回照常开赛；若比赛不自动请手动改设置", LogLevel.Error);
                            await _engine.Pad.Back();
                            return 2200;
                        }
                        _log("[国服周活动] 活动设置：暂未读到 开+完全托管，再核验一拍", LogLevel.Warn);
                        return 1500;
                    case CwScreen.ChallengeList:
                        // 挑战型活动：介绍页按进入后弹的是挑战清单（半途启动直接落在清单同理）
                        return EnterChallengeListPhase();
                    case CwScreen.Difficulty:
                        return await HandleCnDifficultyAsync(obs);
                    case CwScreen.RewardPopup:
                        return await HandleRewardPopupAsync(obs);
                    case CwScreen.CoachSelect:
                        return await HandleCoachSelectAsync(obs);
                    case CwScreen.CoachDetail:
                        return await HandleCoachDetailAsync();
                    case CwScreen.SignConfirm:
                        return await HandleSignConfirmAsync();
                    case CwScreen.Squad:
                    case CwScreen.SquadMenu:
                        if (_squadConfigured)
                        {
                            // 配置已完成后异常落回阵容页（如完成后又弹回）：B 返回活动主页
                            if (++_attempts > 3) { SetPhase(CwPhase.LaunchReady); return 600; }
                            _log("[国服周活动] 异常落在球队阵容页，B 返回活动主页", LogLevel.Warn);
                            await _engine.Pad.Back();
                            return 2200;
                        }
                        SetPhase(CwPhase.SquadSetup);
                        return 800;
                }
                return null; // 其余共享界面（比赛中/弹窗/未知逃生）交给共用决策
            }
            case CwPhase.SquadSetup:
            {
                if (cw == CwScreen.SquadMenu)
                {
                    if (_streak < 1 && _menuApplied) return 900; // 按 A 后的过渡帧：菜单尚未关闭，先等一拍
                    if (!_menuApplied)
                    {
                        // 菜单已打开（无论何时呼出）：直接选「活动加成」。此处绝不能再按 R3——
                        // RS 在菜单打开时是关闭键，按了会把菜单收回去
                        _menuApplied = true;
                        _log($"[国服周活动] RS 菜单：下×{CnWeeklyBrain.EventBonusOptionIndex} 选「活动加成」→ A", LogLevel.Info);
                        await _engine.Pad.DpadRepeat(PadDir.Down, CnWeeklyBrain.EventBonusOptionIndex);
                        await _engine.Pad.Confirm();
                        return 3000;
                    }
                    // 菜单已应用过仍开着：A 未生效，重试；超预算 B 收起放弃（阵容保持原样）
                    if (++_attempts > 2)
                    {
                        _squadConfigured = true;
                        _log("[国服周活动] RS 菜单多次未应用活动加成，B 收起照常开赛", LogLevel.Warn);
                        SetPhase(CwPhase.Flow);
                        await _engine.Pad.Back();
                        return 2200;
                    }
                    _log("[国服周活动] RS 菜单仍在：重选「活动加成」→ A", LogLevel.Warn);
                    await _engine.Pad.DpadRepeat(PadDir.Down, CnWeeklyBrain.EventBonusOptionIndex);
                    await _engine.Pad.Confirm();
                    return 2800;
                }
                if (cw == CwScreen.Squad)
                {
                    if (!_menuApplied)
                    {
                        // 直落阵容页（接管/重试）：先 RS 呼出自动配置菜单；多次打不开则放弃
                        if (++_attempts > 2)
                        {
                            _squadConfigured = true;
                            _log("[国服周活动] 自动配置菜单多次未呼出，B 退出阵容页照常开赛", LogLevel.Warn);
                            SetPhase(CwPhase.Flow);
                            await _engine.Pad.Back();
                            return 2200;
                        }
                        _log("[国服周活动] 球队阵容页：RS 呼出自动配置球员", LogLevel.Info);
                        await _engine.Pad.R3();
                        return 2400;
                    }
                    // 加成已应用：B 完成（右上角 B 完成；保存确认弹窗由下方弹窗分支承接）
                    if (_streak < 1) return 800; // 应用加成后的过场防抖
                    if (++_attempts > 3)
                    {
                        _squadConfigured = true;
                        _log("[国服周活动] B 完成多次未退出阵容页，B 逃生照常开赛", LogLevel.Warn);
                        SetPhase(CwPhase.Flow);
                        await _engine.Pad.Back();
                        return 2200;
                    }
                    _log("[国服周活动] 阵容配置完成：B 完成", LogLevel.Info);
                    await _engine.Pad.Back();
                    return 2600;
                }
                if (cw == CwScreen.EventHome)
                {
                    if (_menuApplied)
                    {
                        _squadConfigured = true;
                        SetPhase(CwPhase.LaunchReady);
                        _log("[国服周活动] 阵容配置完成，回到活动主页", LogLevel.Success);
                        return 700;
                    }
                    // 还没配就回了主页（进阵容页的 A 未生效）：回 Flow 重试（预算在 Flow 侧）
                    SetPhase(CwPhase.Flow);
                    return 600;
                }
                // 阵容保存确认弹窗（B 完成后可能弹「是否保存」类确认）：有推进词就按 A。
                // ★统一弹窗规则：出现 取消/关闭 类字眼先上移（默认焦点多在取消上），把光标
                // 挪到 保存/确定 等推进键再按 A
                if (ml == MatchScreen.HasConfirm ||
                    obs.Ocr.ContainsJoined("确认") || obs.Ocr.ContainsJoined("保存") ||
                    obs.Ocr.FindButton("OK") is not null)
                {
                    if (++_attempts > 4)
                    {
                        _squadConfigured = true;
                        SetPhase(CwPhase.Flow);
                        await _engine.Pad.Back();
                        return 2200;
                    }
                    bool dismissable = obs.Ocr.ContainsJoined("取消") || obs.Ocr.ContainsJoined("关闭");
                    _log("[国服周活动] 阵容弹窗：A 确认", LogLevel.Info);
                    if (dismissable) await _engine.Pad.Dpad(PadDir.Up);
                    await _engine.Pad.Confirm();
                    return 2200;
                }
                // 无特征过场：等待；超预算回 Flow（屏幕驱动自会纠正）
                if (++_attempts > 10) SetPhase(CwPhase.Flow);
                return 1500;
            }
            case CwPhase.LaunchReady:
            {
                if (cw == CwScreen.EventHome)
                {
                    if (_streak < 2) return 900; // RT 过渡帧防抖：防连按两次（第二次可能误触入场后界面）
                    if (++_attempts > 4)
                    {
                        _log("[国服周活动] 教练入场多次未生效，B 返回重新走流程", LogLevel.Warn);
                        SetPhase(CwPhase.Flow);
                        await _engine.Pad.Back();
                        return 2200;
                    }
                    _log("[国服周活动] RT 教练入场", LogLevel.Info);
                    await _engine.Pad.RT();
                    return 3400;
                }
                if (cw == CwScreen.Bracket)
                {
                    // 赛后可能回到对阵表：下一步回活动主页再开赛（国服键序 RT → A → 鼠标）
                    if (_streak < 2) return 900;
                    return await CnAdvance.AdvanceAsync(_engine, _mouse, _streak + 1, "对阵表下一步",
                        "国服周活动", _log, obs.Ocr, obs.Detections, (0.900, 0.920), ct);
                }
                if (cw == CwScreen.Squad || cw == CwScreen.SquadMenu)
                {
                    if (++_attempts > 3) { SetPhase(CwPhase.Flow); return 600; }
                    await _engine.Pad.Back();
                    return 2200;
                }
                if (cw is CwScreen.EventsList or CwScreen.EventIntro or
                    CwScreen.HomeRecommended or CwScreen.HomeMatch or
                    CwScreen.EventHomeTour or CwScreen.ChallengeCardHome or
                    CwScreen.EventSettings or CwScreen.ChallengeList or
                    CwScreen.Difficulty or CwScreen.RewardPopup or
                    CwScreen.CoachSelect or CwScreen.CoachDetail or CwScreen.SignConfirm)
                {
                    SetPhase(CwPhase.Flow); // 被弹回导航链/巡回赛界面：回屏幕驱动重走
                    return 700;
                }
                // 离开活动主页：交给共享比赛流程（起始页/弹窗/比赛内）
                SetPhase(CwPhase.Flow);
                return null;
            }
            case CwPhase.ChallengeList:
                return await HandleChallengeListAsync(cw, ml, obs);
            case CwPhase.ChallengeRetreat:
                return await HandleChallengeRetreatAsync(cw, ml, obs);
            case CwPhase.Retreating:
                return await HandleRetreatingAsync(cw, ml, obs);
            case CwPhase.Handoff:
                return await HandleHandoffAsync(cw, ml);
            default:
                return null;
        }
    }

    /// <summary>
    /// 主页 → 比赛活动列表 的完整导航（一次按完，不靠"下一帧碰巧是比赛页"接力）：
    /// 上×2 进页签行 → 左×4 饱和归位「推荐」→ 右×1「比赛」→ 下×N 进卡行 → 左×M 到
    /// 「比赛活动」→ A 进入。N/M 取策略阶梯（<see cref="HomeCardStrategies"/>）：新版 UI 的
    /// 比赛页卡序/行数与旧版不同时，逐轮换档，而不是把某一版布局写死。
    /// </summary>
    private async Task<int> NavigateToEventListAsync(string reason)
    {
        var (down, left) = HomeCardStrategies[_navTries % HomeCardStrategies.Length];
        _navTries++;
        _log($"[国服周活动] 主页导航（{reason}·第 {_navTries} 次，下×{down} 左×{left}）：" +
             "上×2 → 左×4 归位推荐页签 → 右进比赛页签 → 进卡行选「比赛活动」→ A", LogLevel.Info);
        await _engine.Pad.DpadRepeat(PadDir.Up, 2);
        await _engine.Pad.DpadRepeat(PadDir.Left, 4);
        await _engine.Pad.Dpad(PadDir.Right);
        for (int i = 0; i < down; i++) await _engine.Pad.Dpad(PadDir.Down);
        if (left > 0) await _engine.Pad.DpadRepeat(PadDir.Left, left);
        await _engine.Pad.Confirm();
        return 3200;
    }

    /// <summary>
    /// 落在识别不出的页面（既不是国服主页，也不是活动链上的任何界面）：绝不盲按主页导航
    /// 序列（下/左/A 会把未知页的焦点推到不可控处，最坏开进别的模式），只 B 逃生回上一页；
    /// 连续 6 次仍脱不出去则暂停并提示反馈（同时打一条限流 OCR 原文，便于远程定位这是什么页）。
    /// </summary>
    private async Task<int> EscapeUnknownPageAsync(Observation obs, string why)
    {
        if (_streak < 1) return 700; // 过渡帧防抖：淡入帧不按键
        if ((DateTime.Now - _unknownDumpAt).TotalSeconds > 20)
        {
            _unknownDumpAt = DateTime.Now;
            _log($"[国服周活动] 未知页面 OCR：{string.Join(" | ", obs.Ocr.MergedWords.Select(w => w.Text))}",
                LogLevel.Debug);
        }
        if (++_navUnknown > 6)
        {
            _navUnknown = 0;
            _log($"[国服周活动] 连续 6 次落在无法识别的页面（{why}），暂停 8s；若反复出现请点「反馈」打包日志与截图",
                LogLevel.Error);
            return 8000;
        }
        _log($"[国服周活动] 页面无法识别（{why}）：B 返回上一页重走导航（第 {_navUnknown} 次）", LogLevel.Warn);
        await _engine.Pad.Back();
        return 2400;
    }

    /// <summary>活动列表页（用户 2026-10-17 实机）：列表打开时光标停在「对阵电脑」页签行上，
    /// 先 下 进卡行，再饱和左移×6 归位最左第 1 张活动卡（卡数不固定，到头多按无害），
    /// 主要奖励刷满退场后按 _slotIndex 右移顺延到下一张 → A 进入。多次未进入：槽位 &gt;0 =
    /// 后面已没有活动卡（都刷完了）→ 报成功停止；槽位 0 = B 退回比赛页重导航。</summary>
    private async Task<int> EnterEventAsync()
    {
        if (_streak < 1) return 800; // 过渡帧防抖
        // 卡位上限（硬闸，不依赖 OCR）：卡行末尾之后右移只是饱和，光标永远停在最后一张卡上。
        // 槽位涨过上限 = 后面没有新的活动卡了，直接按全部刷完收尾，否则会在同一张卡上无限打转。
        if (_slotIndex > MaxEventSlots)
            return await FinishAllEventsAsync(
                $"槽位已刷到 {_slotIndex}（卡位上限 {MaxEventSlots}）：活动列表右移不会再换卡，可见活动已全部刷完");
        if (++_attempts > 4)
        {
            if (_slotIndex > 0)
                return await FinishAllEventsAsync($"槽位 {_slotIndex} 多次未进入活动卡：可见活动已全部刷完");
            _log("[国服周活动] 活动列表多次未进入活动，B 退回比赛页重导航", LogLevel.Warn);
            SetPhase(CwPhase.Flow);
            await _engine.Pad.Back();
            return 2400;
        }
        int slot = Math.Clamp(_slotIndex, 0, MaxEventSlots);
        _log(slot > 0
            ? $"[国服周活动] 活动列表：下进卡行 → 左×6 归位 → 右×{slot} 到槽位 {slot} → A 进活动"
            : "[国服周活动] 活动列表：下进卡行 → 左×6 归位第一张 → A 进活动", LogLevel.Info);
        await _engine.Pad.Dpad(PadDir.Down);
        await _engine.Pad.DpadRepeat(PadDir.Left, 6);
        if (slot > 0) await _engine.Pad.DpadRepeat(PadDir.Right, slot);
        await _engine.Pad.Confirm();
        return 2800;
    }

    /// <summary>活动全部刷完（活动列表刷完 / 挑战全部通关）：配置了「全部完成后接力」就先 B 退回
    /// 主页再交班（活动页的词会让目标模块误判界面），否则按原逻辑停止。</summary>
    private async Task<int> FinishAllEventsAsync(string reason)
    {
        _log($"[国服周活动] {reason}", LogLevel.Success);
        if (!string.IsNullOrWhiteSpace(_engine.Config.CnWeekly.AfterCompleteModule))
        {
            SetPhase(CwPhase.Handoff);
            _log("[国服周活动] 已配置全部完成后接力，B 退回主页后交给目标模块", LogLevel.Info);
            await _engine.Pad.Back();
            return 2200;
        }
        _log("[国服周活动] 国服周活动挂机结束", LogLevel.Success);
        _engine.Stop();
        return 0;
    }

    /// <summary>接力准备：B 退出活动链回到主页（两个国服接力模块的主页都在这里）；到主页即交班。
    /// 退出受阻（预算耗尽）也照样交班——目标模块自带残留页 B 逃生，绝不停在原地。</summary>
    private async Task<int> HandleHandoffAsync(CwScreen cw, MatchScreen ml)
    {
        bool neutral = cw is CwScreen.HomeRecommended or CwScreen.HomeMatch || ml == MatchScreen.MainMenu;
        if (neutral || ++_attempts > 8)
        {
            if (_engine.HandoffTo(_engine.Config.CnWeekly.AfterCompleteModule, "国服周活动"))
                return 1500; // 交接完成：下一轮由新模块接管
            _log("[国服周活动] 国服周活动挂机结束", LogLevel.Success);
            _engine.Stop();
            return 0;
        }
        if (cw == CwScreen.RewardPopup || ml == MatchScreen.HasConfirm)
        {
            // 退出途中的弹窗先 B 关闭，多次不消改 A（确认类弹窗）
            if (_attempts <= 4) { await _engine.Pad.Back(); return 1800; }
            await _engine.Pad.Confirm();
            return 1800;
        }
        _log($"[国服周活动] 接力准备：B 退出当前页面（{cw}）", LogLevel.Info);
        await _engine.Pad.Back();
        return 2200;
    }


    private void SetPhase(CwPhase phase)
    {
        _phase = phase;
        _attempts = 0;
    }
}


