using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Bot.Scenarios.Weekly;

/// <summary>
/// 周活动挂机场景（国际服）。菜单侧状态机：主菜单 → 比赛页 → 活动板块 → 对阵AI列表
/// → 进入活动（介绍页+入场弹窗连点）→ 活动主页复查主要奖励 → X 核验活动设置
/// （赛事等级=配置值；自动控制强制开、弹性自动控制强制关）→ A 前往比赛。
/// 比赛中流程与我的联赛完全共用：InMatch 随机点击、终场、结算弹窗、满级检测、场次计数。
/// 赛后回到活动主页自动循环，直到主要奖励完成（探针校准前恒按"进行中"继续刷）。
/// 判定只用固定 UI 词与固定几何（活动名/图片/积分数字每周轮换，禁作判定点）。
/// </summary>
public sealed class WeeklyEventScenario : IScenario
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
        /// <summary>主要奖励已完成：B 返回活动列表换下一个（预留，校准前不进入）。</summary>
        Retreating,
        /// <summary>挑战活动「挑战清单」：饱和左移归位后逐卡右扫，找第一张
        /// （无锁且无勾且非本运行内已通关/已拉黑）的卡按 A 进入；扫到尾全勾 = 全通关退列表。</summary>
        ChallengeList,
        /// <summary>挑战全部完成/异常放弃：B 退介绍页退活动列表（或 B 后 A 重进清单刷新锁态）。</summary>
        ChallengeRetreat,
    }

    private readonly BotEngine _engine;
    private readonly Action<string, LogLevel> _log;
    private readonly Random _rnd = new();

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
    /// <summary>赛事等级改选尝试次数（超 2 次放弃改选，继续用游戏当前值）。</summary>
    private int _levelAttempts;

    // ---- 挑战活动子流程（挑战清单弹窗出现时激活；退回活动列表时复位） ----
    /// <summary>当前活动是挑战活动（以「挑战清单」弹窗或卡详情页「完成的条件」识别）。</summary>
    private bool _challengeMode;
    /// <summary>清单扫描游标：下一张待判定的卡号（1 起；饱和左移归位后从 1 向右扫）。</summary>
    private int _chScan = 1;
    /// <summary>清单扫描是否已归位（进阶段后先饱和左移到卡 1）。</summary>
    private bool _chScanReady;
    /// <summary>游标卡号连续未见于 OCR 的帧数（≥2 判定扫过表尾 = 全部通关）。</summary>
    private int _chMissing;
    /// <summary>本运行内已确认通关的卡号（进卡页读到 胜场 N/N 记录；绿勾探针未校准的兜底）。</summary>
    private readonly HashSet<int> _chLocalDone = new();
    /// <summary>进卡多次未生效的卡号（游戏拒绝进入/锁误判，跳过防死循环）。</summary>
    private readonly HashSet<int> _chBlocked = new();
    /// <summary>最近一次按 A 进入的卡号（重复进入同一卡计数用）。</summary>
    private int _chCurrentCard;
    /// <summary>对同一张卡连续按 A 进入的次数（超 2 拉黑该卡）。</summary>
    private int _chReentry;
    /// <summary>锁态矛盾（游标越过已通关卡却撞锁）次数；超 2 放弃本活动拉黑槽位。</summary>
    private int _chContradictions;
    /// <summary>ChallengeRetreat 子模式：B 退到介绍页后按 A 重进清单（刷新锁态用）。</summary>
    private bool _chRetreatReenter;
    /// <summary>ChallengeRetreat 子模式：放弃本活动（返回列表后拉黑槽位再换下一个）。</summary>
    private bool _chRetreatBlacklist;

    private string _lastScreenName = "";
    private int _streak;
    private readonly MlFlowMemory _memory = new();
    private readonly MatchSessionTracker _matchSession = new();
    private DateTime _lastTapLogAt = DateTime.MinValue;
    private DateTime _lastMaxLevelAt = DateTime.MinValue;
    private bool _takeoverLogged;
    /// <summary>TitleScreen 首次出现时间（比赛上下文中按 X 前 30s 宽限，区分加载画面与真标题）。</summary>
    private DateTime? _titleSince;
    /// <summary>"完成态探针未校准"提示只记一次（右栏读不出主要奖励字样时）。</summary>
    private bool _probeNoteLogged;

    public string Name => "周活动";

    public WeeklyEventScenario(BotEngine engine, Action<string, LogLevel> log)
    {
        _engine = engine;
        _log = log;
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var obs = await _engine.ObserveAsync(ct);
        var now = DateTime.Now;
        // 角区探针兜底 InMatch 需比赛上下文（同我的联赛场景）
        bool corner = MyLeagueScenario.CornerHasContent(obs.Frame);
        bool light = MyLeagueScenario.IsMostlyLightPage(obs.Frame);
        bool matchContext = _memory.SawWithin(MlScreen.InMatch, TimeSpan.FromSeconds(90), now) ||
                            _memory.SawWithin(MlScreen.MatchStartPage, TimeSpan.FromSeconds(90), now);

        // 周活动界面先判：活动主页也含「前往比赛」，不先判会被共享分类误归 LeaguePage。
        // 命中周活动界面时 ml 记 Unknown（由状态机处理，不走共享决策）
        var wk = WeeklyBrain.Classify(obs.Detections, obs.Ocr, corner, matchContext, light);
        var ml = wk == WkScreen.None
            ? MyLeagueBrain.Classify(obs.Detections, obs.Ocr, corner, matchContext, light)
            : MlScreen.Unknown;
        _memory.Record(ml, now);

        if (!_takeoverLogged)
        {
            _takeoverLogged = true;
            _log($"[周活动] 智能接管：当前界面「{(wk != WkScreen.None ? wk.ToString() : ml.ToString())}」，从该阶段继续", LogLevel.Success);
        }

        // ---- 共用比赛流程：场次/关键比赛计数。周活动流程不回我的联赛主页（isHome 恒 false），
        //      终场 FullTime 是权威关场信号，超时兜底 5 分钟
        bool wasActive = _matchSession.Active;
        bool matchEnded = _matchSession.Observe(ml, now,
            sawKeyMatchHint: ml == MlScreen.KeyMatchDialog || obs.Ocr.ContainsJoined("关键比赛"),
            isHome: false);
        if (!wasActive && _matchSession.Active)
            _log("[周活动] 比赛开始：挂机等待比赛结束（按节奏随机点击）", LogLevel.Info);
        if (matchEnded)
        {
            _engine.Stats.MatchesDone++;
            if (_matchSession.WasKeyMatch) _engine.Stats.KeyMatches++;
            _log($"[周活动] 一场比赛结束（{(_matchSession.WasKeyMatch ? "关键比赛，" : "")}累计 {_engine.Stats.MatchesDone} 场）", LogLevel.Success);

            var wkCfg = _engine.Config.Weekly;
            if (wkCfg.StopAfterMatches > 0 && _engine.Stats.MatchesDone >= wkCfg.StopAfterMatches)
            {
                _log($"[周活动] 已完成设定场次（{_engine.Stats.MatchesDone}/{wkCfg.StopAfterMatches} 场），自动停止挂机", LogLevel.Success);
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
            _log($"[周活动] 球员满级提示（累计 {_engine.Stats.PlayersMaxed} 次）", LogLevel.Success);
        }

        string screenName = wk != WkScreen.None ? $"周活动.{wk}" : ml.ToString();
        if (screenName == _lastScreenName) _streak++;
        else { _streak = 0; _lastScreenName = screenName; _log($"[周活动] 界面：{screenName}", LogLevel.Info); }

        // ---- 周活动菜单侧状态机：接管周活动界面与阶段内无特征界面 ----
        int? weeklyWait = await HandleWeeklyAsync(wk, ml, obs);
        if (weeklyWait is > 0) return weeklyWait.Value;
        if (weeklyWait is not null) return 1500;
        // wk==None 且阶段已交回共享流程：走共用决策（比赛中/终场/弹窗/逃生）

        var action = MyLeagueBrain.Decide(ml, obs.Ocr, _streak, matchContext);

        // 标题画面宽限（同我的联赛场景）：比赛上下文中的「eFootball™+X」帧多为加载画面，
        // 前 30s 只等待不按 X；真实标题画面停留超宽限期后照常按 X。
        // 赛后弹窗链上下文（刚见过终场）：品牌过场帧会连续出现 30s 以上
        //（实机 2026-10-12：全场结束后 33s 处按 X 打断了弹窗链），宽限拉长到 90s
        if (action.Kind != MlActionKind.PadXPress)
        {
            _titleSince = null;
        }
        else
        {
            bool recentFlow =
                _memory.SawWithin(MlScreen.InMatch, TimeSpan.FromMinutes(3), now) ||
                _memory.SawWithin(MlScreen.MatchStartPage, TimeSpan.FromMinutes(3), now);
            bool postMatchChain = _memory.SawWithin(MlScreen.FullTime, TimeSpan.FromMinutes(2), now);
            var grace = postMatchChain ? TimeSpan.FromSeconds(90) : TimeSpan.FromSeconds(30);
            _titleSince ??= now;
            if (recentFlow && now - _titleSince.Value < grace)
            {
                _log("[周活动] eFootball 标志帧（疑似比赛加载/赛后过场画面），暂不按 X，继续观察", LogLevel.Info);
                return 2000;
            }
        }

        switch (action.Kind)
        {
            case MlActionKind.PadEnterMatch:
                _log("[周活动] 主菜单导航：下→左×6→A 进比赛页", LogLevel.Info);
                await _engine.Pad.Dpad(PadDir.Down);
                await _engine.Pad.DpadRepeat(PadDir.Left, 6);
                await _engine.Pad.Confirm();
                break;

            case MlActionKind.PadConfirm:
                _log($"[周活动] 手柄 A：{action.Target}", LogLevel.Info);
                await _engine.Pad.Confirm();
                break;

            case MlActionKind.PadUpConfirm:
                _log($"[周活动] 弹窗上移确认：{action.Target}", LogLevel.Info);
                await _engine.Pad.Dpad(PadDir.Up);
                await _engine.Pad.Confirm();
                break;

            case MlActionKind.PadXPress:
                _log($"[周活动] {action.Target}", LogLevel.Info);
                await _engine.Pad.X();
                break;

            case MlActionKind.GamepadBack:
                _log($"[周活动] {action.Target}", LogLevel.Warn);
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
                    _log($"[周活动] 比赛挂机中：随机点击 A（{waitMs / 1000.0:F1}s 间隔）", LogLevel.Debug);
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
    private async Task<int?> HandleWeeklyAsync(WkScreen wk, MlScreen ml, Observation obs)
    {
        switch (_phase)
        {
            case WkPhase.Flow:
            {
                switch (wk)
                {
                    case WkScreen.EventsList:
                        return await EnterNextEventAsync(obs);
                    case WkScreen.EventIntro:
                    case WkScreen.EntryPopup:
                        _hubStep = 0; // 已进入活动链：比赛页扫描结束（防赛后误触发扫描分支）
                        _hubRound = 0;
                        SetPhase(WkPhase.IntroPopups);
                        return await PressConfirmAsync(wk == WkScreen.EventIntro ? "介绍页：进入" : "入场弹窗：确认");
                    case WkScreen.EventHome:
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
                        return EnterChallengeListPhase();
                }
                if (ml == MlScreen.MainMenu)
                {
                    if (_streak >= MyLeagueBrain.StuckStreak) return 5000;
                    _log("[周活动] 主菜单导航：下→左×6→A 进比赛页", LogLevel.Info);
                    await _engine.Pad.Dpad(PadDir.Down);
                    await _engine.Pad.DpadRepeat(PadDir.Left, 6);
                    await _engine.Pad.Confirm();
                    return 3000;
                }
                if (ml == MlScreen.MatchHub)
                {
                    if (_streak >= MyLeagueBrain.StuckStreak) return 5000;
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
                    _log($"[周活动] 比赛页：归位最左后右×{_hubStep} → A（试第 {_hubStep + 1}/4 格找活动板块）", LogLevel.Info);
                    await _engine.Pad.DpadRepeat(PadDir.Left, 6);
                    if (_hubStep > 0) await _engine.Pad.DpadRepeat(PadDir.Right, _hubStep);
                    await _engine.Pad.Confirm();
                    _hubStep++; // 失败（B 回比赛页）时下一轮试下一格；成功由 EnterNextEventAsync 清零
                    return 2800;
                }
                // 扫描途中按 A 打开了别的板块页面（锦标赛/eFootball联赛/我的联赛子页等）：
                // 不是活动列表就 B 退回比赛页，下一轮换下一格（不落共享决策乱按）
                if (_hubStep > 0 && wk == WkScreen.None &&
                    ml is MlScreen.LeaguePage or MlScreen.HasConfirm or MlScreen.PointsPage or MlScreen.ItemList)
                {
                    _log($"[周活动] 打开的是其他板块页面（{ml}），B 退回比赛页继续扫描", LogLevel.Info);
                    await _engine.Pad.Back();
                    return 2200;
                }
                return null; // 其余共享界面（比赛中/弹窗/未知逃生）交给共用决策
            }

            case WkPhase.IntroPopups:
            {
                if (wk == WkScreen.EventHome) { SetPhase(WkPhase.AtHome); return 600; }
                if (wk == WkScreen.EventsList) { SetPhase(WkPhase.Flow); return 800; }
                if (wk == WkScreen.EventIntro) return await PressConfirmAsync("介绍页：进入");
                if (wk == WkScreen.ChallengeList)
                    return EnterChallengeListPhase(); // 挑战活动：介绍页按进入后弹的是挑战清单
                if (wk == WkScreen.EntryPopup) return await PressConfirmAsync("入场弹窗：确认");
                if (ml == MlScreen.HasConfirm) return await PressConfirmAsync("入场弹窗：下一步");
                if (++_attempts > 15)
                {
                    // 比赛上下文中的过场（加载/品牌帧）可能连续超过预算：B 在比赛附近危险
                    //（铲球/误退出），只继续等待；菜单上下文才 B 退回列表重试
                    bool nearMatch = _memory.SawWithin(MlScreen.InMatch, TimeSpan.FromMinutes(2), DateTime.Now) ||
                                     _memory.SawWithin(MlScreen.MatchStartPage, TimeSpan.FromMinutes(2), DateTime.Now);
                    if (!nearMatch)
                    {
                        _log("[周活动] 入场弹窗长时间未结束，B 退回列表重试", LogLevel.Warn);
                        SetPhase(WkPhase.Flow);
                        await _engine.Pad.Back();
                        return 2200;
                    }
                }
                // 未识别的入场弹窗：OCR 有推进词就点（同比赛中弹窗规则），否则等待
                if (obs.Ocr.ContainsJoined("下一步") || obs.Ocr.ContainsJoined("OK") ||
                    obs.Ocr.ContainsJoined("确认") || obs.Ocr.ContainsJoined("进入"))
                    return await PressConfirmAsync("入场推进");
                return 1500;
            }

            case WkPhase.AtHome:
            {
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
                if (ml == MlScreen.DifficultyPage)
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
                if (wk == WkScreen.EventHome)
                {
                    if (++_attempts > 4)
                    {
                        _log("[周活动] 前往比赛多次未生效，B 退回列表重新走流程", LogLevel.Warn);
                        SetPhase(WkPhase.Flow);
                        await _engine.Pad.Back();
                        return 2200;
                    }
                    _log("[周活动] 手柄 A：前往比赛", LogLevel.Info);
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
                if (wk is WkScreen.EventHome or WkScreen.EventIntro)
                {
                    if (++_attempts <= 3) { await _engine.Pad.Back(); return 2200; }
                    SetPhase(WkPhase.Flow);
                    return 800;
                }
                if (wk == WkScreen.EntryPopup)
                    return await PressConfirmAsync("返回途中弹窗：确认"); // 先清弹窗再回列表
                if (ml == MlScreen.HasConfirm)
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

            case WkPhase.ChallengeRetreat:
                return await HandleChallengeRetreatAsync(wk, ml);

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
        {
            _log("[周活动] 可见活动槽位已全部完成，周活动挂机结束", LogLevel.Success);
            _engine.Stop();
            return 0;
        }
        if (_slotIndex > 0) await _engine.Pad.DpadRepeat(PadDir.Right, _slotIndex);

        _log($"[周活动] 活动列表：进入槽位 {_slotIndex}", LogLevel.Info);
        SetPhase(WkPhase.IntroPopups);
        await _engine.Pad.Confirm();
        return 2800;
    }

    /// <summary>活动设置浮层：逐项核验修正。每轮最多一个动作（改完下一轮重新解析再验下一项），
    /// 全部就绪或读不到（保守不按）时 B 返回。改选等级超预算则带 Warn 放弃，绝不卡死。</summary>
    private async Task<int> HandleSettingsAsync(WkScreen wk, MlScreen ml, Observation obs)
    {
        if (wk == WkScreen.EventSettings)
        {
            var cfg = _engine.Config.Weekly;
            var s = WeeklyBrain.ParseActivitySettings(obs.Ocr);

            // 挑战活动设置浮层：没有赛事等级行（难度由卡片固定），只核验自动控制/弹性。
            // 2 行布局、光标默认在「自动控制」行——导航计步与普通浮层（3 行、默认在等级行）不同。
            if (_challengeMode || obs.Ocr.ContainsJoined("完成的条件"))
                return await HandleChallengeSettingsAsync(s);

            var unread = new List<string>();
            if (s.Level is null) unread.Add("赛事等级");
            if (s.AutoControl is null) unread.Add("自动控制");
            if (s.FlexibleAutoControl is null) unread.Add("弹性自动控制");
            if (unread.Count > 0)
                _log($"[周活动] 设置项未读出（{string.Join("、", unread)}），保守跳过不按键", LogLevel.Warn);

            if (++_attempts > 6)
            {
                _log("[周活动] 设置核验多轮未收敛，B 返回直接开赛", LogLevel.Warn);
                SetPhase(WkPhase.LaunchReady);
                await _engine.Pad.Back();
                return 2000;
            }

            // 1) 赛事等级（光标默认在等级行：饱和上移归位后直接 A）
            if (s.Level is not null && s.Level != cfg.EventLevel)
            {
                if (_levelAttempts < 2)
                {
                    _levelAttempts++;
                    _log($"[周活动] 赛事等级 {s.Level} → {cfg.EventLevel}：展开下拉（第 {_levelAttempts} 次）", LogLevel.Info);
                    SetPhase(WkPhase.LevelDropdown);
                    await _engine.Pad.DpadRepeat(PadDir.Up, 4);
                    await _engine.Pad.Confirm();
                    return 2200;
                }
                _log($"[周活动] 赛事等级改选未成功（当前 {s.Level}），继续用当前等级", LogLevel.Warn);
            }
            else if (s.Level == cfg.EventLevel)
            {
                _levelAttempts = 0;
            }

            // 2) 自动控制强制开：归位后下移 2 次
            if (s.AutoControl == false)
            {
                _log("[周活动] 自动控制为关：上×4 归位 → 下×2 → A 改为开（强制项）", LogLevel.Info);
                await _engine.Pad.DpadRepeat(PadDir.Up, 4);
                await _engine.Pad.DpadRepeat(PadDir.Down, 2);
                await _engine.Pad.Confirm();
                return 2000;
            }
            // 3) 弹性自动控制强制关：归位后下移 3 次
            if (s.FlexibleAutoControl == true)
            {
                _log("[周活动] 弹性自动控制为开：上×4 归位 → 下×3 → A 改为关（强制项）", LogLevel.Info);
                await _engine.Pad.DpadRepeat(PadDir.Up, 4);
                await _engine.Pad.DpadRepeat(PadDir.Down, 3);
                await _engine.Pad.Confirm();
                return 2000;
            }

            _log("[周活动] 设置核验完成，B 返回活动主页", LogLevel.Success);
            SetPhase(WkPhase.LaunchReady);
            await _engine.Pad.Back();
            return 1800;
        }

        if (wk == WkScreen.EventHome)
        {
            if (++_attempts <= 2)
            {
                _log("[周活动] 活动设置未打开，重按 X", LogLevel.Warn);
                await _engine.Pad.X();
                return 1800;
            }
            _log("[周活动] 活动设置打不开，跳过核验直接开赛", LogLevel.Warn);
            SetPhase(WkPhase.LaunchReady);
            return 600;
        }
        if (wk == WkScreen.EntryPopup)
            return await PressConfirmAsync("设置层弹窗：确认"); // 盖在设置层上的弹窗先清掉
        if (ml == MlScreen.HasConfirm)
        {
            SetPhase(WkPhase.IntroPopups); // 设置层上叠了弹窗：交给入场弹窗推进
            return await PressConfirmAsync("设置层弹窗：确认");
        }
        if (++_attempts > 4) { SetPhase(WkPhase.Flow); return 600; }
        return 1200;
    }

    /// <summary>赛事等级下拉选项（复用我的联赛难度页检测：蓝勾=当前所选，N+1-k 公式改选）。
    /// 下拉真实样式待实机校准；识别不出时由 LevelDropdown 的 B 收起兜底，绝不乱选。</summary>
    private async Task<int> HandleLevelOptionsAsync(Capture.FrameData frame, OcrResult ocr)
    {
        string wanted = _engine.Config.Weekly.EventLevel;
        var options = MyLeagueBrain.DetectDifficultyOptions(ocr);
        var selected = MyLeagueBrain.DetectSelectedDifficulty(frame, ocr);

        SetPhase(WkPhase.Settings); // 选完（无论是否改选）都回浮层重新核验

        // 所选不在列表 / 蓝勾未检出：按游戏默认直接确认（同我的联赛守则，绝不乱按方向）
        if (!options.Contains(wanted) || selected is null)
        {
            _log($"[周活动] 赛事等级下拉：所选「{selected ?? "未检出"}」，直接确认", LogLevel.Info);
            await _engine.Pad.Confirm();
            return 2500;
        }
        if (selected == wanted)
        {
            _log($"[周活动] 赛事等级已是「{wanted}」，直接确认", LogLevel.Success);
            await _engine.Pad.Confirm();
            return 2500;
        }
        int n = options.Count, k = options.IndexOf(wanted) + 1, steps = n + 1 - k;
        _log($"[周活动] 赛事等级 {selected} → {wanted}（{n} 项第 {k} 行）：上×{steps} 勾选 → 下×{steps} 右×1 确认", LogLevel.Info);
        await _engine.Pad.DpadRepeat(PadDir.Up, steps);
        await _engine.Pad.Confirm();
        await _engine.Pad.DpadRepeat(PadDir.Down, steps);
        await _engine.Pad.Dpad(PadDir.Right);
        await _engine.Pad.Confirm();
        return 3500;
    }

    // ---------------- 挑战活动子流程 ----------------

    /// <summary>进入挑战子流程：激活挑战模式并切到清单扫描阶段（下一轮先饱和左移归位）。</summary>
    private int EnterChallengeListPhase()
    {
        _hubStep = 0;
        _hubRound = 0;
        if (!_challengeMode)
        {
            _challengeMode = true;
            _log("[周活动] 检测到挑战清单：进入挑战子流程（逐卡挑战，卡难度固定不可选）", LogLevel.Success);
        }
        SetPhase(WkPhase.ChallengeList);
        return 1500;
    }

    /// <summary>
    /// 挑战清单扫描（用户规则：永远打第一张既没锁又没勾的卡；等价于「锁前面那张 /
    /// 无锁时最后一张 / 最后一个勾的下一张」）。归位后逐卡右扫：已通关（绿勾/本运行
    /// 内已通关/已拉黑）→ 右移；待挑战 → A 进入卡详情页；撞锁 = 锁态矛盾 → B 退出
    /// 重进清单刷新（超限放弃本活动）；游标卡号消失 = 扫过表尾 → 全部通关退列表。
    /// 5 张以上需要翻页：右移会自动滚动列表，卡号 OCR 跟踪当前卡位（左/右卡位固定，
    /// 卡号随翻页变化），游标卡不在屏上即表尾或丢帧，两帧确认防动画误判。
    /// </summary>
    private async Task<int> HandleChallengeListAsync(WkScreen wk, MlScreen ml, Observation obs)
    {
        if (wk == WkScreen.EventHome) { _chReentry = 0; SetPhase(WkPhase.AtHome); return 600; }
        if (wk == WkScreen.EventSettings) { SetPhase(WkPhase.Settings); return 800; }
        if (wk == WkScreen.EventsList) { _challengeMode = false; SetPhase(WkPhase.Flow); return 800; }
        if (wk == WkScreen.EntryPopup || ml == MlScreen.HasConfirm)
        {
            if (++_attempts > 6) { SetPhase(WkPhase.Flow); return 600; }
            return await PressConfirmAsync("挑战清单弹窗：确认");
        }
        if (wk != WkScreen.ChallengeList)
        {
            if (++_attempts > 10) { SetPhase(WkPhase.Flow); return 600; } // 过场/未知帧超预算：回屏幕驱动自愈
            return 1200;
        }

        if (!_chScanReady)
        {
            _chScanReady = true;
            _chScan = 1;
            _chMissing = 0;
            _log("[周活动·挑战] 挑战清单：饱和左移归位到第 1 张卡", LogLevel.Info);
            await _engine.Pad.DpadRepeat(PadDir.Left, 6);
            return 1500;
        }

        var cur = WeeklyBrain.ScanChallengeCards(obs.Ocr, obs.Frame)
            .FirstOrDefault(c => c.Index == _chScan);
        if (cur is null)
        {
            // 游标卡号不可见：翻页动画丢帧，或已扫过最后一张（1..N-1 全部通关）
            if (++_chMissing < 2) return 1000;
            _log($"[周活动·挑战] 卡 {_chScan:00} 之后没有更多卡：全部挑战已完成，B 退出挑战活动", LogLevel.Success);
            _chRetreatReenter = false;
            _chRetreatBlacklist = false;
            SetPhase(WkPhase.ChallengeRetreat);
            await _engine.Pad.Back();
            return 2200;
        }
        _chMissing = 0;

        if (cur.Locked)
        {
            // 游标越过已通关卡却撞锁：游戏解锁可能有延迟（通关即打勾、重进清单才解锁），
            // B 退介绍页重进刷新锁态；重进仍锁 = 真矛盾（活动过期/进度异常），放弃本活动
            _chContradictions++;
            _chRetreatBlacklist = _chContradictions > 2;
            _log($"[周活动·挑战] 卡 {cur.Index:00} 显示锁定但前置卡应已通关" +
                 (_chRetreatBlacklist ? "，重进未解锁，放弃本活动（拉黑槽位）" : $"，B 重进清单刷新锁态（第 {_chContradictions} 次）"),
                _chRetreatBlacklist ? LogLevel.Error : LogLevel.Warn);
            _chRetreatReenter = !_chRetreatBlacklist;
            SetPhase(WkPhase.ChallengeRetreat);
            await _engine.Pad.Back();
            return 2200;
        }

        if (cur.Checked || _chLocalDone.Contains(cur.Index) || _chBlocked.Contains(cur.Index))
        {
            string why = cur.Checked ? "右上角绿勾"
                : _chLocalDone.Contains(cur.Index) ? "本运行内已通关" : "多次进入未生效已拉黑";
            _chScan++;
            _log($"[周活动·挑战] 卡 {cur.Index:00} 已完成（{why}），右移下一张", LogLevel.Info);
            await _engine.Pad.Dpad(PadDir.Right);
            return 1100;
        }

        // 待挑战卡：按 A 进入（重复进入同一卡计数，超 2 次拉黑防死循环）
        if (_chCurrentCard == cur.Index)
        {
            if (++_chReentry > 2)
            {
                _chBlocked.Add(cur.Index);
                _chScan++;
                _log($"[周活动·挑战] 卡 {cur.Index:00} 连续进入未生效，拉黑跳过", LogLevel.Warn);
                await _engine.Pad.Dpad(PadDir.Right);
                return 1100;
            }
        }
        else
        {
            _chCurrentCard = cur.Index;
            _chReentry = 1;
        }
        _log($"[周活动·挑战] 进入卡 {cur.Index:00}", LogLevel.Info);
        await _engine.Pad.Confirm();
        return 2600;
    }

    /// <summary>挑战卡详情页（活动主页样式）：已通关（胜场读满/主要奖励完成）→ 记忆并
    /// B 回清单换下一张；未通关 → X 开设置核验（无难度，只看自动控制/弹性）后开赛。</summary>
    private async Task<int> HandleChallengeCardHomeAsync(WkScreen wk, Observation obs)
    {
        _challengeMode = true; // 半途启动直接落在卡详情页（「完成的条件」特征）也激活挑战模式
        if (wk == WkScreen.ChallengeList) { SetPhase(WkPhase.ChallengeList); return 800; }
        if (wk == WkScreen.EventSettings) { SetPhase(WkPhase.Settings); return 800; }
        if (wk != WkScreen.EventHome)
        {
            SetPhase(WkPhase.Flow); // 弹窗/过场：回屏幕驱动推进，清完再回主页判断
            return 600;
        }
        if (WeeklyBrain.IsMainRewardDone(obs.Ocr, obs.Frame) || WeeklyBrain.ChallengeProgressDone(obs.Ocr))
        {
            if (_chCurrentCard > 0) _chLocalDone.Add(_chCurrentCard);
            _log($"[周活动·挑战] 卡 {_chCurrentCard:00} 已通关，B 返回挑战清单换下一张", LogLevel.Success);
            SetPhase(WkPhase.ChallengeList);
            await _engine.Pad.Back();
            return 2200;
        }
        SetPhase(WkPhase.Settings);
        await _engine.Pad.X();
        _log("[周活动·挑战] 按 X 打开活动设置（挑战卡无难度，只核验自动控制/弹性）", LogLevel.Info);
        return 1800;
    }

    /// <summary>挑战活动设置浮层（2 行：自动控制/弹性自动控制，光标默认在首行）：
    /// 自动控制强制开（归位直接 A）、弹性强制关（归位下移 1 次 A），就绪 B 返回。
    /// 挑战卡没有赛事等级行——难度由卡片固定，绝不能沿用普通浮层的下×2/下×3 计步。</summary>
    private async Task<int> HandleChallengeSettingsAsync(WkActivitySettings s)
    {
        var unread = new List<string>();
        if (s.AutoControl is null) unread.Add("自动控制");
        if (s.FlexibleAutoControl is null) unread.Add("弹性自动控制");
        if (unread.Count > 0)
            _log($"[周活动·挑战] 设置项未读出（{string.Join("、", unread)}），保守跳过不按键", LogLevel.Warn);

        if (++_attempts > 6)
        {
            _log("[周活动·挑战] 设置核验多轮未收敛，B 返回直接开赛", LogLevel.Warn);
            SetPhase(WkPhase.LaunchReady);
            await _engine.Pad.Back();
            return 2000;
        }

        if (s.AutoControl == false)
        {
            _log("[周活动·挑战] 自动控制为关：上×4 归位 → A 改为开（强制项）", LogLevel.Info);
            await _engine.Pad.DpadRepeat(PadDir.Up, 4);
            await _engine.Pad.Confirm();
            return 2000;
        }
        if (s.FlexibleAutoControl == true)
        {
            _log("[周活动·挑战] 弹性自动控制为开：上×4 归位 → 下×1 → A 改为关（强制项）", LogLevel.Info);
            await _engine.Pad.DpadRepeat(PadDir.Up, 4);
            await _engine.Pad.Dpad(PadDir.Down);
            await _engine.Pad.Confirm();
            return 2000;
        }

        _log("[周活动·挑战] 设置核验完成，B 返回卡详情页开赛", LogLevel.Success);
        SetPhase(WkPhase.LaunchReady);
        await _engine.Pad.Back();
        return 1800;
    }

    /// <summary>挑战退出/重进：B 到介绍页；重进模式在介绍页按 A 回清单刷新锁态，
    /// 否则继续 B 回活动列表（到达后复位挑战模式、按需拉黑槽位并顺延到下一个活动）。</summary>
    private async Task<int> HandleChallengeRetreatAsync(WkScreen wk, MlScreen ml)
    {
        if (wk == WkScreen.EventsList)
        {
            if (_chRetreatBlacklist) _doneSlots.Add(_slotIndex);
            _slotIndex++;
            _challengeMode = false;
            _chCurrentCard = 0;
            _chReentry = 0;
            _chContradictions = 0;
            _chLocalDone.Clear();
            _chBlocked.Clear();
            _log($"[周活动·挑战] 已退出挑战活动，改刷槽位 {_slotIndex}", LogLevel.Success);
            SetPhase(WkPhase.Flow);
            return 800;
        }
        if (wk == WkScreen.ChallengeList)
        {
            if (++_attempts > 6) { SetPhase(WkPhase.Flow); return 800; }
            await _engine.Pad.Back();
            return 2000;
        }
        if (wk == WkScreen.EventIntro)
        {
            if (_chRetreatReenter)
            {
                _chRetreatReenter = false;
                SetPhase(WkPhase.ChallengeList);
                await _engine.Pad.Confirm(); // 介绍页光标默认在「进入」：重进清单
                return 2600;
            }
            if (++_attempts <= 4)
            {
                await _engine.Pad.Back();
                return 2200;
            }
            SetPhase(WkPhase.Flow);
            return 800;
        }
        if (wk == WkScreen.EntryPopup || ml == MlScreen.HasConfirm)
        {
            // 退出途中的弹窗：先 A 清（可能是通关奖励弹窗），多次不消改 B（退出确认类）
            if (++_attempts <= 5) return await PressConfirmAsync("挑战退出途中弹窗：确认");
            await _engine.Pad.Back();
            return 2000;
        }
        if (++_attempts > 8) SetPhase(WkPhase.Flow);
        return 1500;
    }

    private void SetPhase(WkPhase phase)
    {
        _phase = phase;
        _attempts = 0;
        if (phase == WkPhase.ChallengeList)
        {
            _chScanReady = false; // 进阶段先饱和左移归位再开始扫
            _chScan = 1;
            _chMissing = 0;
        }
    }

    private async Task<int> PressConfirmAsync(string what)
    {
        _log($"[周活动] 手柄 A：{what}", LogLevel.Info);
        await _engine.Pad.Confirm();
        return 2500;
    }
}
