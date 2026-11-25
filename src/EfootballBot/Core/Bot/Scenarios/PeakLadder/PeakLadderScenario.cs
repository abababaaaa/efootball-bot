using EfootballBot.Core.Bot.Scenarios.CnWeekly;
using EfootballBot.Core.Bot.Scenarios.Weekly;
using EfootballBot.Core.Bot.Scenarios.AiLadder;
using EfootballBot.Core.Bot.Scenarios.MyLeague;
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

namespace EfootballBot.Core.Bot.Scenarios.PeakLadder;

/// <summary>
/// 国服天梯巅峰赛挂机场景（实况足球在线·教练模式）：
/// 主页比赛页签 下+A 进天梯巅峰赛（卡行默认焦点即它）→ 模式选择页鼠标点右下「天梯巅峰赛-教练」卡
/// （本页无手柄导航，仅鼠标可点、B 返回）→ 活动主页识别「本周可参赛场次」：
/// 0 = B 退出活动并提示本周场次已用尽后停止；&gt;0 = 鼠标点「前往比赛」→「请选择参赛模式」弹窗
/// 上×2+A 选单场比赛（手柄光标默认在取消）→ 可能的后续弹窗 A 推进 → 开赛（教练模式 AI 自动踢，
/// 交共用比赛流程）。赛后回活动主页重读场次循环，直到场次为 0。
/// 输入注意：国服游戏为提权进程，鼠标必须走后台 PostMessage 且本程序须以管理员运行
/// （UIPI 拦截低权限注入；虚拟手柄输入不受限）；比赛起始页确认键是 RT 而非 A（国服特有）。
/// 判定只用固定 UI 词；段位名/积分/赛季数/剩余天数等可变内容不作判定点。
/// </summary>
public sealed class PeakLadderScenario : IScenario
{
    /// <summary>菜单侧流程阶段（屏幕驱动；每阶段独立失败预算，超限 B 逃生回 Flow 重导航）。</summary>
    private enum PlPhase
    {
        /// <summary>屏幕驱动导航：主页→模式选择→活动主页循环读场次。</summary>
        Flow,
        /// <summary>已点前往比赛：参赛模式弹窗（上×2+A 单场）与后续弹窗推进，直到开赛。</summary>
        Launching,
        /// <summary>场次为 0：B 退出活动链，回主页后提示并停止挂机。</summary>
        Exiting,
    }

    private readonly BotEngine _engine;

    private readonly Action<string, LogLevel> _log;

    private readonly BackgroundMouse _mouse;

    private readonly BackgroundKeyboard _keyboard = new();

    private readonly MatchFlowMemory _memory = new();
    /// <summary>赛中匹配（共享层）：开赛/终场判定 + 场内挂机节奏 + 场次记账，五个模块同一份实现。</summary>
    private readonly MatchPlay _matchPlay;
    /// <summary>底层场次跟踪器（模块内读用时/关键比赛等细节用；比赛怎么算由 MatchPlay 决定）。</summary>
    private MatchSessionTracker _matchSession => _matchPlay.Session;

    private PlPhase _phase = PlPhase.Flow;
    /// <summary>当前阶段累计行动次数（SetPhase/换屏清零；超限 B 逃生防死循环）。</summary>
    private int _attempts;
    /// <summary>活动主页连续读不到可参赛场次的轮数（超阈值 B 逃生清遮挡，绝不盲点前往比赛）。</summary>
    private int _countUnknownRounds;

    private DateTime _lastCountWarnAt = DateTime.MinValue;

    private string _lastScreenName = "";

    private int _streak;

    private bool _takeoverLogged;
    /// <summary>TitleScreen 首次出现时间（比赛上下文中按 X 前宽限，区分加载画面与真标题）。</summary>
    private DateTime? _titleSince;

    public string Name => "天梯巅峰赛";
    /// <summary>比赛中（供引擎把手柄诊断挡在比赛之外）。</summary>
    public bool InMatch => _matchSession.Active;

    public PeakLadderScenario(BotEngine engine, Action<string, LogLevel> log)
    {
        _engine = engine;
        _log = log;
        _matchPlay = new MatchPlay(engine, "天梯巅峰赛", log);
        _mouse = new BackgroundMouse();
        _homeGuide = new ReturnToHomeGuide("天梯巅峰赛", log, engine.DumpSuspiciousFrame);
    }

    /// <summary>启动引导：停在其他模块页时先 B 收敛回主页。</summary>
    private readonly ReturnToHomeGuide _homeGuide;

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var obs = await _engine.ObserveAsync(ct);
        var now = DateTime.Now;
        // 角区探针兜底 InMatch 需比赛上下文（同我的联赛/国服周活动场景）
        bool corner = ScreenProbe.CornerHasContent(obs.Frame);
        bool light = ScreenProbe.IsMostlyLightPage(obs.Frame);
        // 画面还是球场（进球庆祝/回放过场）→ 共享分类一律按比赛中处理（2026-11-20 虚计场次事故）
        bool pitch = ScreenProbe.LooksLikePitch(obs.Frame);
        bool matchContext = MatchShared.MatchContext(_memory, now);
        // 本模块界面先判：命中时 ml 记 Unknown（状态机处理，不走共享决策）；
        // 未命中再判周活动（冠军赛）残留页面；两者都不是才交 MyLeagueBrain
        var pl = PeakLadderBrain.Classify(obs.Ocr, obs.Detections);
        var cw = pl == PlScreen.None
            ? CnWeeklyBrain.Classify(obs.Detections, obs.Ocr)
            : CwScreen.None;
        var ml = (pl == PlScreen.None && cw == CwScreen.None)
            ? MatchBrain.Classify(obs.Detections, obs.Ocr, corner, matchContext, light,
                MatchShared.LoginRecovery(_memory, now), pitch)
            : MatchScreen.Unknown;
        _memory.Record(ml, now);
        // 启动引导：停在其他模块页（周活动/AI天梯/我的联赛）时先 B 收敛回主页。
        bool inMatchFlow = matchContext || _matchSession.Active || ReturnToHomeGuide.IsSharedMatchFlow(ml);
        bool myLeagueResidual = pl == PlScreen.None &&
                                (ml is MatchScreen.PointsPage or MatchScreen.ItemList ||
                                 (ml == MatchScreen.LeaguePage && MyLeagueBrain.LooksLikeLeagueHome(obs.Ocr)));
        bool otherModule = !inMatchFlow &&
                           (cw != CwScreen.None
                            || WeeklyBrain.Classify(obs.Detections, obs.Ocr, corner, matchContext, light) != WkScreen.None
                            || AiLadderBrain.Classify(obs.Ocr) != AlScreen.None
                            || myLeagueResidual);
        bool ownPage = pl != PlScreen.None || inMatchFlow;
        int? guideWait = await _homeGuide.StepAsync(obs.Frame,
            ml == MatchScreen.MainMenu, ownPage, otherModule, () => _engine.Pad.Back());
        if (guideWait is not null) return guideWait.Value;
        if (!_takeoverLogged)
        {
            _takeoverLogged = true;
            _log($"[天梯巅峰赛] 智能接管：当前界面「{(pl != PlScreen.None ? pl.ToString() : ml.ToString())}」，从该阶段继续", LogLevel.Success);
        }
        // ---- 赛中匹配（共享层 MatchPlay）：本模块不回我的联赛主页（isHome 恒 false），
        //      终场 FullTime 是权威关场信号，超时兜底 5 分钟，记账全在 MatchPlay 内
        if (pl is PlScreen.HomeMatch or PlScreen.HomeRecommended or PlScreen.ModeSelect)
            _matchPlay.MarkAtMenu();
        _matchPlay.Tick(ml, now, sawKeyMatchHint: false, isHome: false,
            startNote: "教练模式挂机等待比赛结束（按节奏随机点击）");
        string screenName = pl != PlScreen.None ? $"天梯巅峰赛.{pl}" : ml.ToString();
        if (screenName == _lastScreenName) _streak++;
        else
        {
            _streak = 0;
            _lastScreenName = screenName;
            _attempts = 0; // 换屏即重置当前阶段预算
            _log($"[天梯巅峰赛] 界面：{screenName}", LogLevel.Info);
        }
        // 国服比赛起始页：底栏印「设置|球场|RT 下一步」。按用户 2026-11-21 规则，国服「下一步/确认」
        // 一律走国服键序：① RT（页面所印提示键）→ ② A → ③ 后台鼠标点击（共享层 CnAdvance，一份实现）。
        // 拦截仍先于状态机：画面不变的起始页会让共享决策在 _streak≥StuckStreak 后转静默等待
        // （国服周活动 2026-10-14 卡死教训）。
        if (ml == MatchScreen.MatchStartPage)
        {
            if (_streak < 1) return 800; // 过渡帧防抖
            return await CnAdvance.AdvanceAsync(_engine, _mouse, _streak + 1, "起始页", "天梯巅峰赛", _log,
                obs.Ocr, obs.Detections, (0.850, 0.920), ct);
        }
        // Flow 阶段 HasConfirm 页面连续多轮无进展：国服键序（RT→A→鼠标）都用完仍不动才按 B 退出。
        // 阈值放在键序之后（第 8 轮）：前 7 轮 = RT/A/鼠标各试过，页面真死了才逃生，绝不抢在
        // 鼠标点击（第 3 轮，段位积分页/对阵表这类网页层页面唯一有效输入）之前 B 掉。
        if (_phase == PlPhase.Flow && ml == MatchScreen.HasConfirm && _streak >= 8)
        {
            _log("[天梯巅峰赛] 确认页面 RT/A/鼠标多轮无进展，按 B 退出", LogLevel.Warn);
            await _engine.Pad.Back();
            return 2400;
        }
        // ---- 菜单侧状态机：接管本模块界面与阶段内无特征界面 ----
        int? plWait = await HandlePlAsync(pl, cw, ml, obs, ct);
        if (plWait is > 0) return plWait.Value;
        if (plWait is not null) return 1500;
        // pl==None 且阶段已交回共享流程：走共用决策（比赛中/终场/弹窗/逃生）
        var action = MatchBrain.Decide(ml, obs.Ocr, _streak, matchContext, obs.Frame);
        MatchShared.DecisionSnapshot(_log, "天梯巅峰赛", ml, _streak, action, obs.Ocr);
        // 标题画面宽限（共享实现，含赛后弹窗链 90s 档）：比赛上下文中的品牌过场帧会
        // 连续出现 30s 以上，宽限 90s
        if (action.Kind != MatchActionKind.PadXPress)
        {
            _titleSince = null;
        }
        else if (MatchShared.TitleGrace(_memory, now, ref _titleSince, _log, "天梯巅峰赛"))
        {
            return 2000;
        }
        return await MatchActionExecutor.ExecuteAsync(
            _engine, _matchPlay, action, obs, _log, "天梯巅峰赛", ct,
            onEnterMatch: async () =>
            {
                // 国服主页：上×2 进页签行 → 左×4 饱和归位「推荐」→ 右×1 进「比赛」页签
                _log("[天梯巅峰赛] 主页导航：上×2 → 左×4 归位推荐页签 → 右进比赛页签", LogLevel.Info);
                await _engine.Pad.DpadRepeat(PadDir.Up, 2);
                await _engine.Pad.DpadRepeat(PadDir.Left, 4);
                await _engine.Pad.Dpad(PadDir.Right);
                return null;
            },
            onConfirm: async () =>
            {
                // 国服键序：比赛中的弹窗只按 A；赛外走 RT → A → 鼠标
                if (_matchSession.Active)
                {
                    _log($"[天梯巅峰赛] 比赛中弹窗 A：{action.Target}", LogLevel.Info);
                    await _engine.Pad.Confirm();
                    return null;
                }
                return await CnAdvance.AdvanceAsync(_engine, _mouse, _streak + 1, action.Target,
                    "天梯巅峰赛", _log, obs.Ocr, obs.Detections, null, ct);
            });
    }

    // ---------------- 菜单侧状态机 ----------------
    /// <summary>返回正数/0=本轮已处理（下一轮延时）；null=未接管（走共享决策）。</summary>
    private async Task<int?> HandlePlAsync(PlScreen pl, CwScreen cw, MatchScreen ml, Observation obs, CancellationToken ct)
    {
        switch (_phase)
        {
            case PlPhase.Flow:
            {
                // 周活动（冠军赛）残留页面：本模块不经过这些页（冠军赛结束后退出时可能路过），
                // 一律 B 退出回主页（实机 2026-10-14 03:33：B 逃生落在活动列表页，主页导航空转）
                if (cw is CwScreen.EventsList or CwScreen.EventIntro or CwScreen.Bracket or
                    CwScreen.EventHome or CwScreen.Squad or CwScreen.SquadMenu)
                {
                    if (++_attempts > 6)
                    {
                        _log("[天梯巅峰赛] 周活动残留页面多次未退出，暂停 8s 后重试（若反复出现请回报日志）", LogLevel.Error);
                        _attempts = 0;
                        return 8000;
                    }
                    _log($"[天梯巅峰赛] 周活动残留页面（{cw}），B 退回主页", LogLevel.Info);
                    await _engine.Pad.Back();
                    return 2400;
                }
                switch (pl)
                {
                    case PlScreen.HomeRecommended:
                        // 推荐页签：只做「归位到比赛页签」这一件事（卡行与进天梯交给下一帧的
                        // HomeMatch 分支用鼠标点卡面）。2026-11-21 01:53 实机：把
                        // 上×2/左×4/右/下/A 当一串盲按，A 要么落在页签上、要么被主页提示弹窗
                        // 吃掉，画面三轮不变，日志刷「主页：上×2 左×4 …」永远进不了天梯。
                        if (++_attempts > 4)
                        {
                            _log("[天梯巅峰赛] 主页导航多轮未切到比赛页签，暂停 8s 后重试（若反复出现请回报日志）", LogLevel.Error);
                            _attempts = 0;
                            return 8000;
                        }
                        _log("[天梯巅峰赛] 主页（推荐页签）：上×2 进页签行 → 左×4 归位推荐 → 右进比赛页签", LogLevel.Info);
                        await _engine.Pad.DpadRepeat(PadDir.Up, 2);
                        await _engine.Pad.DpadRepeat(PadDir.Left, 4);
                        await _engine.Pad.Dpad(PadDir.Right);
                        return 2200;
                    case PlScreen.HomeMatch:
                        return await EnterLadderFromCardRowAsync(obs, ct);
                    case PlScreen.ModeSelect:
                    {
                        // 本页无手柄导航：必须鼠标点右下「天梯巅峰赛-教练」卡（B 是返回）
                        if (_streak < 1) return 800; // 过渡帧防抖
                        if (++_attempts > 4)
                        {
                            _log("[天梯巅峰赛] 模式选择页多次未进入活动主页，B 返回比赛页重导航", LogLevel.Warn);
                            await _engine.Pad.Back();
                            return 2400;
                        }
                        var card = PeakLadderBrain.FindCoachCardWord(obs.Ocr);
                        await MouseClickAsync(card, PeakLadderBrain.CoachCardFallbackX, PeakLadderBrain.CoachCardFallbackY,
                            card is null ? "天梯巅峰赛-教练卡（兜底坐标）" : "天梯巅峰赛-教练卡", ct);
                        return 3000;
                    }
                    case PlScreen.ActivityHome:
                        return await HandleActivityHomeAsync(obs, ct);
                    case PlScreen.MatchSummary:
                        // 赛后小结（段位分/最佳球员）与段位积分页同属国服天梯网页层：
                        // 手柄 A/B 都不生效，必须鼠标点「确认」
                        return await HandleWebLayerPageAsync(obs, new[] { "确认", "确定", "下一步" },
                            null, "赛后小结", ct);
                    case PlScreen.RankProgress:
                        // 段位积分页：右下「下一步」。OCR 常整帧漏读按钮字，靠 YOLO 框定位；
                        // 该按钮印的是键盘「C」（不是 A/RT），鼠标点不动时按 C 兜一层
                        return await HandleWebLayerPageAsync(obs, new[] { "下一步" },
                            (PeakLadderBrain.RankNextFallbackX, PeakLadderBrain.RankNextFallbackY),
                            "段位积分页", ct, keyVirtual: 0x43); // VK_C
                    case PlScreen.ModeDialog:
                        // Flow 途中直接撞见弹窗（接管/重复进入）：交给 Launching 流程
                        SetPhase(PlPhase.Launching);
                        return 700;
                }
                return null; // 其余共享界面（比赛中/弹窗/未知逃生）交给共用决策
            }
            case PlPhase.Launching:
            {
                if (pl == PlScreen.ModeDialog)
                {
                    // 弹窗自带「当前剩余参赛场次」：=0 即场次用尽（活动主页的橙色 0 偶发被
                    // OCR 误读成 99 并循环点前往比赛，实机 2026-10-14 13:37）——取消并退出
                    var dlgCount = PeakLadderBrain.ParsePlayableCount(obs.Ocr);
                    if (dlgCount == 0)
                    {
                        _log("【提示】本周天梯场次已用尽（弹窗剩余场次=0），取消弹窗并关闭活动", LogLevel.Success);
                        SetPhase(PlPhase.Exiting);
                        await _engine.Pad.Back();
                        return 2400;
                    }
                    if (_streak < 1) return 900; // 弹窗展开过渡帧防抖
                    if (++_attempts > 6)
                    {
                        _log("[天梯巅峰赛] 参赛模式弹窗多次未生效，B 取消回主页重走", LogLevel.Warn);
                        SetPhase(PlPhase.Flow);
                        await _engine.Pad.Back();
                        return 2200;
                    }
                    // 前两轮走手柄配方：光标默认在「取消」（底行），上×2 到「单场比赛」→ A。
                    // 仍不生效说明这一帧落在国服网页层弹窗上（手柄导航被吞，实机 2026-11-07
                    // 12:19 连按 4 轮画面不动），改鼠标直接点「单场比赛」选项——与世界活动、
                    // 教练选择页同款处置（鼠标点击是该层唯一可靠输入）
                    var single = _attempts >= 3 ? PeakLadderBrain.FindOptionWord(obs.Ocr, "单场比赛") : null;
                    if (single is not null)
                    {
                        _log("[天梯巅峰赛] 参赛模式弹窗：手柄配方未生效，改鼠标点「单场比赛」", LogLevel.Warn);
                        await MouseClickAsync(null, single.CenterX, single.CenterY + 0.02, "单场比赛", ct);
                        return 3200;
                    }
                    _log("[天梯巅峰赛] 参赛模式弹窗：上×2 选「单场比赛」→ A", LogLevel.Info);
                    await _engine.Pad.DpadRepeat(PadDir.Up, 2);
                    await _engine.Pad.Confirm();
                    return 3000;
                }
                if (pl == PlScreen.ActivityHome)
                {
                    // 点前往比赛没弹窗（未生效）或已取消：回 Flow 重读场次再试
                    SetPhase(PlPhase.Flow);
                    return 700;
                }
                if (ml == MatchScreen.InMatch)
                {
                    SetPhase(PlPhase.Flow); // 已开赛：交共享比赛流程，赛后屏幕驱动自动续
                    return null;
                }
                // 选择后可能弹出的确认/下一步弹窗：有推进词就走国服键序（RT → A → 鼠标）
                if (ml == MatchScreen.HasConfirm ||
                    obs.Ocr.ContainsJoined("下一步") || obs.Ocr.ContainsJoined("确定") ||
                    obs.Ocr.ContainsJoined("确认") || obs.Ocr.FindButton("OK") is not null)
                {
                    if (++_attempts > 8) { SetPhase(PlPhase.Flow); return 600; }
                    return await CnAdvance.AdvanceAsync(_engine, _mouse, _attempts, "赛前确认弹窗",
                        "天梯巅峰赛", _log, obs.Ocr, obs.Detections, null, ct);
                }
                if (++_attempts > 12)
                {
                    _log("[天梯巅峰赛] 开赛链多轮未推进，B 返回重走流程", LogLevel.Warn);
                    SetPhase(PlPhase.Flow);
                    await _engine.Pad.Back();
                    return 2200;
                }
                return 1500;
            }
            case PlPhase.Exiting:
            {
                if (pl is PlScreen.HomeMatch or PlScreen.HomeRecommended)
                {
                    _log("[天梯巅峰赛] 本周天梯场次已用尽，已退出活动，挂机结束（下周场次刷新后可再跑）", LogLevel.Success);
                    _engine.Stop();
                    return 0;
                }
                if (pl is PlScreen.ActivityHome or PlScreen.ModeSelect)
                {
                    if (++_attempts > 4)
                    {
                        _log("[天梯巅峰赛] 退出活动多轮未成功，直接停止挂机", LogLevel.Warn);
                        _engine.Stop();
                        return 0;
                    }
                    _log("[天梯巅峰赛] 退出中：B 返回上一页", LogLevel.Info);
                    await _engine.Pad.Back();
                    return 2400;
                }
                if (++_attempts > 8)
                {
                    _log("[天梯巅峰赛] 退出链未收敛，直接停止挂机", LogLevel.Warn);
                    _engine.Stop();
                    return 0;
                }
                return 1500;
            }
            default:
                return null;
        }
    }

    /// <summary>活动主页：读「本周可参赛场次」。0=B 退出并提示；&gt;0=鼠标点前往比赛；
    /// 读不到=清弹窗/B 逃生，绝不盲目开赛（用户规则：以识别到的场次为准）。</summary>
    private async Task<int> HandleActivityHomeAsync(Observation obs, CancellationToken ct)
    {
        int? count = PeakLadderBrain.ParsePlayableCount(obs.Ocr);
        if (count is null)
        {
            _countUnknownRounds++;
            // 页面上可能有未分类弹窗：走国服键序（① RT → ② A → ③ 鼠标点按钮）清掉；多轮到 8 轮
            // 仍不动就按 B 收掉（实机 2026-11-07 12:15：赛后小结弹窗上 A 连按 42 次画面不动，
            // 旧逻辑会一直空按到用户手动停止）
            if (obs.Ocr.ContainsJoined("下一步") || obs.Ocr.ContainsJoined("确定") ||
                obs.Ocr.ContainsJoined("确认") || obs.Ocr.FindButton("OK") is not null)
            {
                if (_countUnknownRounds >= 8)
                {
                    _log("[天梯巅峰赛] 活动主页遮挡物 RT/A/鼠标均无效，B 收掉后重读场次", LogLevel.Warn);
                    await _engine.Pad.Back();
                    return 2400;
                }
                return await CnAdvance.AdvanceAsync(_engine, _mouse, _countUnknownRounds, "活动主页遮挡物",
                    "天梯巅峰赛", _log, obs.Ocr, obs.Detections, null, ct);
            }
            if (_countUnknownRounds == 8)
            {
                _log("[天梯巅峰赛] 连续读不到可参赛场次，按 B 尝试关掉遮挡物", LogLevel.Warn);
                await _engine.Pad.Back();
                return 2400;
            }
            if (_countUnknownRounds >= 16 && (DateTime.Now - _lastCountWarnAt).TotalMinutes >= 2)
            {
                _lastCountWarnAt = DateTime.Now;
                _countUnknownRounds = 8; // 回到 B 逃生节拍，持续尝试恢复
                _log($"[天梯巅峰赛] 可参赛场次持续无法读取（OCR 行：{OcrDigest(obs.Ocr)}），保持在活动主页等画面恢复", LogLevel.Error);
            }
            return 1800;
        }
        _countUnknownRounds = 0;
        if (count == 0)
        {
            _log("【提示】本周天梯场次已用尽（本周可参赛场次=0），关闭本次活动并停止挂机", LogLevel.Success);
            SetPhase(PlPhase.Exiting);
            await _engine.Pad.Back();
            return 2400;
        }
        if (_streak < 1) return 800; // 过渡帧防抖
        if (++_attempts > 4)
        {
            _log("[天梯巅峰赛] 前往比赛多次未生效，B 返回重导航", LogLevel.Warn);
            await _engine.Pad.Back();
            return 2400;
        }
        var go = PeakLadderBrain.FindGoMatchWord(obs.Ocr);
        _log($"[天梯巅峰赛] 本周可参赛场次 {count}：进入单场比赛循环", LogLevel.Info);
        await MouseClickAsync(go, PeakLadderBrain.GoMatchFallbackX, PeakLadderBrain.GoMatchFallbackY, "前往比赛", ct);
        SetPhase(PlPhase.Launching);
        return 3000;
    }

    /// <summary>
    /// 比赛页签 → 天梯巅峰赛卡（主页导航的第二步）。2026-11-21 01:53 实机：旧实现把
    /// 上×2/左×4/右/下/A 当一串盲按，三轮画面都停在比赛页——手柄焦点进卡行后，再按「下」
    /// 会把焦点移出卡行，最后那记 A 落在空处（或页签上），永远到不了天梯。改成：
    ///   1) 后台鼠标直接点卡面标题「天梯巅峰赛」——不依赖焦点链，实测 2026-11-21 02:16
    ///      点 (0.5, 0.72) 一次就进模式选择页；OCR 锚到卡面标题时点标题中心，锚不到用兜底坐标。
    ///   2) 鼠标两轮没反应（极少数帧 OCR 读不到卡面标题）才退回手柄：上×1 归位页签行 →
    ///      下进卡行 → A。绝不再按「下×1 就 A」当主路径。
    /// </summary>
    private async Task<int> EnterLadderFromCardRowAsync(Observation obs, CancellationToken ct)
    {
        if (++_attempts > 4)
        {
            _log("[天梯巅峰赛] 比赛页签多轮进不去天梯巅峰赛，暂停 8s 后重试（若反复出现请回报日志）", LogLevel.Error);
            _attempts = 0;
            return 8000;
        }
        // 主页提示弹窗（2026-11-21 实机：首次点「比赛」页签弹出的「来试试技巧提升吧」）：
        // 只认鼠标点击——手柄 A 按了不生效，方向键也被弹窗全吃掉，画面永远停在主页；
        // 旧实现就在这上面无限「主页导航」。先鼠标点掉「确定」再进天梯。
        if (PeakLadderBrain.FindAdvanceWord(obs.Ocr, "确定", "确认", "知道了") is { } popupOk)
        {
            _log("[天梯巅峰赛] 主页有提示弹窗，鼠标点「确定」关掉后再进天梯", LogLevel.Info);
            await MouseClickAsync(null, popupOk.CenterX, popupOk.CenterY, "主页弹窗确定", ct);
            return 2400;
        }
        if (_attempts <= 2)
        {
            var tile = PeakLadderBrain.FindLadderTileWord(obs.Ocr);
            await MouseClickAsync(tile, PeakLadderBrain.LadderTileFallbackX, PeakLadderBrain.LadderTileFallbackY,
                tile is null ? "天梯巅峰赛卡面（兜底坐标）" : "天梯巅峰赛卡面", ct);
            return 3200;
        }
        _log("[天梯巅峰赛] 卡面点击未生效：改手柄 上×1 归位 → 下进卡行 → A", LogLevel.Warn);
        await _engine.Pad.Dpad(PadDir.Up);
        await _engine.Pad.Dpad(PadDir.Down);
        await _engine.Pad.Wait(350, ct); // 等卡行焦点动画落定，A 才吃得到
        await _engine.Pad.Confirm();
        return 3200;
    }

    /// <summary>
    /// 国服天梯「网页层」结算页（赛后小结 / 段位积分页）推进。
    /// 这两页是国服客户端里的网页 UI：虚拟手柄 A/B 都不生效（实机 2026-11-07：白银联赛页
    /// A×5 + B×6 画面不动；赛后小结弹窗 A 连按 42 次不动），只有鼠标点击（或它自己印在按钮上的
    /// 键盘键）能推进——用户反馈「右下角的下一步点不到」即此。按用户 2026-11-21 规则走国服键序：
    /// 第 1 轮 RT（底栏印的提示键）→ 第 2 轮 A → 第 3 轮起鼠标点击（OCR 推进词 → 右下 YOLO
    /// 确认框 → 兜底坐标）→ 第 6 轮再试页面上印的键盘键（段位积分页旧版为 C）→ 8 轮仍不动才 B 逃生。
    /// </summary>
    /// <param name="buttonWords">该页的推进按钮文字（段位积分页只有「下一步」，赛后小结多一个「确认」）。</param>
    /// <param name="fallback">OCR/检测都定位不到时的兜底坐标；null = 用国服通用右下落点。</param>
    /// <param name="keyVirtual">该页按钮上印的键盘提示键（如段位积分页的 C）；0 = 该页没有键盘提示。</param>
    private async Task<int> HandleWebLayerPageAsync(Observation obs, string[] buttonWords,
        (double X, double Y)? fallback, string what, CancellationToken ct, int keyVirtual = 0)
    {
        int attempt = ++_attempts;
        if (attempt >= 8)
        {
            _log($"[天梯巅峰赛] {what}：RT/A/鼠标/提示键全部无效（{attempt} 轮），按 B 退出交回共享流程（若反复出现请回报日志）", LogLevel.Warn);
            _attempts = 0;
            await _engine.Pad.Back();
            return 2400;
        }
        if (attempt == 6 && keyVirtual != 0)
        {
            var winKey = _engine.Window;
            if (winKey is not null)
            {
                _log($"[天梯巅峰赛] {what}：RT/A/鼠标均未推进，按页面上印的键盘键「{(char)keyVirtual}」", LogLevel.Warn);
                await _keyboard.TapAsync(winKey.Handle, keyVirtual, ct: ct);
                return 2400;
            }
        }
        return await CnAdvance.AdvanceAsync(_engine, _mouse, attempt, what, "天梯巅峰赛", _log,
            obs.Ocr, obs.Detections, fallback, ct, buttonWords);
    }

    /// <summary>后台鼠标点击：优先 OCR 锚定词位置（其中心下方 0.02 落在卡面内），
    /// 无锚定用实机校准兜底坐标。国服游戏须本程序提权后 PostMessage 才被处理。</summary>
    private async Task MouseClickAsync(OcrWord? anchor, double fallbackX, double fallbackY, string what, CancellationToken ct)
    {
        double nx = anchor is null ? fallbackX : anchor.CenterX;
        double ny = anchor is null ? fallbackY : anchor.CenterY + 0.02;
        var win = _engine.Window;
        if (win is null)
        {
            _log("[天梯巅峰赛] 游戏窗口句柄缺失，无法执行鼠标点击", LogLevel.Error);
            return;
        }
        // 全限定：GameWindow 在 EfootballBot.Core.System 命名空间（与全局 System 遮蔽陷阱，不用 using）
        var (w, h) = EfootballBot.Core.System.GameWindow.GetClientSize(win.Handle);
        if (w <= 0 || h <= 0) (w, h) = (1280, 720);
        _log($"[天梯巅峰赛] 鼠标点击{what}（{nx:F3},{ny:F3}）", LogLevel.Info);
        await _engine.Pad.HumanWait(150, 320, ct); // 拟人预停顿
        await _mouse.ClickNormalizedAsync(win.Handle, nx, ny, w, h, downHoldMs: 70, ct: ct);
    }

    private void SetPhase(PlPhase phase)
    {
        _phase = phase;
        _attempts = 0;
    }

    /// <summary>OCR 行摘要（场次读不到等远程排障用，截断防日志膨胀）。</summary>
    private static string OcrDigest(OcrResult ocr)
    {
        var text = string.Join(" | ", ocr.Lines.Select(l => l.Text));
        return text.Length <= 200 ? text : text[..200];
    }
}




