using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Bot.Scenarios.Weekly;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Bot.Scenarios.AiLadder;

/// <summary>
/// 国际服 AI 天梯联赛挂机场景（eFootball™联赛·对阵AI类别）：
/// 主菜单 下→左×6→A 进比赛页 → 卡行扫格进「eFootball™联赛」（卡片图片会变，只认文字/槽位，
/// 从实机第 3 卡起逐格试、按 A 后屏幕级验证）→ 类别选择页饱和左移选「对阵AI类别」
/// （卡片右下角组别角标数字/颜色会变，不作判定点）→ 组别主页盲归位（左×4+上×6）A 前往比赛
/// → 开赛链/比赛/赛后弹窗全部交共用流程（直接前往比赛即进比赛，无难度选择页）
/// → 赛后弹窗清完回组别主页自动循环。
/// 停止条件：完成场次（必设项，同我的联赛但无关键比赛）；达到设定组别（可选，检测主页左上角
/// 组别数字，10 最低 1 最高，达到即含更高组别）。
/// 判定只用固定 UI 词；卡片图片/角标/阶段日期/积分/分布百分比可变不作判定点。
/// 全程模拟手柄（国际服不响应后台鼠标）。
/// </summary>
public sealed class AiLadderScenario : IScenario
{
    /// <summary>菜单侧流程阶段（屏幕驱动；每阶段独立失败预算，超限 B 逃生重导航）。</summary>
    private enum AlPhase
    {
        /// <summary>屏幕驱动导航：主菜单→比赛页→类别选择→组别主页。</summary>
        Flow,
        /// <summary>组别主页：读组别/查停止条件 → A 前往比赛循环。</summary>
        Home,
    }

    private readonly BotEngine _engine;
    private readonly Action<string, LogLevel> _log;
    private readonly MlFlowMemory _memory = new();
    private readonly MatchSessionTracker _matchSession = new();
    private readonly Random _rnd = new();

    private AlPhase _phase = AlPhase.Flow;
    /// <summary>当前阶段累计行动次数（SetPhase/换屏清零；超限 B 逃生防死循环）。</summary>
    private int _attempts;
    /// <summary>比赛页扫格尝试数：第 n 次试槽位 (HubStartSlot + n) % 4（A 后屏幕级验证，不对 B 回换格）。</summary>
    private int _hubTry;
    /// <summary>比赛页整轮扫描（4 格）失败后 B 退主菜单重进的次数。</summary>
    private int _hubRound;

    private string _lastScreenName = "";
    private int _streak;
    private bool _takeoverLogged;
    /// <summary>TitleScreen 首次出现时间（比赛上下文中按 X 前宽限，区分加载画面与真标题）。</summary>
    private DateTime? _titleSince;
    private DateTime _lastTapLogAt = DateTime.MinValue;
    private DateTime _lastMaxLevelAt = DateTime.MinValue;
    /// <summary>上次已播报的组别（变化才记日志，防每轮刷屏；0=尚未读到）。</summary>
    private int _divisionLogged;

    /// <summary>比赛页卡行中「eFootball™联赛」卡槽位（0 起；实机 2026-10-14 截图：第 3 卡）。</summary>
    private const int HubStartSlot = 2;

    public string Name => "AI天梯联赛";

    public AiLadderScenario(BotEngine engine, Action<string, LogLevel> log)
    {
        _engine = engine;
        _log = log;
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var obs = await _engine.ObserveAsync(ct);
        var now = DateTime.Now;
        // 角区探针兜底 InMatch 需比赛上下文（同我的联赛/周活动场景）
        bool corner = MyLeagueScenario.CornerHasContent(obs.Frame);
        bool light = MyLeagueScenario.IsMostlyLightPage(obs.Frame);
        bool matchContext = ScenarioShared.MatchContext(_memory, now);

        // 本模块界面先判（组别主页也含「前往比赛」，不先判会被共享分类误归 LeaguePage）；
        // 未命中再判周活动（比赛页活动板块扫格误入）残留页；两者都不是才交 MyLeagueBrain
        var al = AiLadderBrain.Classify(obs.Ocr);
        var wk = al == AlScreen.None
            ? WeeklyBrain.Classify(obs.Detections, obs.Ocr, corner, matchContext, light)
            : WkScreen.None;
        var ml = (al == AlScreen.None && wk == WkScreen.None)
            ? MyLeagueBrain.Classify(obs.Detections, obs.Ocr, corner, matchContext, light)
            : MlScreen.Unknown;
        _memory.Record(ml, now);

        if (!_takeoverLogged)
        {
            _takeoverLogged = true;
            _log($"[AI天梯联赛] 智能接管：当前界面「{DescribeScreen(al, wk, ml)}」，从该阶段继续", LogLevel.Success);
        }

        // ---- 共用比赛流程：场次计数。不回我的联赛主页（isHome 恒 false），
        //      终场 FullTime 是权威关场信号，超时兜底 5 分钟
        bool wasActive = _matchSession.Active;
        bool matchEnded = _matchSession.Observe(ml, now, sawKeyMatchHint: false, isHome: false);
        if (!wasActive && _matchSession.Active)
            _log("[AI天梯联赛] 比赛开始：挂机等待比赛结束（按节奏随机点击）", LogLevel.Info);
        if (matchEnded)
        {
            _engine.Stats.MatchesDone++;
            _log($"[AI天梯联赛] 一场比赛结束（累计 {_engine.Stats.MatchesDone} 场）", LogLevel.Success);

            var cfg = _engine.Config.AiLadder;
            if (cfg.StopAfterMatches > 0 && _engine.Stats.MatchesDone >= cfg.StopAfterMatches)
            {
                _log($"[AI天梯联赛] 已完成设定场次（{_engine.Stats.MatchesDone}/{cfg.StopAfterMatches} 场），自动停止挂机", LogLevel.Success);
                _engine.Stop();
                return 0;
            }
        }

        // ---- 共用：球员满级提示（60s 节流，开关在联赛设置） ----
        if (_engine.Config.MyLeague.NotifyPlayerMaxLevel &&
            obs.Ocr.ContainsJoined("满级") && (now - _lastMaxLevelAt).TotalSeconds > 60)
        {
            _lastMaxLevelAt = now;
            _engine.Stats.PlayersMaxed++;
            _log($"[AI天梯联赛] 球员满级提示（累计 {_engine.Stats.PlayersMaxed} 次）", LogLevel.Success);
        }

        string screenName = DescribeScreen(al, wk, ml);
        if (screenName == _lastScreenName) _streak++;
        else
        {
            _streak = 0;
            _lastScreenName = screenName;
            _attempts = 0; // 换屏即重置当前阶段预算
            _log($"[AI天梯联赛] 界面：{screenName}", LogLevel.Info);
        }

        // ---- 菜单侧状态机：接管本模块界面与阶段内无特征界面 ----
        int? wait = await HandleAlAsync(al, wk, ml, obs);
        if (wait is > 0) return wait.Value;
        if (wait is not null) return 1500;
        // 未接管：走共用决策（比赛中/终场/弹窗/逃生）

        var action = MyLeagueBrain.Decide(ml, obs.Ocr, _streak, matchContext, obs.Frame);
        ScenarioShared.DecisionSnapshot(_log, "AI天梯联赛", ml, _streak, action, obs.Ocr);

        // 标题画面宽限（共享实现，含赛后弹窗链 90s 档）：比赛上下文中的品牌过场帧会
        // 连续出现 30s 以上，赛后弹窗链上下文宽限拉长到 90s
        if (action.Kind != MlActionKind.PadXPress)
        {
            _titleSince = null;
        }
        else if (ScenarioShared.TitleGrace(_memory, now, ref _titleSince, _log, "AI天梯联赛"))
        {
            return 2000;
        }

        switch (action.Kind)
        {
            case MlActionKind.PadEnterMatch:
                _log("[AI天梯联赛] 主菜单导航：下→左×6→A 进比赛页", LogLevel.Info);
                await _engine.Pad.Dpad(PadDir.Down);
                await _engine.Pad.DpadRepeat(PadDir.Left, 6);
                await _engine.Pad.Confirm();
                break;

            case MlActionKind.PadConfirm:
                _log($"[AI天梯联赛] 手柄 A：{action.Target}", LogLevel.Info);
                await _engine.Pad.Confirm();
                break;

            case MlActionKind.PadUpConfirm:
                _log($"[AI天梯联赛] 弹窗上移确认：{action.Target}", LogLevel.Info);
                await _engine.Pad.Dpad(PadDir.Up);
                await _engine.Pad.Confirm();
                break;

            case MlActionKind.PadStartPress:
                _log($"[AI天梯联赛] {action.Target}", LogLevel.Info);
                await _engine.Pad.Start();
                break;

            case MlActionKind.PadXPress:
                _log($"[AI天梯联赛] {action.Target}", LogLevel.Info);
                await _engine.Pad.X();
                break;

            case MlActionKind.GamepadBack:
                _log($"[AI天梯联赛] {action.Target}", LogLevel.Warn);
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
                    _log($"[AI天梯联赛] 比赛挂机中：随机点击 A（{waitMs / 1000.0:F1}s 间隔）", LogLevel.Debug);
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
    private async Task<int?> HandleAlAsync(AlScreen al, WkScreen wk, MlScreen ml, Observation obs)
    {
        switch (_phase)
        {
            case AlPhase.Flow:
            {
                // 扫格途中按 A 打开了别的板块页（我的联赛/锦标赛/活动列表等）：
                // 不是类别页就 B 退回比赛页，下一轮换下一格（不落共享决策乱按）
                if (_hubTry > 0 && al == AlScreen.None &&
                    ml is MlScreen.LeaguePage or MlScreen.HasConfirm or MlScreen.PointsPage or MlScreen.ItemList)
                {
                    _log($"[AI天梯联赛] 打开的是其他板块页面（{ml}），B 退回比赛页继续找 eFootball™联赛", LogLevel.Info);
                    await _engine.Pad.Back();
                    return 2200;
                }
                // 周活动板块残留页（对阵AI/对阵玩家 页签的活动列表，无「类别」后缀）同上；
                // 挑战清单弹窗（误入挑战活动）也不是本模块界面，一并 B 退回
                if (_hubTry > 0 && al == AlScreen.None &&
                    wk is WkScreen.EventsList or WkScreen.EventIntro or WkScreen.EventHome
                        or WkScreen.ChallengeList)
                {
                    _log($"[AI天梯联赛] 误入周活动板块（{wk}），B 退回比赛页", LogLevel.Info);
                    await _engine.Pad.Back();
                    return 2200;
                }

                switch (al)
                {
                    case AlScreen.CategorySelect:
                        return await HandleCategoryAsync();

                    case AlScreen.DivisionHome:
                        _hubTry = 0;
                        _hubRound = 0;
                        SetPhase(AlPhase.Home);
                        return 700; // 下一轮读组别/查停止条件
                }

                if (ml == MlScreen.MainMenu)
                {
                    // 导航多轮无效不再永久等待（≥NavEscapeStreak 周期性 B 清盖层，共享决策同款）
                    if (_streak >= MyLeagueBrain.NavEscapeStreak && (_streak - MyLeagueBrain.NavEscapeStreak) % 5 == 0)
                    {
                        _log("[AI天梯联赛] 主菜单导航多轮无效，B 清盖层/回上级重试", LogLevel.Warn);
                        await _engine.Pad.Back();
                        return 2400;
                    }
                    _log("[AI天梯联赛] 主菜单导航：下→左×6→A 进比赛页", LogLevel.Info);
                    await _engine.Pad.Dpad(PadDir.Down);
                    await _engine.Pad.DpadRepeat(PadDir.Left, 6);
                    await _engine.Pad.Confirm();
                    return 3000;
                }

                if (ml == MlScreen.MatchHub)
                {
                    // 此前 streak≥StuckStreak 永久等待排在恢复分支之前，会把下方的
                    // B 退出/暂停重扫自愈饿死（两计数并行增长，到 8 轮时恢复分支永远轮不到）
                    // ——改为放慢继续扫描，≥NavEscapeStreak 周期性 B 逃生。
                    if (_streak >= MyLeagueBrain.NavEscapeStreak && (_streak - MyLeagueBrain.NavEscapeStreak) % 5 == 0)
                    {
                        _log("[AI天梯联赛] 比赛页多轮无进展，B 退回主菜单重导航", LogLevel.Warn);
                        await _engine.Pad.Back();
                        return 2400;
                    }
                    if (_hubTry >= 4)
                    {
                        _hubRound++;
                        if (_hubRound > 2)
                        {
                            _log("[AI天梯联赛] 比赛页多轮未进入 eFootball™联赛，暂停 30s 后重扫（若反复出现请回报日志）", LogLevel.Error);
                            _hubTry = 0;
                            _hubRound = 0;
                            return 30000;
                        }
                        _log("[AI天梯联赛] 一轮扫描未中，B 退出由主菜单重进（重进后光标回默认位）", LogLevel.Warn);
                        _hubTry = 0;
                        await _engine.Pad.Back();
                        return 2500;
                    }
                    int slot = (HubStartSlot + _hubTry) % 4;
                    _log($"[AI天梯联赛] 比赛页：归位最左后右×{slot} → A（找 eFootball™联赛，试第 {slot + 1} 卡）", LogLevel.Info);
                    await _engine.Pad.DpadRepeat(PadDir.Left, 6);
                    if (slot > 0) await _engine.Pad.DpadRepeat(PadDir.Right, slot);
                    await _engine.Pad.Confirm();
                    _hubTry++; // 失败（B 回比赛页）时下一轮试下一卡；成功进入类别页后由 DivisionHome 分支清零
                    return 2800;
                }

                return null; // 其余共享界面（比赛中/终场/弹窗/未知逃生）交给共用决策
            }

            case AlPhase.Home:
            {
                if (al != AlScreen.DivisionHome)
                {
                    // 已按 A 离开主页（开赛链/比赛中/赛后弹窗链）：交共享流程，赛后屏幕驱动自动回
                    SetPhase(AlPhase.Flow);
                    return null;
                }

                // 盖层弹窗（赛后奖励/组别变动等，主页词透出仍可读）：先 A 清掉。
                // 绝不在弹窗上盲归位——多按钮弹窗上按方向键会改选（如 是/否）。
                // 主页自身无 下一步/确认/OK 词、前往比赛卡不被 YOLO 判 btn_confirm（周活动同款 UI 实测），
                // 命中即弹窗。
                if (PopupOnTop(obs))
                {
                    if (++_attempts > 10)
                    {
                        _log("[AI天梯联赛] 主页弹窗长时间未清完，B 退出联赛重进", LogLevel.Warn);
                        SetPhase(AlPhase.Flow);
                        _hubTry = 0;
                        await _engine.Pad.Back();
                        return 2400;
                    }
                    _log("[AI天梯联赛] 组别主页上有弹窗：A 推进", LogLevel.Info);
                    await _engine.Pad.Confirm();
                    return 2200;
                }

                if (_streak < 1) return 800; // 过渡帧防抖：主页稳定一拍再开始判断

                // 组别检测（可选停止条件）：主页左上角 组别X；变化才播报，防刷屏
                int? div = AiLadderBrain.ParseDivision(obs.Ocr);
                if (div is { } current)
                {
                    if (current != _divisionLogged)
                    {
                        _divisionLogged = current;
                        _log($"[AI天梯联赛] 当前组别 {current}", LogLevel.Success);
                    }
                    int stopAt = _engine.Config.AiLadder.StopAtDivision;
                    if (stopAt is >= 1 and <= 10 && current <= stopAt)
                    {
                        _log($"[AI天梯联赛] 已达到设定组别 {stopAt}（当前 {current}），自动停止挂机", LogLevel.Success);
                        _engine.Stop();
                        return 0;
                    }
                }

                if (++_attempts > 5)
                {
                    _log("[AI天梯联赛] 前往比赛多次未生效，B 退出联赛由比赛页重进（重进后光标回默认位）", LogLevel.Warn);
                    SetPhase(AlPhase.Flow);
                    _hubTry = 0;
                    await _engine.Pad.Back();
                    return 2400;
                }

                // 盲归位到「前往比赛」（主页左上卡）：Left×4 + Up×6 位置无关、到头多按无害
                // （同我的联赛主页左列链：前往比赛--下-->比赛历史，反向同链；i 图标在前往比赛右边）
                _log($"[AI天梯联赛] 盲归位到前往比赛（左×4+上×6）后开赛（第 {_attempts} 次）", LogLevel.Info);
                await _engine.Pad.DpadRepeat(PadDir.Left, 4);
                await _engine.Pad.DpadRepeat(PadDir.Up, 6);
                await _engine.Pad.Confirm();
                return 3000;
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// 类别选择页：左移归位「对阵AI类别」（行内最左卡，到头多按无害）后 A 进组别主页。
    /// 按前先确认页面已稳定一拍（进页过渡帧 OCR 常缺词）。
    /// </summary>
    private async Task<int> HandleCategoryAsync()
    {
        if (_streak < 1) return 800;
        if (++_attempts > 5)
        {
            _log("[AI天梯联赛] 类别页多次未进入组别主页，B 返回比赛页重新进入", LogLevel.Warn);
            await _engine.Pad.Back();
            return 2400;
        }
        _log("[AI天梯联赛] 类别页：左×2 归位「对阵AI类别」→ A 进入", LogLevel.Info);
        await _engine.Pad.DpadRepeat(PadDir.Left, 2);
        await _engine.Pad.Confirm();
        return 2800;
    }

    /// <summary>组别主页上的盖层弹窗探测（同周活动主页门控：推进词或屏幕中部确认按钮）。</summary>
    private static bool PopupOnTop(Observation obs) =>
        obs.Ocr.ContainsJoined("下一步") || obs.Ocr.ContainsJoined("确认") ||
        obs.Ocr.FindButton("OK") is not null ||
        obs.Detections.Any(d => d.ClassName == "btn_confirm" &&
                                d.Score >= MyLeagueBrain.MinScore && d.CenterY < 0.85);

    private static string DescribeScreen(AlScreen al, WkScreen wk, MlScreen ml) =>
        al != AlScreen.None ? $"AI天梯.{al}"
        : wk != WkScreen.None ? $"周活动.{wk}"
        : ml.ToString();

    private void SetPhase(AlPhase phase)
    {
        _phase = phase;
        _attempts = 0;
    }
}
