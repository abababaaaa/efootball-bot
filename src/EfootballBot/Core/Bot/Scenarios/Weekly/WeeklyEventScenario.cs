using System.IO;
using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Bot.Scenarios.AiLadder;
using EfootballBot.Core.Bot.Scenarios.PeakLadder;

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

namespace EfootballBot.Core.Bot.Scenarios.Weekly;

/// <summary>
/// 周活动挂机场景（国际服）。菜单侧状态机：主菜单 → 比赛页 → 活动板块 → 对阵AI列表
/// → 进入活动（介绍页+入场弹窗连点）→ 活动主页复查主要奖励 → X 核验活动设置
/// （赛事等级=配置值；自动控制强制开、弹性自动控制强制关）→ A 前往比赛。
/// 比赛中流程与我的联赛完全共用：InMatch 随机点击、终场、结算弹窗、满级检测、场次计数。
/// 赛后回到活动主页自动循环，直到主要奖励完成（探针校准前恒按"进行中"继续刷）。
/// 介绍页「进入」（2026-11-09）：光标在「进入」就直接 A，一个方向键都不按；光标被游戏记忆到
/// 「详情」上时先上×1 归位再 A，并按「详情」说明面板当场 B 关闭（见 PressIntroEnterAsync）。
/// 挑战活动新子内容（2026-11-08）：介绍页 →「选择地区」→「选择球队」两页各按一次 Y 随机后
/// 直接 A 下一步（无确认键、不来回随机）→「要使用&lt;队名&gt;吗？」上×1 A 进入 → 挑战清单。
/// 挑战清单上可能盖「挑战N已解锁 / OK」解锁弹窗（2026-11-08 用户实机）：任何阶段命中都
/// 先 A 清弹窗，清完重新归位扫描（见 HandleChallengeUnlockPopupAsync）。
/// 阵容挑战（Team Effort，2026-11-19 用户国际服实机）：活动列表卡片的按钮改叫「继续」，
/// 进入后直接落「阵容挑战主页」，上面必弹一次「球员卡限制 / OK」说明弹窗（固定副标题
/// 「在之前胜场中使用的球员卡,将无法在后续比赛中使用。」）——任何阶段命中都先 A 清掉。
/// 清完后主页按用户规则**跳过活动设置核验**直接 A「前往比赛」开赛（该版主页没有积分/
/// 主要奖励栏，完成态探针待校准，先按进行中继续刷；比赛中流程与其他周活动完全共用）。
/// 阵容挑战的比赛计划失效修复（2026-11-22 用户实机规则）：赛后弹「无法进行比赛 / OK」
/// （比赛计划里有球员卡失效）→ A 继续 → 回主页看底栏「Y 比赛计划」右上角红色叹号 →
/// 有就按 Y 进比赛计划 → 清掉途中弹窗 → 阵容页左移到侧边栏、下选「自动配置球员」→
/// A → 选项弹窗下拉选「重视能力值」→ A 下一步 → B 完成 → 回主页红点消失再开赛。
/// 判定只用固定 UI 词与固定几何（活动名/图片/积分数字每周轮换，禁作判定点）。
/// </summary>
public sealed partial class WeeklyEventScenario : IScenario
{
    /// <summary>菜单侧流程阶段（屏幕驱动；每阶段独立失败预算，超限 B 逃生回 Flow 重导航）。</summary>
    private enum WkPhase
    {
        /// <summary>菜单导航：主菜单→比赛页→活动列表→进入当前槽位活动。</summary>
        Flow,
        /// <summary>活动介绍页 + 入场弹窗（设置专用比赛计划等）连点推进，直到活动主页。</summary>
        IntroPopups,
        /// <summary>活动主页：复查主要奖励 → 已完成拉黑返回 / 未完成按 X 开设置。</summary>
        AtHome,
        /// <summary>活动设置浮层：逐项核验修正（每轮最多一个动作），全部就绪 B 返回。</summary>
        Settings,
        /// <summary>赛事等级下拉已展开：按配置选择（复用我的联赛难度页检测）。</summary>
        LevelDropdown,
        /// <summary>设置完成回活动主页：A 前往比赛，之后交给共用比赛流程。</summary>
        LaunchReady,
        /// <summary>阵容挑战「比赛计划失效」修复（2026-11-22 用户实机）：底栏 Y 比赛计划 →
        /// 清弹窗 → 阵容页左列侧边栏「自动配置球员」→ 选项弹窗选「重视能力值」→ B 完成 →
        /// 回主页确认红色叹号消失。每一步都按屏幕重新判，见内部 <see cref="PlanStep"/>。</summary>
        MatchPlanSetup,
        /// <summary>主要奖励已完成：B 返回活动列表换下一个（预留，校准前不进入）。</summary>
        Retreating,
        /// <summary>挑战活动「挑战清单」：饱和左移归位后逐卡右扫，找第一张
        /// （无锁且无勾且非本运行内已通关/已拉黑）的卡按 A 进入；扫到尾全勾 = 全通关退列表。</summary>
        ChallengeList,
        /// <summary>挑战全部完成/异常放弃：B 退介绍页退活动列表（或 B 后 A 重进清单刷新锁态）。</summary>
        ChallengeRetreat,
        /// <summary>全部活动刷完且配置了接力模块：先 B 退回比赛页/主菜单（活动列表的词会干扰
        /// 目标模块的界面判定，绝不在列表上直接交班），到中性页后换场景继续挂机。</summary>
        Handoff,
    }


    private readonly BotEngine _engine;

    private readonly Action<string, LogLevel> _log;

    private WkPhase _phase = WkPhase.Flow;
    /// <summary>当前阶段累计行动次数（SetPhase 清零；各阶段超限 B 逃生防死循环）。</summary>
    private int _attempts;
    /// <summary>比赛页选活动板块：当前尝试的磁贴格（0=最左锦标赛，1=活动，…每轮起点经饱和
    /// 左移归一，按 A 后屏幕级验证，不对则 B 退回试下一格）。</summary>
    private int _hubStep;
    /// <summary>比赛页整轮扫描（4 格）失败后 B 退主菜单重进的次数。</summary>
    private int _hubRound;
    /// <summary>当前挂机的活动槽位（0 起，按可见卡位计；活动名可变，槽位序号才是稳定标识）。</summary>
    private int _slotIndex;
    /// <summary>已完成拉黑的槽位（预留：主要奖励完成探针校准后才会写入）。</summary>
    private readonly HashSet<int> _doneSlots = new();
    /// <summary>误入比赛历史页后 B 退出的累计次数（仅用于日志升级，动作恒为 B——历史页无危险项）。</summary>
    private int _historyBacks;
    /// <summary>「建议」面板被打开（A 落在左列「建议」上）的累计次数；开赛成功清零。</summary>
    private int _adviceBacks;
    /// <summary>当前是否已经在建议面板里（连续多帧只按一次实证计数）。</summary>
    private bool _adviceOpen;
    /// <summary>开赛前的焦点归位档位：0=直接 A（焦点默认在前往比赛）；1=上×1；2=左×2+上×2；
    /// 3=饱和左×4+上×6。只在实测「A 落到比赛历史页」后递增——游戏记忆了主页焦点位置
    /// （实机 2026-10-26 万圣节活动：光标停在左列「比赛历史」面板，直接 A 永远开不了赛）。
    /// 开赛成功即清零：赛后回主页焦点通常已回前往比赛，无需继续上移。</summary>
    private int _launchNudge;
    /// <summary>最近一次焦点实证是「A 落在建议上」（建议面板被点开）：归位第一档改用上×2，
    /// 而不是历史页验证过的上×1（建议在左列最下，上×1 只到比赛历史面板）。</summary>
    private bool _focusMissAtAdvice;
    /// <summary>已拉黑槽位仍停在介绍页的 B 退出次数（超 3 强制顺延槽位，防退出受阻死循环）。</summary>
    private int _blacklistEscape;
    /// <summary>介绍页「进入」的累计尝试次数（键在「进入」上却依然没进活动 → 下一轮改上移归位，
    /// 见 <see cref="WeeklyBrain.DecideIntroEntry"/>）。进活动主页/回列表时清零。</summary>
    private int _introEnterTries;
    /// <summary>介绍页卡死只转存一次实机帧（远程排障用，避免每次挂机刷几百张图）。</summary>
    private bool _introStuckDumped;


    private string _lastScreenName = "";

    private int _streak;

    private readonly MatchFlowMemory _memory = new();
    /// <summary>赛中匹配（共享层）：开赛/终场判定 + 场内挂机节奏 + 场次记账，五个模块同一份实现。</summary>
    private readonly MatchPlay _matchPlay;
    /// <summary>底层场次跟踪器（模块内读用时/关键比赛等细节用；比赛怎么算由 MatchPlay 决定）。</summary>
    private MatchSessionTracker _matchSession => _matchPlay.Session;
    /// <summary>赛后活动任务页（全屏活动地图）状态机：比赛结束后/开始前出现，随服务器推送轮换。</summary>
    private readonly PostMatchActivityController _postMatch = new();

    private DateTime _lastMaxLevelAt = DateTime.MinValue;

    private bool _takeoverLogged;
    /// <summary>TitleScreen 首次出现时间（比赛上下文中按 X 前 30s 宽限，区分加载画面与真标题）。</summary>
    private DateTime? _titleSince;
    /// <summary>"完成态探针未校准"提示只记一次（右栏读不出主要奖励字样时）。</summary>
    private bool _probeNoteLogged;

    public string Name => "周活动";
    /// <summary>比赛中（供引擎把手柄诊断挡在比赛之外）。</summary>
    public bool InMatch => _matchSession.Active;

    public WeeklyEventScenario(BotEngine engine, Action<string, LogLevel> log)
    {
        _engine = engine;
        _log = log;
        _matchPlay = new MatchPlay(engine, "周活动", log);
        _homeGuide = new ReturnToHomeGuide("周活动", log, engine.DumpSuspiciousFrame);
    }

    /// <summary>启动引导：停在其他模块页时先 B 收敛回主页。</summary>
    private readonly ReturnToHomeGuide _homeGuide;

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var obs = await _engine.ObserveAsync(ct);
        var now = DateTime.Now;
        // 赛后活动任务页（全屏活动地图）优先拦截：不进共享分类（含「下一步」会被判
        // HasConfirm 盲按 A / 活动主页词干扰），由专用状态机按用户规则推进或退出。
        var pmWait = await _postMatch.StepAsync(_engine, obs,
            _engine.Config.PostMatchActivity.AutoAdvanceWeeklyTasks, _log, "周活动", ct);
        if (pmWait is not null) return pmWait.Value;
        // 角区探针兜底 InMatch 需比赛上下文（同我的联赛场景）
        bool corner = ScreenProbe.CornerHasContent(obs.Frame);
        bool light = ScreenProbe.IsMostlyLightPage(obs.Frame);
        // 画面还是球场（进球庆祝/回放过场）→ 共享分类一律按比赛中处理（2026-11-20 虚计场次事故）
        bool pitch = ScreenProbe.LooksLikePitch(obs.Frame);
        bool matchContext = MatchShared.MatchContext(_memory, now);
        // 周活动界面先判：活动主页也含「前往比赛」，不先判会被共享分类误归 LeaguePage。
        // 命中周活动界面时 ml 记 Unknown（由状态机处理，不走共享决策）
        var wk = WeeklyBrain.Classify(obs.Detections, obs.Ocr, corner, matchContext, light);
        var ml = wk == WkScreen.None
            ? MatchBrain.Classify(obs.Detections, obs.Ocr, corner, matchContext, light,
                MatchShared.LoginRecovery(_memory, now), pitch)
            : MatchScreen.Unknown;
        _memory.Record(ml, now);
        // 启动引导：停在其他模块页（我的联赛/天梯/巅峰赛）时先 B 收敛回主页，
        // 否则共享分类会把该页当"前往比赛"直接开赛，导致从别的模块页启动周活动到不了。
        // ★双守卫：
        // 1) 已处于比赛上下文或共用比赛流程（开赛页/比赛中/过场弹窗/终场）时不触发引导，立即放行清零；
        // 2) LeaguePage 只有真的含我的联赛专有词（LooksLikeLeagueHome）才算我的联赛残留页，
        //    避免把周活动点「前往比赛」后的纯 btn_back 过渡页（MatchBrain 兜底成 LeaguePage）误踢回主页。
        bool inMatchFlow = matchContext || _matchSession.Active || ReturnToHomeGuide.IsSharedMatchFlow(ml);
        bool myLeagueResidual = wk == WkScreen.None &&
                                (ml is MatchScreen.PointsPage or MatchScreen.ItemList ||
                                 (ml == MatchScreen.LeaguePage && MyLeagueBrain.LooksLikeLeagueHome(obs.Ocr)));
        bool otherModule = !inMatchFlow &&
                           (AiLadderBrain.Classify(obs.Ocr) != AlScreen.None
                            || PeakLadderBrain.Classify(obs.Ocr, obs.Detections) != PlScreen.None
                            || myLeagueResidual);
        bool ownPage = wk != WkScreen.None || inMatchFlow;
        int? guideWait = await _homeGuide.StepAsync(obs.Frame,
            ml == MatchScreen.MainMenu, ownPage, otherModule,
            () => _engine.Pad.Back());
        if (guideWait is not null) return guideWait.Value;
        if (!_takeoverLogged)
        {
            _takeoverLogged = true;
            _log($"[周活动] 智能接管：当前界面「{(wk != WkScreen.None ? wk.ToString() : ml.ToString())}」，从该阶段继续", LogLevel.Success);
        }
        // ---- 赛中匹配（共享层 MatchPlay）：开赛/终场/超时判定 + 场次与关键比赛记账
        var tick = _matchPlay.Tick(ml, now,
            sawKeyMatchHint: ml == MatchScreen.KeyMatchDialog || obs.Ocr.ContainsJoined("关键比赛"),
            isHome: false);
        if (tick.Started)
        {
            _launchNudge = 0; // 开赛成功：焦点归位档位不再需要（赛后回主页焦点通常回前往比赛）
            _adviceBacks = 0;
            _focusMissAtAdvice = false;
        }
        if (tick.Ended && tick.EndedTrusted)
        {
            var wkCfg = _engine.Config.Weekly;
            if (wkCfg.StopAfterMatches > 0 && _engine.ModuleMatchesDone >= wkCfg.StopAfterMatches)
            {
                _log($"[周活动] 已完成设定场次（{_engine.ModuleMatchesDone}/{wkCfg.StopAfterMatches} 场），自动停止挂机", LogLevel.Success);
                _engine.Stop();
                return 0;
            }
        }
        // ---- 共用：球员满级提示（60s 节流，常开） ----
        if (obs.Ocr.ContainsJoined("满级") && (now - _lastMaxLevelAt).TotalSeconds > 60)
        {
            _lastMaxLevelAt = now;
            _engine.Stats.PlayersMaxed++;
            _log($"[周活动] 球员满级提示（累计 {_engine.Stats.PlayersMaxed} 次）", LogLevel.Success);
            _engine.ReportMaxLevel("比赛中满级提示");
        }
        string screenName = wk != WkScreen.None ? $"周活动.{wk}" : ml.ToString();
        if (screenName == _lastScreenName) _streak++;
        else { _streak = 0; _lastScreenName = screenName; _log($"[周活动] 界面：{screenName}", LogLevel.Info); }
        // ---- 周活动菜单侧状态机：接管周活动界面与阶段内无特征界面 ----
        int? weeklyWait = await HandleWeeklyAsync(wk, ml, obs);
        if (weeklyWait is > 0) return weeklyWait.Value;
        if (weeklyWait is not null) return 1500;
        // wk==None 且阶段已交回共享流程：走共用决策（比赛中/终场/弹窗/逃生）
        var action = MatchBrain.Decide(ml, obs.Ocr, _streak, matchContext, obs.Frame);
        MatchShared.DecisionSnapshot(_log, "周活动", ml, _streak, action, obs.Ocr);
        // 标题画面宽限（共享实现，含赛后弹窗链 90s 档）：比赛上下文中的「eFootball™+X」帧
        // 多为加载画面；赛后品牌过场会连续 30s+（实机 2026-10-12：全场结束后 33s 处按 X
        // 打断了弹窗链）。真实标题画面停留超宽限期后照常按 X。
        if (action.Kind != MatchActionKind.PadXPress)
        {
            _titleSince = null;
        }
        else if (MatchShared.TitleGrace(_memory, now, ref _titleSince, _log, "周活动"))
        {
            return 2000;
        }
        switch (action.Kind)
        {
            case MatchActionKind.PadEnterMatch:
                // 主菜单进比赛卡：下×2 进底部卡行 → OCR 定位"比赛"格位后精确右移；读不全回退左×6
                await _engine.Pad.DpadRepeat(PadDir.Down, 5); // 饱和下按到底部卡行（到底停住，防下少了按错）
                int matchOff = HubTileLocator.FindMainMenuMatchOffset(obs.Ocr) ?? 0;
                if (matchOff > 0)
                {
                    _log($"[周活动] 主菜单导航：下×5→归位最左→右×{matchOff}→A（OCR 定位比赛在第 {matchOff + 1} 格）", LogLevel.Info);
                    await _engine.Pad.DpadRepeat(PadDir.Left, 6);
                    await _engine.Pad.DpadRepeat(PadDir.Right, matchOff);
                }
                else
                {
                    _log("[周活动] 主菜单导航：下×5→左×6→A（OCR 未读全底部磁贴，回退饱和归位）", LogLevel.Info);
                    _engine.DumpSuspiciousFrame(obs.Frame, "menu_tile_ocr_incomplete", "主菜单底部磁贴 OCR 读不全，回退固定左×6 导航");
                    await _engine.Pad.DpadRepeat(PadDir.Left, 6);
                }
                await _engine.Pad.Confirm();
                break;
            case MatchActionKind.PadConfirm:
                _log($"[周活动] 手柄 A：{action.Target}", LogLevel.Info);
                await _engine.Pad.Confirm();
                break;
            case MatchActionKind.PadUpConfirm:
                _log($"[周活动] 弹窗上移确认：{action.Target}", LogLevel.Info);
                await _engine.Pad.Dpad(PadDir.Up);
                await Task.Delay(250); // 上移后留时给游戏刷新焦点，否则A落在旧焦点(取消)
                await _engine.Pad.Confirm();
                break;
            case MatchActionKind.PadDownConfirm:
                _log($"[周活动] 弹窗下移确认：{action.Target}", LogLevel.Info);
                await _engine.Pad.Dpad(PadDir.Down);
                await _engine.Pad.Confirm();
                break;
            case MatchActionKind.PadStartPress:
                _log($"[周活动] {action.Target}", LogLevel.Info);
                await _engine.Pad.Start();
                break;
            case MatchActionKind.PadXPress:
                _log($"[周活动] {action.Target}", LogLevel.Info);
                await _engine.Pad.X();
                break;
            case MatchActionKind.PadLoginNav:
                // eFootball 2027 标题页登录序列：下→右→A（A 落在「登录」上，用户实机序列）
                _log($"[周活动] {action.Target}", LogLevel.Info);
                await _engine.Pad.Dpad(PadDir.Down);
                await _engine.Pad.Dpad(PadDir.Right);
                await _engine.Pad.Confirm();
                break;
            case MatchActionKind.GamepadBack:
                _log($"[周活动] {action.Target}", LogLevel.Warn);
                await _engine.Pad.Back();
                break;
            case MatchActionKind.GamepadBackAfterConfirm:
                _log($"[周活动] {action.Target}", LogLevel.Warn);
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

    // ---------------- 菜单侧状态机 ----------------
    /// <summary>返回正数/0=本轮已处理（下一轮延时）；null=未接管（走共享决策）。</summary>
    private async Task<int?> HandleWeeklyAsync(WkScreen wk, MatchScreen ml, Observation obs)
    {
        // 建议面板：任何阶段命中都先关掉（面板是纯展示层，盖在主页上会让主页探针全部失效）。
        // B 每次只退一级：详情视图 → 列表视图 → 活动主页，所以每观测到面板就按一次 B，
        // 由下一轮继续判（实机 2026-10-28 反馈包：旧逻辑被抢判 EventHome，A/X 全打空，
        // 「多次未生效」的 B 只把详情退成列表，退不回主页 → 三十轮原地打转）。
        // 开赛阶段打开建议面板 = 那记 A 落在左列「建议」上（焦点不在前往比赛）——记一次实证
        // 让 LaunchReady 回升焦点归位档位，下次 A 前先把焦点往上挪。
        // 一次误按会连出两帧（详情 + 列表），只在「刚进面板」那一帧记实证，避免档位跳级。
        if (wk == WkScreen.AdvicePanel)
        {
            if (!_adviceOpen)
            {
                _adviceOpen = true;
                if (_phase == WkPhase.LaunchReady)
                {
                    if (_launchNudge < 3) _launchNudge++;
                    _focusMissAtAdvice = true;
                }
                _adviceBacks++;
                var lvl = _adviceBacks > 3 ? LogLevel.Warn : LogLevel.Info;
                _log($"[周活动] 建议面板盖在主页上（A 按在「建议」上，第 {_adviceBacks} 次），B 关闭（焦点归位档位 {_launchNudge}）", lvl);
            }
            await _engine.Pad.Back();
            return 1600;
        }
        _adviceOpen = false;
        // 阵容挑战入场弹窗「球员卡限制 / OK」（2026-11-19 用户国际服实机）：进入活动后必弹
        // 一次，盖在阵容挑战主页上，只有一枚 OK。任何阶段命中都在这里 A 掉——弹窗背后主页词
        //（前往比赛 / 赛程 / 赛事等级）仍会被 OCR 读到，落主页分支会去按 X/前往比赛，
        // 全部被单键弹窗吃掉（旧逻辑在这里干等到预算耗尽再 B 退回列表，整夜零场次）。
        if (wk == WkScreen.PlayerCardRestrictPopup)
            return await HandlePlayerCardRestrictAsync();
        _squadPopupPresses = 0; // 弹窗已不在屏上：连按计数归零
        // 赛后「无法进行比赛」弹窗（2026-11-22 用户实机）：点 A 继续即回活动主页；真正的修复
        // 在主页的红点探针——关掉弹窗后直接回 MatchPlanSetup 重新确认红点，再按 Y 修阵容。
        // ★任何阶段命中都接管：弹窗盖在主页上，主页词全可读，落主页分支会去按 X/前往比赛，
        // 被单键弹窗吃掉（旧逻辑即在此处整夜「弹窗 → 前往比赛 → 弹窗」零场次）。
        if (wk == WkScreen.MatchPlanInvalidPopup)
            return await HandleMatchPlanInvalidAsync();
        _planInvalidPresses = 0; // 弹窗已不在屏上：连按计数归零
        // 比赛计划编辑页 / 其「自动配置球员」选项弹窗（2026-11-22 用户实机）：正常只在
        // MatchPlanSetup 里出现；半途启动（人工点进比赛计划后才开挂机）也从这里接上。
        // ★吸收上游 v7.4.1 修复：只在「还没进修复子流程」时接管一次；已经在 MatchPlanSetup
        // 里必须把这一帧放回下面的 phase 分派（HandleMatchPlanSetupAsync → PlanStep 屏幕驱动）。
        // 旧写法无条件接管并 return，每帧都把 _planStep 重置成 Lineup —— 手柄方向键发不出去，
        // 屏幕永远停在编辑页，日志每秒刷「接管比赛计划页」（用户实测卡一整天、零场次）。
        bool planEditorOnScreen = wk is WkScreen.SquadLineupEdit or WkScreen.AutoConfigOptions;
        if (WeeklyBrain.ShouldTakeOverPlanEditor(planEditorOnScreen, _phase == WkPhase.MatchPlanSetup))
        {
            SetPhase(WkPhase.MatchPlanSetup);
            _planStep = wk == WkScreen.AutoConfigOptions ? PlanStep.Options : PlanStep.Lineup;
            _planBlindStep = 0;
            _log($"[周活动·阵容挑战] 接管比赛计划页（{wk}）：按侧边栏「自动配置球员」修复阵容", LogLevel.Info);
            return 900;
        }
        // 挑战解锁弹窗「挑战N已解锁 / OK」（2026-11-08 国际服实机）：盖在挑战清单上，只有一个
        // OK 键。任何阶段命中都在这里接管——弹窗半透明盖层下清单标题/卡号/底栏仍在，落清单
        // 分支会让归位/移卡全被吃掉，卡位锁探针还会被弹窗浅灰底误判成锁定 → 回撤退 B 退不掉
        // → 清单↔弹窗死循环（用户反馈：识别不到这个弹窗）。
        if (wk == WkScreen.ChallengeUnlockPopup)
            return await HandleChallengeUnlockPopupAsync();
        // 比赛历史页：任何阶段误入都先 B 退回活动主页（该页无可推进项，交给共享决策会被
        // 误检的 btn_confirm 拖成「盲按 A 三十轮再 B 逃生」的死循环，实机 2026-10-26 反馈包）。
        // 真正把焦点修好的动作在 LaunchReady 的归位里——退出历史页后游戏把焦点还给
        // 「比赛历史」面板，不回位下一次 A 还会再点开它。
        if (wk == WkScreen.MatchHistory)
        {
            // 开赛阶段进历史页 = 刚才那记 A 按在「比赛历史」面板上（焦点不在前往比赛），
            // 记一次实证让 LaunchReady 回升归位档位，下次 A 前先把焦点挪回前往比赛
            if (_phase == WkPhase.LaunchReady && _launchNudge < 3) _launchNudge++;
            if (++_historyBacks > 4)
                _log("[周活动] 反复落在比赛历史页，B 退回活动主页后重新归位（若一直出现请回报日志）", LogLevel.Warn);
            _log($"[周活动] 误入比赛历史页，B 退回活动主页（焦点归位档位 {_launchNudge}）", LogLevel.Info);
            await _engine.Pad.Back();
            return 2200;
        }
        // 介绍页「详情」说明面板（2026-11-09 国际服实机）：介绍页那记 A 落在「详情」上时打开的是
        // 浅色文档面板（右上只有「关闭」、没有「进入」），面板里没有任何 A 能推进的东西。
        // 旧逻辑把它判成 LeagueInfoPage → 干等 15 帧（约 30s）才 B 退回介绍页，退回来光标还在
        // 「详情」上 → 又 A 又开面板，整夜零场次（日志 EventIntro ↔ LeagueInfoPage 每 30s 一轮）。
        // 这里当场 B 关闭，并把「按 A 进不去」的实证记进介绍页决策：下一轮改成上移归位。
        if (wk == WkScreen.None && WeeklyBrain.IsIntroDetailPanel(obs.Ocr))
        {
            _introEnterTries = Math.Max(_introEnterTries, WeeklyBrain.IntroEnterTriesBeforeNudge);
            _log("[周活动] 打开的是介绍页「详情」说明面板（页内无「进入」按钮），B 关闭回介绍页，" +
                 "下一轮先上移把光标对准「进入」", LogLevel.Warn);
            await _engine.Pad.Back();
            return 2200;
        }
        // 挑战活动新增前置流程（2026-11-08 国际服「Road to CAF AFCON 2027」子内容）：
        // 介绍页按进入 → 选择地区 → 选择球队 →「要使用<队名>吗？」确认弹窗 → 正常挑战清单。
        // 三页都没有推进词（只有底栏 B 返回 / Y 随机，弹窗只有 进入 + 取消），落共享决策会被
        // 判 Unknown 原地干等，所以任何阶段命中都接管；半途启动直接落在这些页时同样接得上。
        // 用户规则（2026-11-08）：两页随机各按一次就够，不来回点；没有确认键，随机按完直接
        // 下一步（A）——见 HandleTeamPickAsync / WeeklyBrain.DecidePickStep。
        if (wk == WkScreen.TeamConfirm)
            return await HandleTeamConfirmAsync();
        if (wk is WkScreen.RegionSelect or WkScreen.TeamSelect)
            return await HandleTeamPickAsync(wk);
        _pickAttempts = 0;
        _pickRandomPressed = false;
        _pickSamePageFrames = 0;
        _pickPage = WkScreen.None;
        _pickEscapes = 0;
        _teamConfirmAttempts = 0;
        // 单场周活动「选择赛事等级」页（2026-10-30 用户截图 + issue #1）：任何阶段命中都接管，
        // 旧逻辑该页 wk=None/ml=DifficultyPage 无人处理 → 原地干等。半途启动/标题被遮挡
        //（issue #1 手机照：GUI 覆盖层盖住左列）时本判定只剩 ml=DifficultyPage 一个信号，
        // 一并接管；LevelDropdown 阶段的 ml=DifficultyPage 是设置浮层下拉样式，交给原逻辑。
        if (wk == WkScreen.EventDifficulty ||
            (_phase != WkPhase.LevelDropdown && ml == MatchScreen.DifficultyPage))
            return await HandleEventDifficultyAsync(obs);
        switch (_phase)
        {
            case WkPhase.Flow:
            {
                // 大活动页第一格「锦标赛」被 A 打开（2026-11-13 用户实机）：该页也有
                // 「进入 / 详情」两枚按钮，会被 Classify 误判成活动介绍页——旧逻辑随即 A
                // 打开「活动简介」、再 A 打开详情说明面板、B 回来又 A（反馈包
                // 20261112_233710 里 23:33 起每 2~3 秒一轮、零场次的那条链）。
                // 识别出锦标赛特征就 B 退回比赛页，下一轮按磁贴序号改试「活动」格。
                if (_hubStep > 0 && wk == WkScreen.EventIntro && WeeklyBrain.IsTournamentPage(obs.Ocr))
                {
                    _log("[周活动] 打开的是大活动页第 1 格「锦标赛」页面（不是活动介绍页），B 退回比赛页改试下一格", LogLevel.Warn);
                    await _engine.Pad.Back();
                    return 2400;
                }
                switch (wk)
                {
                    case WkScreen.EventsList:
                        // 活动列表 = 本模块的"菜单"：解禁下一场（结算链里的比赛帧误判不算新场）
                        _matchSession.MarkAtMenu();
                        return await EnterNextEventAsync(obs);
                    case WkScreen.EventIntro:
                    case WkScreen.EntryPopup:
                        // 已拉黑槽位（挑战全完成/已放弃）绝不再按 A 重进：B 退出为主，
                        // 多次仍在介绍页（退出受阻，issue #1 实机）就强制顺延槽位，
                        // 回到列表后由 EnterNextEventAsync 的拉黑跳过正常换下一个。
                        if (_doneSlots.Contains(_slotIndex))
                        {
                            if (++_blacklistEscape > 3)
                            {
                                _blacklistEscape = 0;
                                _challengeMode = false;
                                _slotIndex++;
                                _log($"[周活动] 已拉黑槽位 {_slotIndex - 1} 仍停在介绍页，强制顺延改刷槽位 {_slotIndex}", LogLevel.Warn);
                            }
                            else
                            {
                                _log($"[周活动] 已拉黑槽位 {_slotIndex} 的介绍页，B 退出不重进", LogLevel.Warn);
                            }
                            await _engine.Pad.Back();
                            return 2200;
                        }
                        _hubStep = 0; // 已进入活动链：比赛页扫描结束（防赛后误触发扫描分支）
                        _hubRound = 0;
                        SetPhase(WkPhase.IntroPopups);
                        if (wk == WkScreen.EventIntro) return await PressIntroEnterAsync(obs);
                        return await HandlePopupAsync(obs, "入场弹窗");
                    case WkScreen.EventHome:
                    case WkScreen.TeamEffortHome: // 阵容挑战主页：同一层级的活动主页（版式不同）
                        _hubStep = 0;
                        _hubRound = 0;
                        if (_streak < 1) return 800; // 过渡帧防抖：主页稳定一拍再开始判断
                        SetPhase(WkPhase.AtHome);
                        return 600; // 下一轮做主要奖励复查
                    case WkScreen.EventSettings:
                        _hubStep = 0;
                        _hubRound = 0;
                        SetPhase(WkPhase.Settings);
                        return 800;
                    case WkScreen.ChallengeList:
                        // 半途启动直接落在挑战清单（或异常态回到 Flow 时清单还开着）
                        _hubStep = 0;
                        _hubRound = 0;
                        return await EnterChallengeListPhaseAsync();
                }
                if (ml == MatchScreen.MainMenu)
                {
                    // 导航多轮无效不再永久等待（≥NavEscapeStreak 周期性 B 清盖层，共享决策同款）
                    if (_streak >= MatchBrain.NavEscapeStreak && (_streak - MatchBrain.NavEscapeStreak) % 5 == 0)
                    {
                        _log("[周活动] 主菜单导航多轮无效，B 清盖层/回上级重试", LogLevel.Warn);
                        await _engine.Pad.Back();
                        return 2400;
                    }
                    await _engine.Pad.DpadRepeat(PadDir.Down, 5); // 饱和下按到底部卡行（到底停住，防下少了按错）
                    int flowMatchOff = HubTileLocator.FindMainMenuMatchOffset(obs.Ocr) ?? 0;
                    if (flowMatchOff > 0)
                    {
                        _log($"[周活动] 主菜单导航：下×5→归位最左→右×{flowMatchOff}→A（OCR 定位比赛在第 {flowMatchOff + 1} 格）", LogLevel.Info);
                        await _engine.Pad.DpadRepeat(PadDir.Left, 6);
                        await _engine.Pad.DpadRepeat(PadDir.Right, flowMatchOff);
                    }
                    else
                    {
                        _log("[周活动] 主菜单导航：下×5→左×6→A（OCR 未读全底部磁贴，回退饱和归位）", LogLevel.Info);
                        _engine.DumpSuspiciousFrame(obs.Frame, "menu_tile_ocr_incomplete", "主菜单底部磁贴 OCR 读不全，回退固定左×6 导航");
                        await _engine.Pad.DpadRepeat(PadDir.Left, 6);
                    }
                    await _engine.Pad.Confirm();
                    return 3000;
                }
                if (ml == MatchScreen.MatchHub)
                {
                    if (_streak >= MatchBrain.StuckStreak) return 5000;
                    // 【识别重做同款模式】国际服比赛页是 4 卡横排、无预览区，磁贴标签词
                    // 聚焦/非聚焦尺寸几乎无差（实机帧：锦标赛聚焦 H=0.058 vs 活动 0.056），
                    // OCR 判焦点不可行——改为「饱和左移归一起点 → 右×N 试第 N 格 → 按 A 后
                    // 屏幕级验证」，不对则 B 退回换下一格（见下方扫描途中分支）。
                    if (_hubStep > 3)
                    {
                        _hubRound++;
                        if (_hubRound > 2)
                        {
                            _log("[周活动] 比赛页多轮未进入活动板块，暂停 30s 后重扫（若反复出现请回报日志）", LogLevel.Error);
                            _hubStep = 0;
                            _hubRound = 0;
                            return 30000;
                        }
                        _log("[周活动] 活动板块扫描一轮未中，B 退出由主菜单重进（重进后光标回默认位）", LogLevel.Warn);
                        _hubStep = 0;
                        await _engine.Pad.Back();
                        return 2500;
                    }
                    // ★2026-11-13 用户实机：大活动页第一格改成了「锦标赛」（Round 3 Ranking
                    // Event / 参加锦标赛形式的活动），活动板块退到第二格。旧逻辑从最左第 1 格
                    // 盲扫，第一记 A 必进锦标赛页，再被误判成活动介绍页 → 「活动简介 → 详情
                    // 说明面板」每 2~3 秒一轮、整夜零场次（反馈包 20261112_233710）。
                    // 磁贴标题词 OCR 能直接给出「活动」在第几格，优先按它定向；读不全再逐格扫。
                    if (_hubStep == 0)
                    {
                        int? activityTile = WeeklyBrain.FindActivityTileOffset(obs.Ocr);
                        if (activityTile is int offset)
                        {
                            _log($"[周活动] 比赛页：磁贴行识别到「活动」在第 {offset + 1} 格" +
                                 (offset > 0 ? "（第 1 格是锦标赛，跳过不按）" : "") +
                                 $" → 归位最左后右×{offset} → A", LogLevel.Info);
                            await _engine.Pad.DpadRepeat(PadDir.Left, 6);
                            if (offset > 0) await _engine.Pad.DpadRepeat(PadDir.Right, offset);
                            await _engine.Pad.Confirm();
                            // 定向失败（弹窗吃掉/仍是比赛页）时从下一格继续扫，绝不重复按同一格
                            _hubStep = offset + 1;
                            return 2800;
                        }
                    }
                    _log($"[周活动] 比赛页：归位最左后右×{_hubStep} → A（试第 {_hubStep + 1}/4 格找活动板块）", LogLevel.Info);
                    await _engine.Pad.DpadRepeat(PadDir.Left, 6);
                    if (_hubStep > 0) await _engine.Pad.DpadRepeat(PadDir.Right, _hubStep);
                    await _engine.Pad.Confirm();
                    _hubStep++; // 失败（B 回比赛页）时下一轮试下一格；成功由 EnterNextEventAsync 清零
                    return 2800;
                }
                // 弹窗（奖励到账 / 物品已送到收件箱 / 机会交易 / 比赛回放…）：不管 _hubStep 是多少，
                // 一律先按弹窗规则推进，绝不 B。★2026-11-09 夜间事故根因：_hubStep 是上一轮比赛页
                // 扫描残留的值，HasConfirm 恰好落进下面那条「其他板块页 → B」规则，而单键弹窗
                // （「物品已送到收件箱 / OK」）按 B 没有任何反应 → 每 2.2s 一次 B 空转整夜，
                // 日志「打开的是其他板块页面（HasConfirm）」刷屏 6 小时、零场次。
                if (wk == WkScreen.None && ml == MatchScreen.HasConfirm)
                {
                    _hubStep = 0;
                    _hubRound = 0;
                    return await HandlePopupAsync(obs, "弹窗");
                }
                // 扫描途中按 A 打开了别的板块页面（锦标赛/eFootball联赛/我的联赛子页等）：
                // 不是活动列表就 B 退回比赛页，下一轮换下一格（不落共享决策乱按）
                if (_hubStep > 0 && wk == WkScreen.None &&
                    ml is MatchScreen.LeaguePage or MatchScreen.PointsPage or MatchScreen.ItemList)
                {
                    _log($"[周活动] 打开的是其他板块页面（{ml}），B 退回比赛页继续扫描", LogLevel.Info);
                    await _engine.Pad.Back();
                    return 2200;
                }
                return null; // 其余共享界面（比赛中/弹窗/未知逃生）交给共用决策
            }
            case WkPhase.IntroPopups:
            {
                // 阵容挑战没有「进入/详情」介绍页：列表那记「继续」直接落到主页（上面盖着
                // 「球员卡限制」弹窗，已由最前面的拦截清掉），所以主页判定要连它一起收。
                if (wk is WkScreen.EventHome or WkScreen.TeamEffortHome)
                {
                    _introEnterTries = 0;   // 已进活动主页：介绍页的按键实证作废
                    _introStuckDumped = false;
                    SetPhase(WkPhase.AtHome);
                    return 600;
                }
                if (wk == WkScreen.EventsList) { SetPhase(WkPhase.Flow); return 800; }
                // 单场活动/特殊活动从列表进入后不经过活动主页直接进开赛页或比赛中：立即切回 Flow 交还共享比赛流程
                if (ml is MatchScreen.MatchStartPage or MatchScreen.InMatch)
                {
                    _introEnterTries = 0;
                    _introStuckDumped = false;
                    SetPhase(WkPhase.Flow);
                    return null;
                }
                if (wk == WkScreen.EventIntro) return await PressIntroEnterAsync(obs);
                if (wk == WkScreen.ChallengeList)
                    return await EnterChallengeListPhaseAsync(); // 挑战活动：介绍页按进入后弹的是挑战清单
                if (wk == WkScreen.EntryPopup || ml == MatchScreen.HasConfirm)
                    return await HandlePopupAsync(obs, "入场弹窗");
                if (++_attempts > 15)
                {
                    // 比赛上下文中的过场（加载/品牌帧）可能连续超过预算：B 在比赛附近危险
                    //（铲球/误退出），只继续等待；菜单上下文才 B 退回列表重试
                    bool nearMatch = _memory.SawWithin(MatchScreen.InMatch, TimeSpan.FromMinutes(2), DateTime.Now) ||
                                     _memory.SawWithin(MatchScreen.MatchStartPage, TimeSpan.FromMinutes(2), DateTime.Now);
                    if (!nearMatch)
                    {
                        _log($"[周活动] 介绍页/入场弹窗长时间未结束（{wk} / {ml}），B 退回列表重试：" +
                             OcrDigest(obs.Ocr), LogLevel.Warn);
                        SetPhase(WkPhase.Flow);
                        await _engine.Pad.Back();
                        return 2200;
                    }
                }
                // 卡在既非主页也非弹窗的页面上：第 6 帧转存一次实机帧 + OCR 原文，
                // 下一份反馈包就能直接看到那个"认不出来"的页面长什么样（远程排障用）。
                if (_attempts == 6 && !_introStuckDumped)
                {
                    _introStuckDumped = true;
                    _log($"[周活动] 介绍页推进受阻（{wk} / {ml}），转存实机帧备查：{OcrDigest(obs.Ocr)}", LogLevel.Warn);
                    DumpLiveFrame(obs.Frame, "wk_intro_stuck");
                }
                // 未识别的入场弹窗：OCR 有推进词就点（同比赛中弹窗规则），否则等待
                if (obs.Ocr.ContainsJoined("下一步") || obs.Ocr.ContainsJoined("OK") ||
                    obs.Ocr.ContainsJoined("确认") || obs.Ocr.ContainsJoined("进入"))
                    return await PressConfirmAsync("入场推进");
                return 1500;
            }
            case WkPhase.AtHome:
            {
                // 阵容挑战（2026-11-19 用户实机规则）：主页没有积分 / 主要奖励栏，进入后直接
                // 点下一步（前往比赛）即可开始比赛。★不做 X 活动设置核验——用户规则明确跳过
                // （该版主页的浮层项未拿到实机帧，盲按 X 可能把焦点打到别处）；
                // 完成态探针（挑战1..3 的进度）留待校准，先按进行中继续刷。
                if (wk == WkScreen.TeamEffortHome)
                {
                    if (!_squadNoteLogged)
                    {
                        _squadNoteLogged = true;
                        _log("[周活动·阵容挑战] 主页：无积分/主要奖励栏（完成态探针未校准），" +
                             "按规则跳过活动设置，直接前往比赛开始比赛", LogLevel.Info);
                    }
                    SetPhase(WkPhase.LaunchReady);
                    return 400;
                }
                if (_challengeMode || WeeklyBrain.IsChallengeCardHome(obs.Ocr))
                    return await HandleChallengeCardHomeAsync(wk, obs);
                if (wk != WkScreen.EventHome)
                {
                    SetPhase(WkPhase.Flow); // X 未开成功等异常：回屏幕驱动
                    return 600;
                }
                if (WeeklyBrain.IsMainRewardDone(obs.Ocr, obs.Frame))
                {
                    //【预留判定，校准前恒 false 不触发】主要奖励已完成：拉黑槽位返回列表
                    _doneSlots.Add(_slotIndex);
                    _log($"[周活动] 活动（槽位 {_slotIndex}）主要奖励已完成，拉黑并返回列表换下一个", LogLevel.Success);
                    SetPhase(WkPhase.Retreating);
                    await _engine.Pad.Back();
                    return 2200;
                }
                if (!WeeklyBrain.MainRewardPending(obs.Ocr) && !_probeNoteLogged)
                {
                    _probeNoteLogged = true;
                    _log("[周活动] 主要奖励完成态探针未校准（右栏未读出「离获得主要奖励」），按进行中继续刷", LogLevel.Info);
                }
                SetPhase(WkPhase.Settings);
                await _engine.Pad.X();
                _log("[周活动] 按 X 打开活动设置（核验赛事等级/自动控制/弹性）", LogLevel.Info);
                return 1800;
            }
            case WkPhase.Settings:
                return await HandleSettingsAsync(wk, ml, obs);
            case WkPhase.LevelDropdown:
            {
                if (ml == MatchScreen.DifficultyPage)
                    return await HandleLevelOptionsAsync(obs.Frame, obs.Ocr);
                // 下拉展开样式未识别（待实机校准）：B 收起回浮层；改选预算在 Settings 侧
                if (++_attempts > 3)
                {
                    _log("[周活动] 赛事等级下拉样式未识别，B 收起（等级保持游戏当前值，待校准截图）", LogLevel.Warn);
                    SetPhase(WkPhase.Settings);
                    await _engine.Pad.Back();
                    return 1800;
                }
                return 1000;
            }
            case WkPhase.LaunchReady:
            {
                if (wk == WkScreen.EventSettings)
                {
                    _log("[周活动] 设置浮层未关闭，按 B 返回", LogLevel.Warn);
                    await _engine.Pad.Back();
                    return 1800;
                }
                if (wk is WkScreen.EventHome or WkScreen.TeamEffortHome ||
                    (_challengeMode && WeeklyBrain.IsChallengeCardHome(obs.Ocr)))
                {
                    // 阵容挑战：主页底栏「Y 比赛计划」上有红色叹号 = 比赛计划里有球员卡失效，
                    // 此时按「前往比赛」只会弹「无法进行比赛」（用户 2026-11-22 实机规则）：
                    // 先进 Y 修阵容（侧边栏自动配置球员 → 重视能力值 → B 完成），红点消失再开赛。
                    if (wk == WkScreen.TeamEffortHome && WeeklyBrain.DetectPlanBadge(obs.Frame, obs.Ocr))
                        return await BeginMatchPlanFixAsync();
                    if (++_attempts > 4)
                    {
                        // 退回列表重进后光标回默认位：归位档位清零，重进后先按直接 A 试
                        _launchNudge = 0;
                        _log("[周活动] 前往比赛多次未生效，B 退回列表重新走流程", LogLevel.Warn);
                        SetPhase(WkPhase.Flow);
                        await _engine.Pad.Back();
                        return 2200;
                    }
                    // 焦点归位（实机 2026-10-26 反馈包：活动主页的焦点被游戏记忆，光标停在左列
                    // 「比赛历史」面板上时直接 A 只会反复点开比赛历史，永远进不了比赛）。
                    // 档位由「A 落到比赛历史页」这一实证递增，绝不无条件饱和上移——主页顶栏
                    // 右上是「任务/LS」胶囊，盲目 Up×6 可能把焦点送上去按开任务页。
                    switch (_launchNudge)
                    {
                        case 1:
                            if (_focusMissAtAdvice)
                            {
                                _log("[周活动] 焦点不在前往比赛（落在左列「建议」上）：上×2 归位后 A", LogLevel.Info);
                                await _engine.Pad.DpadRepeat(PadDir.Up, 2);
                            }
                            else
                            {
                                _log("[周活动] 焦点不在前往比赛（已落到比赛历史）：上×1 归位后 A", LogLevel.Info);
                                await _engine.Pad.Dpad(PadDir.Up);
                            }
                            break;
                        case 2:
                            _log("[周活动] 焦点不在前往比赛：左×2+上×2 归位后 A", LogLevel.Info);
                            await _engine.Pad.DpadRepeat(PadDir.Left, 2);
                            await _engine.Pad.DpadRepeat(PadDir.Up, 2);
                            break;
                        case > 2:
                            // 前两档都无效才用饱和配方（我的联赛/AI天梯主页同款：到头多按无害）
                            _log("[周活动] 焦点不在前往比赛：饱和归位（左×4+上×6）后 A", LogLevel.Info);
                            await _engine.Pad.DpadRepeat(PadDir.Left, 4);
                            await _engine.Pad.DpadRepeat(PadDir.Up, 6);
                            break;
                        default:
                            _log("[周活动] 手柄 A：前往比赛", LogLevel.Info);
                            break;
                    }
                    await _engine.Pad.Confirm();
                    return 3000;
                }
                // 离开活动主页：交给共享比赛流程（起始页/弹窗/比赛内）
                SetPhase(WkPhase.Flow);
                return null;
            }
            case WkPhase.Retreating:
            {
                if (wk == WkScreen.EventsList)
                {
                    _slotIndex++;
                    SetPhase(WkPhase.Flow);
                    _log($"[周活动] 已返回活动列表，改刷槽位 {_slotIndex}", LogLevel.Info);
                    return 800;
                }
                if (wk is WkScreen.EventHome or WkScreen.EventIntro or WkScreen.TeamEffortHome)
                {
                    if (++_attempts <= 3) { await _engine.Pad.Back(); return 2200; }
                    SetPhase(WkPhase.Flow);
                    return 800;
                }
                if (wk == WkScreen.EntryPopup)
                    return await PressConfirmAsync("返回途中弹窗：确认"); // 先清弹窗再回列表
                if (ml == MatchScreen.HasConfirm)
                {
                    // 返回途中的确认框：先 B 尝试取消，3 次后按 A（视为确认退出）
                    if (++_attempts <= 3) { await _engine.Pad.Back(); return 2000; }
                    await _engine.Pad.Confirm();
                    return 2000;
                }
                if (++_attempts > 5) SetPhase(WkPhase.Flow);
                return 1500;
            }
            case WkPhase.ChallengeList:
                return await HandleChallengeListAsync(wk, ml, obs);
            case WkPhase.MatchPlanSetup:
                return await HandleMatchPlanSetupAsync(wk, ml, obs);
            case WkPhase.ChallengeRetreat:
                return await HandleChallengeRetreatAsync(wk, ml);
            case WkPhase.Handoff:
                return await HandleHandoffAsync(wk, ml);
            default:
                return null;
        }
    }

    /// <summary>活动列表页：归位（下进卡行 → 饱和左移到最左=第 1 卡，用户实测"先下再左"）
    /// → 绿勾/已拉黑槽位逐个右移跳过 → A 进入第一个可刷活动。</summary>
    private async Task<int> EnterNextEventAsync(Observation obs)
    {
        _hubStep = 0;
        _hubRound = 0;
        _blacklistEscape = 0;
        _launchNudge = 0; // 重新从列表进入活动：游戏焦点回默认位（前往比赛），归位档位清零
        _focusMissAtAdvice = false;
        _planFixAttempts = 0; // 换了活动/重进：比赛计划修复次数重新计
        await _engine.Pad.Dpad(PadDir.Down);          // 页签行→卡行（已在卡行则到头无害）
        await _engine.Pad.DpadRepeat(PadDir.Left, 6); // 饱和到最左第 1 卡（到头多按无害）
        // 绿勾探针（已完成活动）或本运行内已拉黑：右移跳过。第 4 卡屏幕外探不到勾，
        // 直接进入后由主页完成态判定兜底（完成→B 返回拉黑），不会重复刷
        while (_slotIndex < 4)
        {
            bool blacklisted = _doneSlots.Contains(_slotIndex);
            bool greenCheck = !blacklisted && WeeklyBrain.DetectGreenCheck(obs.Frame, _slotIndex);
            if (!blacklisted && !greenCheck) break;
            if (greenCheck)
            {
                _doneSlots.Add(_slotIndex);
                _log($"[周活动] 卡位 {_slotIndex + 1} 右上角绿勾（主要奖励已完成），跳过不进入", LogLevel.Info);
            }
            await _engine.Pad.Dpad(PadDir.Right);
            _slotIndex++;
        }
        if (_slotIndex >= 4)
            return await FinishAllEventsAsync();
        if (_slotIndex > 0) await _engine.Pad.DpadRepeat(PadDir.Right, _slotIndex);
        _log($"[周活动] 活动列表：进入槽位 {_slotIndex}", LogLevel.Info);
        SetPhase(WkPhase.IntroPopups);
        await _engine.Pad.Confirm();
        return 2800;
    }

    /// <summary>周活动全部刷完（可见槽位全部完成或被跳过）：配置了「全部完成后接力」就先 B 退回
    /// 比赛页/主菜单再交班（活动列表的「活动/前往比赛」等词会让目标模块误判界面），否则停止。</summary>
    private async Task<int> FinishAllEventsAsync()
    {
        _log("[周活动] 可见活动槽位已全部刷完（完成或跳过）", LogLevel.Success);
        if (!string.IsNullOrWhiteSpace(_engine.Config.Weekly.AfterCompleteModule))
        {
            SetPhase(WkPhase.Handoff);
            _log("[周活动] 已配置全部完成后接力，B 退回比赛页后交给目标模块", LogLevel.Info);
            await _engine.Pad.Back();
            return 2200;
        }
        _log("[周活动] 周活动挂机结束", LogLevel.Success);
        _engine.Stop();
        return 0;
    }

    /// <summary>接力准备：B 退出活动链回到比赛页/主菜单，到中性页后把场景换成目标模块。
    /// 退出受阻（预算耗尽）也照样交班——目标模块自带 B 逃生，绝不停在原地。</summary>
    private async Task<int> HandleHandoffAsync(WkScreen wk, MatchScreen ml)
    {
        bool neutral = ml is MatchScreen.MainMenu or MatchScreen.MatchHub;
        if (neutral || ++_attempts > 8)
        {
            if (_engine.HandoffTo(_engine.Config.Weekly.AfterCompleteModule, "周活动"))
                return 1500; // 交接完成：下一轮由新模块接管
            _log("[周活动] 周活动挂机结束", LogLevel.Success);
            _engine.Stop();
            return 0;
        }
        if (wk == WkScreen.EntryPopup)
            return await PressConfirmAsync("退出途中弹窗：确认");
        if (ml == MatchScreen.HasConfirm)
        {
            // 返回途中的确认框：先 B 尝试取消，3 次后按 A（视为确认退出）
            if (_attempts <= 3) { await _engine.Pad.Back(); return 2000; }
            await _engine.Pad.Confirm();
            return 2000;
        }
        _log($"[周活动] 接力准备：B 退出当前页面（{wk}）", LogLevel.Info);
        await _engine.Pad.Back();
        return 2200;
    }


    // ---------------- 阵容挑战：比赛计划失效修复（2026-11-22 用户实机规则） ----------------
    /// <summary>同一处红色叹号最多修几次；超限停止挂机（比赛计划无效时游戏恒定拒绝开赛）。</summary>
    private const int MaxPlanFixAttempts = 3;


    private void SetPhase(WkPhase phase)
    {
        _phase = phase;
        _attempts = 0;
        if (phase == WkPhase.MatchPlanSetup)
        {
            _planStep = PlanStep.Open;      // 新的一次修复：先按 Y，再按屏幕往下走
            _planOptionPicked = false;
            _planBlindStep = 0;             // 盲走配方重新计（上游 v7.4.1）
        }
        if (phase == WkPhase.IntroPopups)
        {
            _introEnterTries = 0;     // 新一次入场：介绍页的按键实证作废
            _introStuckDumped = false;
        }
        if (phase == WkPhase.ChallengeList)
        {
            _chScanReady = false; // 进阶段先饱和左移归位再开始扫
            _chScan = 1;
            _chMissing = 0;
            _chCursorDone = false;
            _chVanishRescans = 0;
        }
    }

    private async Task<int> PressConfirmAsync(string what)
    {
        _log($"[周活动] 手柄 A：{what}", LogLevel.Info);
        await _engine.Pad.Confirm();
        return 2500;
    }

    /// <summary>
    /// 弹窗推进（用户 2026-11-10 规则）：先看弹窗有几个按键——一个按键 → 直接按下一步（A）；
    /// 两个及以上 → 看白底高光那枚是不是 确认/进行/OK/下一步/进入/继续/领取 这类推进键，
    /// 是 → 直接 A；不是（高光在 取消/返回/关闭 上）→ 按上下关系挪一格到推进键再 A。
    /// 决策与像素探针见 <see cref="PopupBrain"/>（纯函数，可单测）。
    /// 认不出按钮词的弹窗退回原来的「有推进词就 A」行为，绝不停在原地。
    /// </summary>
    private async Task<int> HandlePopupAsync(Observation obs, string context)
    {
        var action = PopupBrain.Decide(obs.Frame, obs.Ocr, out var note);
        switch (action)
        {
            case PopupAction.DownThenConfirm:
                _log($"[周活动] {context}：{note}", LogLevel.Info);
                await _engine.Pad.Dpad(PadDir.Down);
                await _engine.Pad.Confirm();
                return 2400;
            case PopupAction.UpThenConfirm:
                _log($"[周活动] {context}：{note}", LogLevel.Info);
                await _engine.Pad.Dpad(PadDir.Up);
                await Task.Delay(250); // 上移后留时给游戏刷新焦点，否则A落在旧焦点(取消)
                await _engine.Pad.Confirm();
                return 2400;
            case PopupAction.PressConfirm:
                _log($"[周活动] {context}：{note}", LogLevel.Info);
                await _engine.Pad.Confirm();
                return 2400;
            default:
                // 按钮词没读到：有推进词（含底栏「前往比赛/开始比赛/下一步」等）就按 A，否则等一拍由上层重判
                if (obs.Ocr.ContainsJoined("下一步") || obs.Ocr.ContainsJoined("OK") ||
                    obs.Ocr.ContainsJoined("确认") || obs.Ocr.ContainsJoined("进入") ||
                    obs.Ocr.ContainsJoined("继续") || obs.Ocr.ContainsJoined("前往比赛") ||
                    MatchBrain.FindBottomPushWord(obs.Ocr) is not null)
                {
                    _log($"[周活动] {context}：{note}，OCR 有推进词 → A", LogLevel.Info);
                    await _engine.Pad.Confirm();
                    return 2400;
                }
                _log($"[周活动] {context}：{note}，暂不按键等一拍重判", LogLevel.Debug);
                return 1200;
        }
    }

    /// <summary>
    /// 活动介绍页「进入」（用户 2026-11-09 国际服规则：光标默认在「进入」，直接点下一步即可，
    /// 介绍页不该有任何方向键）。游戏会把上一页的光标位置记忆下来——点过「详情」后光标停在
    /// 详情上，直接 A 只会打开灰色说明面板，面板里没有能推进的按钮，退回来光标还在详情上 →
    /// 整夜在介绍页打转。所以先做像素级焦点探针（<see cref="WeeklyBrain.DetectFocusedIntroButton"/>）：
    /// · 光标在「进入」→ 直接 A，一个方向键都不按；
    /// · 光标在「详情」→ 上×1 归位到「进入」再 A（绝不往下点）；
    /// · 探针读不出 → 先按用户默认直接 A，连续 <see cref="WeeklyBrain.IntroEnterTriesBeforeNudge"/>
    ///   次没推进再改上移归位，绝不对同一页无限重复同一个无效动作。
    /// </summary>
    private async Task<int> PressIntroEnterAsync(Observation obs)
    {
        var focus = WeeklyBrain.DetectFocusedIntroButton(obs.Frame, obs.Ocr);
        var step = WeeklyBrain.DecideIntroEntry(focus, _introEnterTries);
        _introEnterTries++;
        if (step == WeeklyBrain.WkIntroAction.PressEnter)
        {
            string why = focus == WeeklyBrain.WkIntroFocus.Enter ? "白底高亮" : "聚焦探针未读出，按默认光标在「进入」";
            _log($"[周活动] 介绍页：光标在「进入」（{why}）→ 直接 A，不按方向键", LogLevel.Info);
            await _engine.Pad.Confirm();
            return 2500;
        }
        _log($"[周活动] 介绍页：光标在「详情」（第 {_introEnterTries} 次尝试）→ 上×1 归位到「进入」后 A", LogLevel.Warn);
        await _engine.Pad.Dpad(PadDir.Up);
        await _engine.Pad.Confirm();
        return 2600;
    }

    /// <summary>OCR 行摘要（排障用，截断防日志膨胀）。</summary>
    private static string OcrDigest(OcrResult ocr)
    {
        var text = string.Join(" | ", ocr.Lines.Select(l => l.Text.Replace(" ", "")));
        return text.Length <= 320 ? text : text[..320];
    }

    /// <summary>
    /// 实机帧转存（远程排障用，失败静默）：写 data\dump\{tag}_时间戳.png。
    /// 场景：某个页面判定在离线帧上对、实机上却认不出——只有真帧才能校准。
    /// </summary>
    private void DumpLiveFrame(FrameData f, string tag)
    {
        var pixels = (byte[])f.Bgra.Clone(); // 帧池复用数组：观察线程先拷贝，后台只做编码
        string dir = Path.Combine(EfootballBot.Core.System.AppPaths.DataDir, "dump");
        string file = Path.Combine(dir, $"{tag}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(dir);
                var info = new SkiaSharp.SKImageInfo(f.Width, f.Height,
                    SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
                unsafe
                {
                    fixed (byte* p = pixels)
                    {
                        using var bmp = new SkiaSharp.SKBitmap();
                        if (!bmp.InstallPixels(info, (nint)p, info.RowBytes)) return;
                        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
                        using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                        using var fs = File.OpenWrite(file);
                        data.SaveTo(fs);
                    }
                }
                _log($"[周活动] 已存实机帧：{file}", LogLevel.Info);
            }
            catch { }
        });
    }
}
