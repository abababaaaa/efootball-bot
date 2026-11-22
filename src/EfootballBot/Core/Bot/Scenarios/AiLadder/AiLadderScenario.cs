using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Bot.Scenarios.Weekly;
using EfootballBot.Core.Bot.Scenarios.PeakLadder;
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

namespace EfootballBot.Core.Bot.Scenarios.AiLadder;

/// <summary>
/// 国际服 AI 天梯联赛挂机场景（eFootball™联赛·对阵AI类别）：
/// 主菜单 下×2→左×6→A 进比赛页（下×2=新 UI 进底部卡行，用户 2026-10-19 实测） → 卡行扫格进「eFootball™联赛」（卡片图片会变，只认文字/槽位，
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

    private readonly MatchFlowMemory _memory = new();
    /// <summary>赛中匹配（共享层）：开赛/终场判定 + 场内挂机节奏 + 场次记账，五个模块同一份实现。</summary>
    private readonly MatchPlay _matchPlay;
    /// <summary>底层场次跟踪器（模块内读用时/关键比赛等细节用；比赛怎么算由 MatchPlay 决定）。</summary>
    private MatchSessionTracker _matchSession => _matchPlay.Session;
    /// <summary>赛后活动任务页（全屏活动地图）状态机：比赛结束后/开始前出现。</summary>
    private readonly PostMatchActivityController _postMatch = new();

    private AlPhase _phase = AlPhase.Flow;
    /// <summary>当前阶段累计行动次数（SetPhase/换屏清零；超限 B 逃生防死循环）。</summary>
    private int _attempts;
    /// <summary>比赛页扫格尝试数：第 n 次试槽位 (HubStartSlot + n) % 4（A 后屏幕级验证，不对 B 回换格）。</summary>
    private int _hubTry;
    /// <summary>比赛页整轮扫描（4 格）失败后 B 退主菜单重进的次数。</summary>
    private int _hubRound;
    /// <summary>本轮由 OCR 从磁贴行定位到的 eFootball联赛格位（null=未定位/走原固定扫描）。
    /// 磁贴随活动增减会左移（2026-11-22 活动结束后联赛从第3卡移到第2卡），故优先用实时定位。</summary>
    private int? _hubOcrPinned;
    /// <summary>组别主页整轮盲试（5 次盲归位开赛+B 退出）失败后由类别页重进的次数：识别退化
    /// 机器上主页永不被认出，靠它防 类别页↔主页 无限循环（>3 轮暂停回报）。</summary>
    private int _homeRound;

    private string _lastScreenName = "";

    private int _streak;

    private bool _takeoverLogged;
    /// <summary>TitleScreen 首次出现时间（比赛上下文中按 X 前宽限，区分加载画面与真标题）。</summary>
    private DateTime? _titleSince;

    private DateTime _lastMaxLevelAt = DateTime.MinValue;
    /// <summary>上次已播报的组别（变化才记日志，防每轮刷屏；0=尚未读到）。</summary>
    private int _divisionLogged;
    /// <summary>比赛页卡行中「eFootball™联赛」卡槽位（0 起；实机 2026-10-14 截图：第 3 卡）。</summary>
    private const int HubStartSlot = 2;

    public string Name => "AI天梯联赛";
    /// <summary>比赛中（供引擎把手柄诊断挡在比赛之外）。</summary>
    public bool InMatch => _matchSession.Active;

    public AiLadderScenario(BotEngine engine, Action<string, LogLevel> log)
    {
        _engine = engine;
        _log = log;
        _matchPlay = new MatchPlay(engine, "AI天梯联赛", log);
        _homeGuide = new ReturnToHomeGuide("AI天梯联赛", log, engine.DumpSuspiciousFrame);
    }

    /// <summary>启动引导：停在其他模块页时先 B 收敛回主页。</summary>
    private readonly ReturnToHomeGuide _homeGuide;

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var obs = await _engine.ObserveAsync(ct);
        var now = DateTime.Now;
        // 赛后活动任务页（全屏活动地图）优先拦截，不进共享分类盲按 A。
        var pmWait = await _postMatch.StepAsync(_engine, obs,
            _engine.Config.PostMatchActivity.AutoAdvanceWeeklyTasks, _log, Name, ct);
        if (pmWait is not null) return pmWait.Value;
        // 角区探针兜底 InMatch 需比赛上下文（同我的联赛/周活动场景）
        bool corner = ScreenProbe.CornerHasContent(obs.Frame);
        bool light = ScreenProbe.IsMostlyLightPage(obs.Frame);
        // 画面还是球场（进球庆祝/回放过场）→ 共享分类一律按比赛中处理（2026-11-20 虚计场次事故）
        bool pitch = ScreenProbe.LooksLikePitch(obs.Frame);
        bool matchContext = MatchShared.MatchContext(_memory, now);
        // 本模块界面先判（组别主页也含「前往比赛」，不先判会被共享分类误归 LeaguePage）；
        // 未命中再判周活动（比赛页活动板块扫格误入）残留页；两者都不是才交 MyLeagueBrain
        var al = AiLadderBrain.Classify(obs.Ocr);
        var wk = al == AlScreen.None
            ? WeeklyBrain.Classify(obs.Detections, obs.Ocr, corner, matchContext, light)
            : WkScreen.None;
        var ml = (al == AlScreen.None && wk == WkScreen.None)
            ? MatchBrain.Classify(obs.Detections, obs.Ocr, corner, matchContext, light,
                MatchShared.LoginRecovery(_memory, now), pitch)
            : MatchScreen.Unknown;
        _memory.Record(ml, now);
        // 启动引导：停在其他模块页（周活动/巅峰赛/我的联赛）时先 B 收敛回主页。
        bool otherModule = wk != WkScreen.None
                           || PeakLadderBrain.Classify(obs.Ocr, obs.Detections) != PlScreen.None
                           || (al == AlScreen.None &&
                               ml is MatchScreen.LeaguePage or MatchScreen.PointsPage or MatchScreen.ItemList);
        bool ownPage = al != AlScreen.None || ml is MatchScreen.MatchStartPage or MatchScreen.InMatch
                       or MatchScreen.DifficultyPage or MatchScreen.HasConfirm;
        int? guideWait = await _homeGuide.StepAsync(obs.Frame,
            ml == MatchScreen.MainMenu, ownPage, otherModule, () => _engine.Pad.Back());
        if (guideWait is not null) return guideWait.Value;
        if (!_takeoverLogged)
        {
            _takeoverLogged = true;
            _log($"[AI天梯联赛] 智能接管：当前界面「{DescribeScreen(al, wk, ml)}」，从该阶段继续", LogLevel.Success);
        }
        // ---- 赛中匹配（共享层 MatchPlay）：开赛/终场/超时判定 + 场次记账全在这里，
        //      模块只做自己的收尾（组别主页是本模块的"菜单"，到这里才允许开下一场）
        if (al == AlScreen.DivisionHome || al == AlScreen.CategorySelect) _matchPlay.MarkAtMenu();
        var tick = _matchPlay.Tick(ml, now, sawKeyMatchHint: false, isHome: false);
        if (tick.Started) _homeRound = 0; // 开赛成功=盲试配方有效，重置主页重试轮数
        if (tick.Ended && tick.EndedTrusted)
        {
            var cfg = _engine.Config.AiLadder;
            if (cfg.StopAfterMatches > 0 && _engine.ModuleMatchesDone >= cfg.StopAfterMatches)
            {
                _log($"[AI天梯联赛] 已完成设定场次（{_engine.ModuleMatchesDone}/{cfg.StopAfterMatches} 场），自动停止挂机", LogLevel.Success);
                _engine.Stop();
                return 0;
            }
        }
        // ---- 共用：球员满级提示（60s 节流，常开） ----
        if (obs.Ocr.ContainsJoined("满级") && (now - _lastMaxLevelAt).TotalSeconds > 60)
        {
            _lastMaxLevelAt = now;
            _engine.Stats.PlayersMaxed++;
            _log($"[AI天梯联赛] 球员满级提示（累计 {_engine.Stats.PlayersMaxed} 次）", LogLevel.Success);
            _engine.ReportMaxLevel("比赛中满级提示");
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
        var action = MatchBrain.Decide(ml, obs.Ocr, _streak, matchContext, obs.Frame);
        MatchShared.DecisionSnapshot(_log, "AI天梯联赛", ml, _streak, action, obs.Ocr);
        // 标题画面宽限（共享实现，含赛后弹窗链 90s 档）：比赛上下文中的品牌过场帧会
        // 连续出现 30s 以上，赛后弹窗链上下文宽限拉长到 90s
        if (action.Kind != MatchActionKind.PadXPress)
        {
            _titleSince = null;
        }
        else if (MatchShared.TitleGrace(_memory, now, ref _titleSince, _log, "AI天梯联赛"))
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
                    _log($"[AI天梯联赛] 主菜单导航：下×5→归位最左→右×{matchOff}→A（OCR 定位比赛在第 {matchOff + 1} 格）", LogLevel.Info);
                    await _engine.Pad.DpadRepeat(PadDir.Left, 6);
                    await _engine.Pad.DpadRepeat(PadDir.Right, matchOff);
                }
                else
                {
                    _log("[AI天梯联赛] 主菜单导航：下×5→左×6→A（OCR 未读全底部磁贴，回退饱和归位）", LogLevel.Info);
                    _engine.DumpSuspiciousFrame(obs.Frame, "menu_tile_ocr_incomplete", "主菜单底部磁贴 OCR 读不全，回退固定左×6 导航");
                    await _engine.Pad.DpadRepeat(PadDir.Left, 6);
                }
                await _engine.Pad.Confirm();
                break;
            case MatchActionKind.PadConfirm:
                _log($"[AI天梯联赛] 手柄 A：{action.Target}", LogLevel.Info);
                await _engine.Pad.Confirm();
                break;
            case MatchActionKind.PadUpConfirm:
                _log($"[AI天梯联赛] 弹窗上移确认：{action.Target}", LogLevel.Info);
                await _engine.Pad.Dpad(PadDir.Up);
                await _engine.Pad.Confirm();
                break;
            case MatchActionKind.PadDownConfirm:
                _log($"[AI天梯联赛] 弹窗下移确认：{action.Target}", LogLevel.Info);
                await _engine.Pad.Dpad(PadDir.Down);
                await _engine.Pad.Confirm();
                break;
            case MatchActionKind.PadStartPress:
                _log($"[AI天梯联赛] {action.Target}", LogLevel.Info);
                await _engine.Pad.Start();
                break;
            case MatchActionKind.PadXPress:
                _log($"[AI天梯联赛] {action.Target}", LogLevel.Info);
                await _engine.Pad.X();
                break;
            case MatchActionKind.PadLoginNav:
                // eFootball 2027 标题页登录序列：下→右→A（A 落在「登录」上，用户实机序列）
                _log($"[AI天梯联赛] {action.Target}", LogLevel.Info);
                await _engine.Pad.Dpad(PadDir.Down);
                await _engine.Pad.Dpad(PadDir.Right);
                await _engine.Pad.Confirm();
                break;
            case MatchActionKind.GamepadBack:
                _log($"[AI天梯联赛] {action.Target}", LogLevel.Warn);
                await _engine.Pad.Back();
                break;
            case MatchActionKind.GamepadBackAfterConfirm:
                _log($"[AI天梯联赛] {action.Target}", LogLevel.Warn);
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
    private async Task<int?> HandleAlAsync(AlScreen al, WkScreen wk, MatchScreen ml, Observation obs)
    {
        switch (_phase)
        {
            case AlPhase.Flow:
            {
                // 扫格途中按 A 打开了别的板块页（我的联赛/锦标赛/活动列表等）：
                // 不是类别页就 B 退回比赛页，下一轮换下一格（不落共享决策乱按）
                if (_hubTry > 0 && al == AlScreen.None &&
                    ml is MatchScreen.LeaguePage or MatchScreen.HasConfirm or MatchScreen.PointsPage or MatchScreen.ItemList)
                {
                    _log($"[AI天梯联赛] 打开的是其他板块页面（{ml}），B 退回比赛页继续找 eFootball™联赛", LogLevel.Info);
                    // OCR 定位命中却仍进错：说明该格定位有偏差，下一轮换一格试（不重复按同一格）
                    if (_hubOcrPinned is int pinned && pinned + 1 < 4)
                        _hubTry = pinned + 1 - HubStartSlot; // 使下轮固定扫描 slot=pinned+1
                    _hubOcrPinned = null;
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
                        _homeRound = 0;
                        SetPhase(AlPhase.Home);
                        return 700; // 下一轮读组别/查停止条件
                }
                if (ml == MatchScreen.MainMenu)
                {
                    // 导航多轮无效不再永久等待（≥NavEscapeStreak 周期性 B 清盖层，共享决策同款）
                    if (_streak >= MatchBrain.NavEscapeStreak && (_streak - MatchBrain.NavEscapeStreak) % 5 == 0)
                    {
                        _log("[AI天梯联赛] 主菜单导航多轮无效，B 清盖层/回上级重试", LogLevel.Warn);
                        await _engine.Pad.Back();
                        return 2400;
                    }
                    await _engine.Pad.DpadRepeat(PadDir.Down, 5); // 饱和下按到底部卡行（到底停住，防下少了按错）
                    int flowMatchOff = HubTileLocator.FindMainMenuMatchOffset(obs.Ocr) ?? 0;
                    if (flowMatchOff > 0)
                    {
                        _log($"[AI天梯联赛] 主菜单导航：下×5→归位最左→右×{flowMatchOff}→A（OCR 定位比赛在第 {flowMatchOff + 1} 格）", LogLevel.Info);
                        await _engine.Pad.DpadRepeat(PadDir.Left, 6);
                        await _engine.Pad.DpadRepeat(PadDir.Right, flowMatchOff);
                    }
                    else
                    {
                        _log("[AI天梯联赛] 主菜单导航：下×5→左×6→A（OCR 未读全底部磁贴，回退饱和归位）", LogLevel.Info);
                        _engine.DumpSuspiciousFrame(obs.Frame, "menu_tile_ocr_incomplete", "主菜单底部磁贴 OCR 读不全，回退固定左×6 导航");
                        await _engine.Pad.DpadRepeat(PadDir.Left, 6);
                    }
                    await _engine.Pad.Confirm();
                    return 3000;
                }
                if (ml == MatchScreen.MatchHub)
                {
                    // 此前 streak≥StuckStreak 永久等待排在恢复分支之前，会把下方的
                    // B 退出/暂停重扫自愈饿死（两计数并行增长，到 8 轮时恢复分支永远轮不到）
                    // ——改为放慢继续扫描，≥NavEscapeStreak 周期性 B 逃生。
                    if (_streak >= MatchBrain.NavEscapeStreak && (_streak - MatchBrain.NavEscapeStreak) % 5 == 0)
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
                    // 磁贴行随活动增减会左右漂移（2026-11-22 实机：活动卡消失后 eFootball™联赛
                    // 从第3卡移到第2卡，写死右×2 每次都按进「我的联赛」）。优先 OCR 实时定位。
                    int? ocrSlot = HubTileLocator.FindTileOffset(obs.Ocr, "eFootball联赛");
                    int slot;
                    if (ocrSlot is int os)
                    {
                        slot = os;
                        _hubOcrPinned = os;
                        _log($"[AI天梯联赛] 比赛页：OCR 定位「eFootball™联赛」在第 {slot + 1} 卡 → 右×{slot} → A", LogLevel.Info);
                    }
                    else
                    {
                        // OCR 读不全时回退原固定扫描（读不到就不瞎猜）
                        slot = (HubStartSlot + _hubTry) % 4;
                        _hubOcrPinned = null;
                        _log($"[AI天梯联赛] 比赛页：磁贴行读不全，回退固定扫描 右×{slot} → A（试第 {slot + 1} 卡）", LogLevel.Info);
                    }
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
                // 识别退化兜底（2026-10-21 反馈包实机：GDI 兜底捕获 + 2027 新 UI 美术字，
                // 组别主页 OCR 只剩 組別9/階段3/日期 碎片，前往比赛/组别分布/比赛场次全不可读
                // → Classify 恒 None、共享分类落 LeaguePage、旧流程 B 逃生成 类别页↔主页 死循环）：
                // 页面只要没被正判离开本模块（比赛中/周活动页/弹窗页等），一律继续按主页流程
                // 处理——盲归位配方本就 OCR 无关，识别成功只用于额外验证。
                bool onHome = al == AlScreen.DivisionHome;
                if (!onHome &&
                    !(al == AlScreen.None && wk == WkScreen.None && ml == MatchScreen.LeaguePage))
                {
                    // 已按 A 离开主页（开赛链/比赛中/赛后弹窗链）：交共享流程，赛后屏幕驱动自动回
                    SetPhase(AlPhase.Flow);
                    return null;
                }
                if (onHome) _homeRound = 0;
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
                    // 整轮盲试失败：识别退化机器上主页永远不被认出，B 退出后由类别页重进
                    // 会再次走到这里——_homeRound 预算防 类别页↔主页 无限循环
                    _homeRound++;
                    if (_homeRound > 3)
                    {
                        _log("[AI天梯联赛] 组别主页多轮盲试开赛未生效（反复出现请回报日志），暂停 30s 后重试", LogLevel.Error);
                        _homeRound = 0;
                        _hubTry = 0;
                        SetPhase(AlPhase.Flow);
                        return 30000;
                    }
                    _log($"[AI天梯联赛] 前往比赛多次未生效，B 退出联赛由类别页重进（第 {_homeRound} 轮，重进后光标回默认位）", LogLevel.Warn);
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
        // 乐观进入主页阶段：A 后的组别主页在捕获退化机器上识别不出（2026-10-21 反馈包），
        // 若留在 Flow 会被共享决策按「联赛页无关键词」B 逃生成死循环——Home 阶段对
        // 无特征 LeaguePage 页按盲归位配方开赛，不再依赖 Classify 先认出 DivisionHome
        _hubTry = 0;
        _hubRound = 0;
        SetPhase(AlPhase.Home);
        return 2800;
    }

    /// <summary>组别主页上的盖层弹窗探测（同周活动主页门控：推进词或屏幕中部确认按钮）。</summary>
    private static bool PopupOnTop(Observation obs) =>
        obs.Ocr.ContainsJoined("下一步") || obs.Ocr.ContainsJoined("确认") ||
        obs.Ocr.FindButton("OK") is not null ||
        obs.Detections.Any(d => d.ClassName == "btn_confirm" &&
                                d.Score >= MatchBrain.MinScore && d.CenterY < 0.85);
    private static string DescribeScreen(AlScreen al, WkScreen wk, MatchScreen ml) =>
        al != AlScreen.None ? $"AI天梯.{al}"
        : wk != WkScreen.None ? $"周活动.{wk}"
        : ml.ToString();
    private void SetPhase(AlPhase phase)
    {
        _phase = phase;
        _attempts = 0;
    }
}


