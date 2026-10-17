using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Bot.Scenarios.CnWeekly;

/// <summary>
/// 国服周活动挂机场景（实况足球在线）。菜单侧状态机：主页（推荐页签）→ 饱和归位进比赛页签
/// → 卡行 下→左 选「比赛活动」→ 活动列表（对阵电脑）进活动 → 介绍页「进入」→ 按活动形态分叉：
/// ①杯赛型（冠军赛）：对阵表 A/RT 下一步 → 活动主页：首次进「球队阵容」RS 自动配置选「活动加成」
///   → B 完成 → RT 教练入场开赛；赛后回活动主页循环，打满冠军赛全程（小组赛×3+淘汰赛×4=7 场）。
/// ②巡回赛型（球星训练，2026-10-17 实机）：活动主页（前往比赛 + X 活动设置）——首次 X 打开
///   活动设置只读核验 教练模式=开 + 教练模式内控制=完全托管（不盲改：面板里 A 可能误开换队；
///   OCR 漏读则提示后照常开赛）→ B 完成 → A 前往比赛；赛后回主页循环。
/// 比赛中流程与我的联赛完全共用：InMatch 随机点击、终场、结算弹窗、满级检测、场次计数。
/// 达到「完成场次停止」设定或主要奖励打完后由用户手动停止（完成态探针待校准）。
/// 判定只用固定 UI 词（对阵电脑/教练入场/前往比赛/活动设置/球队阵容列表/活动加成…），
/// 活动名每周轮换禁作判定点。
/// </summary>
public sealed class CnWeeklyScenario : IScenario
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
    }

    private readonly BotEngine _engine;
    private readonly Action<string, LogLevel> _log;
    private readonly Random _rnd = new();

    private CwPhase _phase = CwPhase.Flow;
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

    private string _lastScreenName = "";
    private int _streak;
    private readonly MlFlowMemory _memory = new();
    private readonly MatchSessionTracker _matchSession = new();
    private DateTime _lastTapLogAt = DateTime.MinValue;
    private DateTime _lastMaxLevelAt = DateTime.MinValue;
    private bool _takeoverLogged;
    /// <summary>TitleScreen 首次出现时间（比赛上下文中按 X 前 30s 宽限，区分加载画面与真标题）。</summary>
    private DateTime? _titleSince;

    public string Name => "国服周活动";

    public CnWeeklyScenario(BotEngine engine, Action<string, LogLevel> log)
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

        // 国服周活动界面先判：命中时 ml 记 Unknown（由状态机处理，不走共享决策），
        // 未命中交 MyLeagueBrain（比赛中/终场/共用弹窗/标题画面等）
        var cw = CnWeeklyBrain.Classify(obs.Detections, obs.Ocr);
        var ml = cw == CwScreen.None
            ? MyLeagueBrain.Classify(obs.Detections, obs.Ocr, corner, matchContext, light)
            : MlScreen.Unknown;
        _memory.Record(ml, now);

        if (!_takeoverLogged)
        {
            _takeoverLogged = true;
            _log($"[国服周活动] 智能接管：当前界面「{(cw != CwScreen.None ? cw.ToString() : ml.ToString())}」，从该阶段继续", LogLevel.Success);
        }

        // ---- 共用比赛流程：场次计数。国服周活动不回我的联赛主页（isHome 恒 false），
        //      终场 FullTime 是权威关场信号，超时兜底 5 分钟
        bool wasActive = _matchSession.Active;
        bool matchEnded = _matchSession.Observe(ml, now,
            sawKeyMatchHint: ml == MlScreen.KeyMatchDialog || obs.Ocr.ContainsJoined("关键比赛"),
            isHome: false);
        if (!wasActive && _matchSession.Active)
        {
            _tourPanelReopens = 0; // 比赛开打：游戏会重置主页焦点，乒乓探测复位
            _log("[国服周活动] 比赛开始：挂机等待比赛结束（按节奏随机点击）", LogLevel.Info);
        }
        if (matchEnded)
        {
            _engine.Stats.MatchesDone++;
            if (_matchSession.WasKeyMatch) _engine.Stats.KeyMatches++;
            _log($"[国服周活动] 一场比赛结束（累计 {_engine.Stats.MatchesDone} 场）", LogLevel.Success);

            var cwCfg = _engine.Config.CnWeekly;
            if (cwCfg.StopAfterMatches > 0 && _engine.Stats.MatchesDone >= cwCfg.StopAfterMatches)
            {
                _log($"[国服周活动] 已完成设定场次（{_engine.Stats.MatchesDone}/{cwCfg.StopAfterMatches} 场），自动停止挂机", LogLevel.Success);
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
            _log($"[国服周活动] 球员满级提示（累计 {_engine.Stats.PlayersMaxed} 次）", LogLevel.Success);
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
        // 国服比赛起始页：底栏「设置|球场|RT 下一步」——确认键是 RT 而非 A（国际服为 A）。
        // 共享决策按 A 无法开赛，且画面不变 _streak≥StuckStreak 后转入静默等待，会死在这里
        // （实机 2026-10-14 00:53：起始页无人按键卡死 6 分钟）。先于状态机拦截：RT 开赛。
        if (ml == MlScreen.MatchStartPage)
        {
            if (_streak < 1) return 800; // 过渡帧防抖
            _log("[国服周活动] 起始页：RT 下一步开赛（国服确认键为 RT）", LogLevel.Info);
            await _engine.Pad.RT();
            return 3400;
        }

        int? cnWait = await HandleCnAsync(cw, ml, obs);
        if (cnWait is > 0) return cnWait.Value;
        if (cnWait is not null) return 1500;
        // cw==None 且阶段已交回共享流程：走共用决策（比赛中/终场/弹窗/逃生）

        var action = MyLeagueBrain.Decide(ml, obs.Ocr, _streak, matchContext);

        // 标题画面宽限（同国际服周活动场景）：比赛上下文中的「eFootball™+X」帧多为加载画面。
        // 国服赛后品牌过场帧会连续出现 30s 以上，宽限拉长到 90s
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
                _log("[国服周活动] eFootball 标志帧（疑似比赛加载/赛后过场画面），暂不按 X，继续观察", LogLevel.Info);
                return 2000;
            }
        }

        switch (action.Kind)
        {
            case MlActionKind.PadEnterMatch:
                // 国服主页不走国际服的下→左×6：国服主页是页签行（推荐/比赛/签约/商店），
                // 上×2 进页签行 → 左×4 饱和归位「推荐」→ 右×1 到「比赛」
                _log("[国服周活动] 主页导航：上×2 → 左×4 归位推荐页签 → 右进比赛页签", LogLevel.Info);
                await _engine.Pad.DpadRepeat(PadDir.Up, 2);
                await _engine.Pad.DpadRepeat(PadDir.Left, 4);
                await _engine.Pad.Dpad(PadDir.Right);
                break;

            case MlActionKind.PadConfirm:
                _log($"[国服周活动] 手柄 A：{action.Target}", LogLevel.Info);
                await _engine.Pad.Confirm();
                break;

            case MlActionKind.PadUpConfirm:
                _log($"[国服周活动] 弹窗上移确认：{action.Target}", LogLevel.Info);
                await _engine.Pad.Dpad(PadDir.Up);
                await _engine.Pad.Confirm();
                break;

            case MlActionKind.PadXPress:
                _log($"[国服周活动] {action.Target}", LogLevel.Info);
                await _engine.Pad.X();
                break;

            case MlActionKind.GamepadBack:
                _log($"[国服周活动] {action.Target}", LogLevel.Warn);
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
                    _log($"[国服周活动] 比赛挂机中：随机点击 A（{waitMs / 1000.0:F1}s 间隔）", LogLevel.Debug);
                }
                return waitMs;
            }

            case MlActionKind.PadMoveRight:
                await _engine.Pad.Dpad(PadDir.Right);
                break;
        }
        return action.WaitMs;
    }

    // ---------------- 国服菜单侧状态机 ----------------

    /// <summary>返回正数/0=本轮已处理（下一轮延时）；null=未接管（走共享决策）。</summary>
    private async Task<int?> HandleCnAsync(CwScreen cw, MlScreen ml, Observation obs)
    {
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
                        // → 右×1 到「比赛」页签 → 下进卡行（默认落天梯巅峰赛）→ 左到「比赛活动」→ A
                        _log("[国服周活动] 主页：上×2 左×4 归位推荐 → 右进比赛页 → 下 → 左 → A 进比赛活动", LogLevel.Info);
                        await _engine.Pad.DpadRepeat(PadDir.Up, 2);
                        await _engine.Pad.DpadRepeat(PadDir.Left, 4);
                        await _engine.Pad.Dpad(PadDir.Right);
                        await _engine.Pad.Dpad(PadDir.Down);
                        await _engine.Pad.Dpad(PadDir.Left);
                        await _engine.Pad.Confirm();
                        return 3200;

                    case CwScreen.EventsList:
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
                        // 「RT 下一步」页按 A 即可（用户 2026-10-14 实机确认）；A 连续两次未生效
                        // 换 RT 一次（防焦点在页签上 A 被页签吃掉），仍不行再回 A
                        if (_attempts == 3)
                        {
                            _log("[国服周活动] 对阵表：A 两次未生效，改按 RT 下一步", LogLevel.Info);
                            await _engine.Pad.RT();
                        }
                        else
                        {
                            _log("[国服周活动] 对阵表：A 下一步进活动主页", LogLevel.Info);
                            await _engine.Pad.Confirm();
                        }
                        return 3200;

                    case CwScreen.EventHome:
                        if (_streak < 1) return 800; // 过渡帧防抖
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

                    case CwScreen.EventSettings:
                        if (_streak < 1) return 800; // 面板打开过渡帧
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

                // 阵容保存确认弹窗（B 完成后可能弹「是否保存」类确认）：有推进词就按 A
                if (ml == MlScreen.HasConfirm ||
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
                    _log("[国服周活动] 阵容弹窗：A 确认", LogLevel.Info);
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
                    // 赛后可能回到对阵表：下一步回活动主页再开赛（「RT 下一步」页按 A 即可）
                    if (_streak < 2) return 900;
                    _log("[国服周活动] 对阵表：A 下一步", LogLevel.Info);
                    await _engine.Pad.Confirm();
                    return 3200;
                }
                if (cw == CwScreen.Squad || cw == CwScreen.SquadMenu)
                {
                    if (++_attempts > 3) { SetPhase(CwPhase.Flow); return 600; }
                    await _engine.Pad.Back();
                    return 2200;
                }
                if (cw is CwScreen.EventsList or CwScreen.EventIntro or
                    CwScreen.HomeRecommended or CwScreen.HomeMatch or
                    CwScreen.EventHomeTour or CwScreen.EventSettings)
                {
                    SetPhase(CwPhase.Flow); // 被弹回导航链/巡回赛界面：回屏幕驱动重走
                    return 700;
                }
                // 离开活动主页：交给共享比赛流程（起始页/弹窗/比赛内）
                SetPhase(CwPhase.Flow);
                return null;
            }

            default:
                return null;
        }
    }

    /// <summary>活动列表页：A 进入当前焦点活动卡（国服当前仅 1 张冠军赛卡，光标默认落卡上）。
    /// A 无响应时下移一次再试（焦点可能停在页签行），多次失败 B 退回比赛页重导航。</summary>
    private async Task<int> EnterEventAsync()
    {
        if (_streak < 1) return 800; // 过渡帧防抖
        if (++_attempts > 4)
        {
            _log("[国服周活动] 活动列表多次未进入活动，B 退回比赛页重导航", LogLevel.Warn);
            SetPhase(CwPhase.Flow);
            await _engine.Pad.Back();
            return 2400;
        }
        if (_attempts >= 3)
        {
            _log("[国服周活动] 活动列表：下移进卡行后再按 A", LogLevel.Info);
            await _engine.Pad.Dpad(PadDir.Down);
        }
        _log("[国服周活动] 活动列表：A 进入活动", LogLevel.Info);
        await _engine.Pad.Confirm();
        return 2800;
    }

    private void SetPhase(CwPhase phase)
    {
        _phase = phase;
        _attempts = 0;
    }
}
