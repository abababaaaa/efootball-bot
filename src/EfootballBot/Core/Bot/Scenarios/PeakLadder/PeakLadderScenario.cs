using EfootballBot.Core.Bot.Scenarios.CnWeekly;
using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;

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
    private readonly MlFlowMemory _memory = new();
    private readonly MatchSessionTracker _matchSession = new();
    private readonly Random _rnd = new();

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
    private DateTime _lastTapLogAt = DateTime.MinValue;

    public string Name => "天梯巅峰赛";

    public PeakLadderScenario(BotEngine engine, Action<string, LogLevel> log)
    {
        _engine = engine;
        _log = log;
        _mouse = new BackgroundMouse();
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var obs = await _engine.ObserveAsync(ct);
        var now = DateTime.Now;
        // 角区探针兜底 InMatch 需比赛上下文（同我的联赛/国服周活动场景）
        bool corner = MyLeagueScenario.CornerHasContent(obs.Frame);
        bool light = MyLeagueScenario.IsMostlyLightPage(obs.Frame);
        bool matchContext = ScenarioShared.MatchContext(_memory, now);

        // 本模块界面先判：命中时 ml 记 Unknown（状态机处理，不走共享决策）；
        // 未命中再判周活动（冠军赛）残留页面；两者都不是才交 MyLeagueBrain
        var pl = PeakLadderBrain.Classify(obs.Ocr);
        var cw = pl == PlScreen.None
            ? CnWeeklyBrain.Classify(obs.Detections, obs.Ocr)
            : CwScreen.None;
        var ml = (pl == PlScreen.None && cw == CwScreen.None)
            ? MyLeagueBrain.Classify(obs.Detections, obs.Ocr, corner, matchContext, light,
                ScenarioShared.LoginRecovery(_memory, now))
            : MlScreen.Unknown;
        _memory.Record(ml, now);

        if (!_takeoverLogged)
        {
            _takeoverLogged = true;
            _log($"[天梯巅峰赛] 智能接管：当前界面「{(pl != PlScreen.None ? pl.ToString() : ml.ToString())}」，从该阶段继续", LogLevel.Success);
        }

        // ---- 共用比赛流程：场次计数。不回我的联赛主页（isHome 恒 false），
        //      终场 FullTime 是权威关场信号，超时兜底 5 分钟
        bool wasActive = _matchSession.Active;
        bool matchEnded = _matchSession.Observe(ml, now, sawKeyMatchHint: false, isHome: false);
        if (!wasActive && _matchSession.Active)
            _log("[天梯巅峰赛] 比赛开始：教练模式挂机等待比赛结束（按节奏随机点击）", LogLevel.Info);
        if (matchEnded)
        {
            _engine.Stats.MatchesDone++;
            _log($"[天梯巅峰赛] 一场比赛结束（累计 {_engine.Stats.MatchesDone} 场）", LogLevel.Success);
            _engine.ReportMatchDone(_matchSession.WasKeyMatch);
        }

        string screenName = pl != PlScreen.None ? $"天梯巅峰赛.{pl}" : ml.ToString();
        if (screenName == _lastScreenName) _streak++;
        else
        {
            _streak = 0;
            _lastScreenName = screenName;
            _attempts = 0; // 换屏即重置当前阶段预算
            _log($"[天梯巅峰赛] 界面：{screenName}", LogLevel.Info);
        }

        // 国服比赛起始页：底栏「设置|球场|RT 下一步」——确认键是 RT 而非 A（国际服为 A）。
        // 共享决策按 A 无法开赛，且画面不变 _streak≥StuckStreak 后转入静默等待会死等
        // （国服周活动实机 2026-10-14 00:53 卡死教训）。先于状态机拦截：RT 开赛。
        if (ml == MlScreen.MatchStartPage)
        {
            if (_streak < 1) return 800; // 过渡帧防抖
            _log("[天梯巅峰赛] 起始页：RT 下一步开赛（国服确认键为 RT）", LogLevel.Info);
            await _engine.Pad.RT();
            return 3400;
        }

        // Flow 阶段 HasConfirm 页面连续 5 轮无进展：A 在该页不生效（如冠军赛结束后的
        // 对阵表「RT 下一步」页面，实机 2026-10-14 03:28 连按 A 8 次画面不动后共享决策
        // 静默死等）→ 按 B 退出该页。正常确认弹窗第一轮 A 就会推进，到不了 5 轮；
        // ModeDialog 等本模块界面已被 pl 分支先行接管，不受影响。
        if (_phase == PlPhase.Flow && ml == MlScreen.HasConfirm && _streak >= 5)
        {
            _log("[天梯巅峰赛] 确认页面多轮无进展（疑似 RT 类页面），按 B 退出", LogLevel.Warn);
            await _engine.Pad.Back();
            return 2400;
        }

        // ---- 菜单侧状态机：接管本模块界面与阶段内无特征界面 ----
        int? plWait = await HandlePlAsync(pl, cw, ml, obs, ct);
        if (plWait is > 0) return plWait.Value;
        if (plWait is not null) return 1500;
        // pl==None 且阶段已交回共享流程：走共用决策（比赛中/终场/弹窗/逃生）

        var action = MyLeagueBrain.Decide(ml, obs.Ocr, _streak, matchContext, obs.Frame);
        ScenarioShared.DecisionSnapshot(_log, "天梯巅峰赛", ml, _streak, action, obs.Ocr);

        // 标题画面宽限（共享实现，含赛后弹窗链 90s 档）：比赛上下文中的品牌过场帧会
        // 连续出现 30s 以上，宽限 90s
        if (action.Kind != MlActionKind.PadXPress)
        {
            _titleSince = null;
        }
        else if (ScenarioShared.TitleGrace(_memory, now, ref _titleSince, _log, "天梯巅峰赛"))
        {
            return 2000;
        }

        switch (action.Kind)
        {
            case MlActionKind.PadEnterMatch:
                // 国服主页：上×2 进页签行 → 左×4 饱和归位「推荐」→ 右×1 进「比赛」页签
                // （卡行进入天梯巅峰赛由 Flow 的主页分支处理）
                _log("[天梯巅峰赛] 主页导航：上×2 → 左×4 归位推荐页签 → 右进比赛页签", LogLevel.Info);
                await _engine.Pad.DpadRepeat(PadDir.Up, 2);
                await _engine.Pad.DpadRepeat(PadDir.Left, 4);
                await _engine.Pad.Dpad(PadDir.Right);
                break;

            case MlActionKind.PadConfirm:
                _log($"[天梯巅峰赛] 手柄 A：{action.Target}", LogLevel.Info);
                await _engine.Pad.Confirm();
                break;

            case MlActionKind.PadUpConfirm:
                _log($"[天梯巅峰赛] 弹窗上移确认：{action.Target}", LogLevel.Info);
                await _engine.Pad.Dpad(PadDir.Up);
                await _engine.Pad.Confirm();
                break;

            case MlActionKind.PadStartPress:
                _log($"[天梯巅峰赛] {action.Target}", LogLevel.Info);
                await _engine.Pad.Start();
                break;

            case MlActionKind.PadXPress:
                _log($"[天梯巅峰赛] {action.Target}", LogLevel.Info);
                await _engine.Pad.X();
                break;

            case MlActionKind.PadLoginNav:
                // eFootball 2027 标题页登录序列：下→右→A（A 落在「登录」上，用户实机序列）
                _log($"[天梯巅峰赛] {action.Target}", LogLevel.Info);
                await _engine.Pad.Dpad(PadDir.Down);
                await _engine.Pad.Dpad(PadDir.Right);
                await _engine.Pad.Confirm();
                break;

            case MlActionKind.GamepadBack:
                _log($"[天梯巅峰赛] {action.Target}", LogLevel.Warn);
                await _engine.Pad.Back();
                break;

            case MlActionKind.MatchIdleTap:
            {
                await _engine.Pad.Confirm();
                var t = _engine.Config.Timing;
                int waitMs = _rnd.Next(Math.Min(t.IdleTapMinMs, t.IdleTapMaxMs),
                                       Math.Max(t.IdleTapMinMs, t.IdleTapMaxMs) + 1);
                if ((DateTime.Now - _lastTapLogAt).TotalMinutes >= 1)
                {
                    _lastTapLogAt = DateTime.Now;
                    _log($"[天梯巅峰赛] 比赛挂机中：随机点击 A（{waitMs / 1000.0:F1}s 间隔）", LogLevel.Debug);
                }
                return waitMs;
            }

            case MlActionKind.PadMoveRight:
                await _engine.Pad.Dpad(PadDir.Right);
                break;
        }
        return action.WaitMs;
    }

    // ---------------- 菜单侧状态机 ----------------

    /// <summary>返回正数/0=本轮已处理（下一轮延时）；null=未接管（走共享决策）。</summary>
    private async Task<int?> HandlePlAsync(PlScreen pl, CwScreen cw, MlScreen ml, Observation obs, CancellationToken ct)
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
                    case PlScreen.HomeMatch:
                    case PlScreen.HomeRecommended:
                        if (++_attempts > 4)
                        {
                            _log("[天梯巅峰赛] 主页导航多轮未进入天梯巅峰赛，暂停 8s 后重试（若反复出现请回报日志）", LogLevel.Error);
                            _attempts = 0;
                            return 8000;
                        }
                        // 饱和归位（任意焦点/页签状态皆可达）：上×2 进页签行 → 左×4 到最左「推荐」
                        // → 右×1 到「比赛」页签 → 下进卡行（默认焦点即天梯巅峰赛）→ A 进入
                        _log("[天梯巅峰赛] 主页：上×2 左×4 归位推荐 → 右进比赛页签 → 下+A 进天梯巅峰赛", LogLevel.Info);
                        await _engine.Pad.DpadRepeat(PadDir.Up, 2);
                        await _engine.Pad.DpadRepeat(PadDir.Left, 4);
                        await _engine.Pad.Dpad(PadDir.Right);
                        await _engine.Pad.Dpad(PadDir.Down);
                        await _engine.Pad.Confirm();
                        return 3200;

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
                    if (++_attempts > 4)
                    {
                        _log("[天梯巅峰赛] 参赛模式弹窗多次未生效，B 取消回主页重走", LogLevel.Warn);
                        SetPhase(PlPhase.Flow);
                        await _engine.Pad.Back();
                        return 2200;
                    }
                    // 手柄光标默认在「取消」（底行）：上×2 到「单场比赛」→ A
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
                if (ml == MlScreen.InMatch)
                {
                    SetPhase(PlPhase.Flow); // 已开赛：交共享比赛流程，赛后屏幕驱动自动续
                    return null;
                }
                // 选择后可能弹出的确认/下一步弹窗：有推进词就按 A
                if (ml == MlScreen.HasConfirm ||
                    obs.Ocr.ContainsJoined("下一步") || obs.Ocr.ContainsJoined("确定") ||
                    obs.Ocr.ContainsJoined("确认") || obs.Ocr.FindButton("OK") is not null)
                {
                    if (++_attempts > 8) { SetPhase(PlPhase.Flow); return 600; }
                    _log("[天梯巅峰赛] 赛前弹窗：A 下一步", LogLevel.Info);
                    await _engine.Pad.Confirm();
                    return 2400;
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
            // 页面上可能有未分类弹窗：有推进词先按 A 清掉
            if (obs.Ocr.ContainsJoined("下一步") || obs.Ocr.ContainsJoined("确定") ||
                obs.Ocr.ContainsJoined("确认") || obs.Ocr.FindButton("OK") is not null)
            {
                _log("[天梯巅峰赛] 活动主页上有弹窗：A 清掉后再读场次", LogLevel.Info);
                await _engine.Pad.Confirm();
                return 2200;
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
