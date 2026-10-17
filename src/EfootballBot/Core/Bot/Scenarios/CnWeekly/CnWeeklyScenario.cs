using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Bot.Scenarios.Weekly;
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
///   OCR 漏读则提示后照常开赛）→ B 完成 → A 前往比赛；赛后回主页循环。主页右栏主要奖励
///   带勾时右移进 i 信息弹窗读 x/x：全满=拿满，B 退上上页回活动列表换下一个活动（无卡自停）；
///   没满=回前往比赛继续刷（勾≠拿满，以弹窗 x/x 为准）。
/// ③挑战型（挑战清单，2026-10-17 实机）：介绍页进入后弹「挑战清单」选卡页——游戏默认
///   光标已落在待挑战卡上（上锁的上一张/最后一个对勾的下一张），评估后直接 A 进入；
///   通关（卡详情页胜场读满）后 B 回清单换下一张；无可挑战的卡（全勾/全锁）重进清单
///   刷新，重进仍无 = 全部完成自动停止。
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
        /// <summary>主要奖励已获得：B 清介绍页/弹窗回活动列表，槽位 +1 换下一个活动。</summary>
        Retreating,
        /// <summary>挑战型「挑战清单」：归位第一张后逐卡右扫，找第一张（无锁且无勾且非
        /// 本运行内已通关/已拉黑）的卡按 A 进入；扫到尾全勾 = 全部通关退出停止。</summary>
        ChallengeList,
        /// <summary>挑战全部完成/锁态异常放弃：B 退介绍页退活动列表（或 B 后 A 重进清单刷新锁态）。</summary>
        ChallengeRetreat,
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
    /// <summary>当前挂机的活动槽位（0 起，活动列表卡行从左数；主要奖励刷满退列表后 +1）。</summary>
    private int _slotIndex;

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

            // 主要奖励进度只在比赛后变化：解除「未拿满」门控，下次回主页再核验一次
            _rewardContinueUntilMatch = false;

            if (_challengeMode && _chCurrentCard > 0)
            {
                _chCurPlays++;
                _log($"[国服周活动·挑战] 卡 {_chCurrentCard:00} 第 {_chCurPlays} 局结束", LogLevel.Info);
            }

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
                        if (_challengeMode || WeeklyBrain.IsChallengeCardHome(obs.Ocr))
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
                        if (_challengeMode || WeeklyBrain.IsChallengeCardHome(obs.Ocr))
                        {
                            // 挑战卡详情页（巡回赛主页样式）：已通关 → B 回清单；未通关落入下方
                            // 巡回赛既有流程（X 活动设置核验 → A 前往比赛）
                            int? done = await ChallengeCardDoneStepAsync(obs);
                            if (done is not null) return done.Value;
                        }
                        {
                            // 主要奖励拿满核验（用户 2026-10-17 流程）：主页勾 → i 弹窗读 x/x
                            int? reward = await TourRewardCheckStepAsync(obs);
                            if (reward is not null) return reward.Value;
                        }
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

                    case CwScreen.ChallengeList:
                        // 挑战型活动：介绍页按进入后弹的是挑战清单（半途启动直接落在清单同理）
                        return EnterChallengeListPhase();

                    case CwScreen.Difficulty:
                        return await HandleCnDifficultyAsync(obs);

                    case CwScreen.RewardPopup:
                        return await HandleRewardPopupAsync(obs);

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
                    CwScreen.EventHomeTour or CwScreen.EventSettings or
                    CwScreen.ChallengeList or CwScreen.Difficulty or CwScreen.RewardPopup)
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
                return await HandleChallengeRetreatAsync(cw, ml);

            case CwPhase.Retreating:
                return await HandleRetreatingAsync(cw, ml);

            default:
                return null;
        }
    }

    /// <summary>活动列表页（用户 2026-10-17 实机）：列表打开时光标停在「对阵电脑」页签行上，
    /// 先 下 进卡行，再饱和左移×6 归位最左第 1 张活动卡（卡数不固定，到头多按无害），
    /// 主要奖励刷满退场后按 _slotIndex 右移顺延到下一张 → A 进入。多次未进入：槽位 &gt;0 =
    /// 后面已没有活动卡（都刷完了）→ 报成功停止；槽位 0 = B 退回比赛页重导航。</summary>
    private async Task<int> EnterEventAsync()
    {
        if (_streak < 1) return 800; // 过渡帧防抖
        if (++_attempts > 4)
        {
            if (_slotIndex > 0)
            {
                _log($"[国服周活动] 槽位 {_slotIndex} 多次未进入活动卡：可见活动已全部刷完，停止挂机", LogLevel.Success);
                _engine.Stop();
                return 0;
            }
            _log("[国服周活动] 活动列表多次未进入活动，B 退回比赛页重导航", LogLevel.Warn);
            SetPhase(CwPhase.Flow);
            await _engine.Pad.Back();
            return 2400;
        }
        _log(_slotIndex > 0
            ? $"[国服周活动] 活动列表：下进卡行 → 左×6 归位 → 右×{_slotIndex} 到槽位 {_slotIndex} → A 进活动"
            : "[国服周活动] 活动列表：下进卡行 → 左×6 归位第一张 → A 进活动", LogLevel.Info);
        await _engine.Pad.Dpad(PadDir.Down);
        await _engine.Pad.DpadRepeat(PadDir.Left, 6);
        if (_slotIndex > 0) await _engine.Pad.DpadRepeat(PadDir.Right, _slotIndex);
        await _engine.Pad.Confirm();
        return 2800;
    }

    /// <summary>「选择比赛难度」页（2026-10-17 实机 cn_tour_difficulty，前往比赛后弹出）：
    /// 与我的联赛难度核对同一守则——读当前选中（白底高亮卡）与配置比对：相同/读不到/
    /// 目标不在卡行 = 直接确认，绝不盲导航；不同 = 按规范难度序左右移差值后确认。
    /// 底栏写 RT 确定，A 通常同样生效（对阵表「RT 下一步」同款实测）：A 两次未生效换 RT。</summary>
    private async Task<int> HandleCnDifficultyAsync(Observation obs)
    {
        if (_streak < 1) return 800; // 页面淡入防抖
        if (++_attempts > 4)
        {
            _log("[国服周活动] 难度页确认多次未生效，B 返回重走流程", LogLevel.Warn);
            SetPhase(CwPhase.Flow);
            await _engine.Pad.Back();
            return 2400;
        }

        string wanted = _engine.Config.CnWeekly.Difficulty;
        string? selected = CnWeeklyBrain.DetectSelectedCnDifficulty(obs.Frame, obs.Ocr);

        if (selected is not null && selected != wanted)
        {
            int from = Array.IndexOf(MyLeagueBrain.Difficulties, selected);
            int to = Array.IndexOf(MyLeagueBrain.Difficulties, wanted);
            if (from >= 0 && to >= 0)
            {
                int steps = to - from;
                _log($"[国服周活动] 难度核对：当前 {selected} → 目标 {wanted}，{(steps > 0 ? "右" : "左")}×{Math.Abs(steps)} 后确认", LogLevel.Info);
                await _engine.Pad.DpadRepeat(steps > 0 ? PadDir.Right : PadDir.Left, Math.Abs(steps));
                await ConfirmDifficultyAsync();
                return 3000;
            }
        }

        string why = selected is null ? "未读出当前难度" : selected == wanted ? $"已是 {wanted}" : "目标难度不在卡行";
        _log($"[国服周活动] 难度核对：{why}，直接确认", LogLevel.Info);
        await ConfirmDifficultyAsync();
        return 3000;
    }

    /// <summary>难度页确认键：默认 A，连续两次未生效换 RT 一次（底栏标 RT 确定），仍不行回 A。</summary>
    private async Task ConfirmDifficultyAsync()
    {
        if (_attempts == 3)
        {
            _log("[国服周活动] 难度页：A 两次未生效，改按 RT 确定", LogLevel.Info);
            await _engine.Pad.RT();
        }
        else
        {
            await _engine.Pad.Confirm();
        }
    }

    // ---------------- 巡回赛主要奖励核验（主页勾 → i 信息弹窗 x/x） ----------------

    /// <summary>
    /// 巡回赛主页主要奖励核验步（用户 2026-10-17 流程，仅国服）：主页右栏勾 = 已有主要
    /// 奖励被领过，不代表拿满（主页 高光定向券×1 带勾时 i 弹窗里仍是 1/2）。右移进 i →
    /// A 打开信息弹窗 → RewardPopup 读 x/x：全满 → 关闭后从主页退上上页回活动列表换
    /// 下一个活动；没满 → 关闭+左移回前往比赛继续刷。弹窗打不开超 2 次：放弃核验按
    /// 未拿满继续刷（保守不提前退场）。返回 null = 本轮不在核验流程，调用方继续常规逻辑。
    /// </summary>
    private async Task<int?> TourRewardCheckStepAsync(Observation obs)
    {
        if (_rewardCheckStage == 3)
        {
            // 弹窗已确认全满：从主页 B 退上上页（主页→介绍页→活动列表，见 Retreating）
            _rewardCheckStage = 0;
            SetPhase(CwPhase.Retreating);
            _log("[国服周活动] 主要奖励已拿满（i 弹窗 x/x 全满），退回活动列表换下一个活动", LogLevel.Success);
            await _engine.Pad.Back();
            return 2200;
        }
        if (_rewardContinueUntilMatch || _rewardPopupGaveUp ||
            !CnWeeklyBrain.MainRewardHasCheck(obs.Frame))
            return null;

        if (_rewardCheckStage == 0)
        {
            _rewardCheckStage = 1;
            _attempts = 0;
            _log("[国服周活动] 主页主要奖励带勾：右移进 i → A 打开信息弹窗核对 x/x", LogLevel.Info);
            await _engine.Pad.Dpad(PadDir.Right); // 前往比赛 → i
            return 1500;
        }
        if (_rewardCheckStage == 1)
        {
            _rewardCheckStage = 2;
            _log("[国服周活动] A 打开信息弹窗", LogLevel.Info);
            await _engine.Pad.Confirm();
            return 2200;
        }
        // stage 2：A 已按过，弹窗仍未出现（光标不在 i 等），重试
        if (++_attempts > 2)
        {
            _rewardPopupGaveUp = true;
            _rewardCheckStage = 0;
            _attempts = 0;
            _log("[国服周活动] 信息弹窗多次未打开，放弃 x/x 核验，按未拿满继续刷", LogLevel.Warn);
            await _engine.Pad.Dpad(PadDir.Left); // i → 前往比赛，恢复常规流程
            return 1200;
        }
        _log("[国服周活动] 信息弹窗未出现，重按 A 打开", LogLevel.Info);
        await _engine.Pad.Confirm();
        return 2200;
    }

    /// <summary>i 信息弹窗（主要奖励 x/x 进度）：全满 → B 关闭，stage=3 由主页核验步退场；
    /// 没满/读不出 → B 关闭 + 左移回前往比赛继续刷（读不出按未拿满，保守不提前退场）。
    /// 进度词只取 主要奖励 头与 活动时间 头之间（活动日期 2026/10/16 三段式不吃）。</summary>
    private async Task<int> HandleRewardPopupAsync(Observation obs)
    {
        if (_streak < 1) return 800; // 弹窗淡入防抖
        var (readable, allDone) = CnWeeklyBrain.ParseMainRewardProgress(obs.Ocr);
        if (!readable && ++_attempts <= 2) return 1200; // 进度词未读全，再认一拍

        _rewardCheckStage = 0;
        _attempts = 0;
        await _engine.Pad.Back(); // B 关闭弹窗（光标回 i）
        if (allDone)
        {
            _rewardCheckStage = 3;
            _log("[国服周活动] 主要奖励全部拿满（x/x 全等），关闭弹窗后退回活动列表", LogLevel.Success);
            return 2400;
        }
        // 未拿满：本场打完前不再重复核验（勾会一直挂在主页，否则原地循环）
        _rewardContinueUntilMatch = true;
        _log($"[国服周活动] 主要奖励未拿满（{(readable ? "进度未全满" : "进度未读出")}），左移回前往比赛继续刷；本场结束后再复核", LogLevel.Info);
        await _engine.Pad.Dpad(PadDir.Left); // i → 前往比赛
        return 2400;
    }

    /// <summary>主要奖励已拿满后的退场：B 清介绍页/设置面板/弹窗回活动列表（上上页），
    /// 槽位 +1 换下一个活动；途中确认弹窗（奖励领取类）按 A 清掉。</summary>
    private async Task<int?> HandleRetreatingAsync(CwScreen cw, MlScreen ml)
    {
        if (cw == CwScreen.EventsList)
        {
            _slotIndex++;
            SetPhase(CwPhase.Flow);
            _log($"[国服周活动] 已退回活动列表，改刷槽位 {_slotIndex}", LogLevel.Success);
            return 800;
        }
        if (cw is CwScreen.EventHomeTour or CwScreen.EventHome or
            CwScreen.EventIntro or CwScreen.EventSettings or CwScreen.RewardPopup)
        {
            if (++_attempts > 5) { SetPhase(CwPhase.Flow); return 700; }
            await _engine.Pad.Back();
            return 2200;
        }
        if (ml == MlScreen.HasConfirm)
        {
            if (++_attempts > 6) { SetPhase(CwPhase.Flow); return 700; }
            _log("[国服周活动] 退场途中弹窗：A 确认（可能是奖励领取）", LogLevel.Info);
            await _engine.Pad.Confirm();
            return 2200;
        }
        if (++_attempts > 8) SetPhase(CwPhase.Flow);
        return 1500;
    }

    // ---------------- 挑战型活动子流程（2026-10-17 实机） ----------------

    /// <summary>进入挑战子流程：激活挑战模式并切到清单阶段（游戏默认光标已在待挑战卡上）。</summary>
    private int EnterChallengeListPhase()
    {
        if (!_challengeMode)
        {
            _challengeMode = true;
            _log("[国服周活动] 检测到挑战清单：进入挑战子流程（默认光标即待挑战卡，评估后直接进入）", LogLevel.Success);
        }
        SetPhase(CwPhase.ChallengeList);
        return 1200;
    }

    /// <summary>
    /// 挑战卡详情页通关判定步（杯赛/巡回赛主页样式通用）：右栏胜场读满（"1/1" 经 Norm 成
    /// "11"，进行中 "0/1"→"01" 不命中）或通关探针漏读但本卡已打满 2 局 → 记忆已通关并
    /// B 回清单换下一张。同一卡再次判通关 = 光标不在待挑战卡上（勾探针漏读的已通关卡），
    /// 走重进清单复位光标。未通关返回 null，调用方继续正常开赛流程。
    /// </summary>
    private async Task<int?> ChallengeCardDoneStepAsync(Observation obs)
    {
        _challengeMode = true; // 半途启动直接落在卡详情页也激活挑战模式
        bool progressDone = WeeklyBrain.ChallengeProgressDone(obs.Ocr);
        if (!progressDone && _chCurPlays < 2) return null;

        bool repeat = _chCurrentCard > 0 && _chLocalDone.Contains(_chCurrentCard);
        if (_chCurrentCard > 0) _chLocalDone.Add(_chCurrentCard);
        _chCurPlays = 0;
        if (repeat)
            return await RefreshChallengeListAsync("已通关的卡再次进入（光标不在待挑战卡上）");

        SetPhase(CwPhase.ChallengeList);
        _log($"[国服周活动·挑战] 卡 {_chCurrentCard:00} 已通关（{(progressDone ? "右栏胜场读满" : "已打满 2 局按通关处理")}），B 返回清单换下一张",
            LogLevel.Success);
        await _engine.Pad.Back();
        return 2200;
    }

    /// <summary>
    /// 挑战清单（用户 2026-10-17 实机规则）：清单打开时光标已由游戏落在待挑战卡上——
    /// 「上锁的上一张 / 最后一个对勾的下一张」，不做任何归位或右移，评估后直接 A 进入。
    /// 扫描只做两件事：①确认还有可挑战的卡（无锁无勾、非本运行内已通关/已拉黑）——
    /// 一张都没有则 B 重进清单刷新（通关后解锁有延迟；重进仍无 = 全部完成，停止挂机）；
    /// ②A 连续未生效（光标与扫描不符等异常）同样重进刷新光标，超限停止。
    /// </summary>
    private async Task<int?> HandleChallengeListAsync(CwScreen cw, MlScreen ml, Observation obs)
    {
        // 按进卡详情页成功：交回 Flow（巡回赛/杯赛样式开赛流程 + 通关拦截都在那边）
        if (cw is CwScreen.EventHomeTour or CwScreen.EventHome)
        {
            _chStaleA = 0;
            _chRefreshs = 0;
            SetPhase(CwPhase.Flow);
            return 600;
        }
        if (cw == CwScreen.EventSettings) { SetPhase(CwPhase.Flow); return 700; }
        if (cw == CwScreen.EventsList) { SetPhase(CwPhase.Flow); return 800; }
        if (cw == CwScreen.EventIntro)
        {
            // 清单被关掉了（异常按键/全通关自动退场）：A 重进清单
            if (++_attempts > 6) { SetPhase(CwPhase.Flow); return 600; }
            _log("[国服周活动·挑战] 清单已关闭，A 重进", LogLevel.Info);
            await _engine.Pad.Confirm();
            return 2600;
        }
        // 比赛上下文（A 直落比赛/结算弹窗链）：交回共享流程，清完自会回清单/卡页
        if (ml is MlScreen.InMatch or MlScreen.MatchStartPage or MlScreen.FullTime or MlScreen.TitleScreen)
        {
            SetPhase(CwPhase.Flow);
            return null;
        }
        if (ml == MlScreen.HasConfirm)
        {
            if (++_attempts > 6) { SetPhase(CwPhase.Flow); return 600; }
            _log("[国服周活动·挑战] 清单弹窗：A 确认", LogLevel.Info);
            await _engine.Pad.Confirm();
            return 2200;
        }
        if (cw != CwScreen.ChallengeList)
        {
            if (++_attempts > 10) { SetPhase(CwPhase.Flow); return 600; } // 过场/未知帧超预算：回屏幕驱动自愈
            return 1200;
        }

        if (_streak < 1) return 800; // 开场动画/淡入防抖（卡号 OCR 空帧同此等待）

        var playable = CnWeeklyBrain.ScanChallengeCards(obs.Ocr, obs.Frame)
            .Where(c => !c.Locked && !c.Checked &&
                        !_chLocalDone.Contains(c.Index) && !_chBlocked.Contains(c.Index))
            .ToList();
        if (playable.Count == 0)
            return await RefreshChallengeListAsync("清单上没有可挑战的卡（全勾/全锁）");

        if (_chStaleA >= 3)
            return await RefreshChallengeListAsync("连续按 A 未进入挑战卡");

        _chStaleA++;
        _chCurrentCard = playable[0].Index; // 默认光标即第一张可挑战卡（用户实机）
        _chCurPlays = 0;
        _log($"[国服周活动·挑战] 进入卡 {_chCurrentCard:00}（默认光标即待挑战卡）", LogLevel.Info);
        await _engine.Pad.Confirm();
        return 2600;
    }

    /// <summary>B 退介绍页重进清单：游戏会把光标复位到待挑战卡并刷新锁态（通关后解锁有延迟）。
    /// 超过 3 次仍无进展则停止挂机——最后原因是无可挑战卡 = 全部完成（报成功），否则报错误。</summary>
    private async Task<int> RefreshChallengeListAsync(string reason)
    {
        bool noPlayable = reason.Contains("没有可挑战");
        _chRefreshs++;
        _chStaleA = 0;
        if (_chRefreshs > 3)
        {
            ResetChallengeState();
            if (noPlayable)
            {
                _log("[国服周活动] 重进清单仍无可挑战的卡：全部挑战已完成，停止挂机", LogLevel.Success);
            }
            else
            {
                _log("[国服周活动] 挑战清单多次刷新仍无法进入挑战卡，停止挂机（请手动检查活动状态）", LogLevel.Error);
            }
            _engine.Stop();
            return 0;
        }
        _chRetreatReenter = true;
        SetPhase(CwPhase.ChallengeRetreat);
        _log($"[国服周活动·挑战] {reason}，B 重进清单刷新（第 {_chRefreshs} 次）", LogLevel.Info);
        await _engine.Pad.Back();
        return 2200;
    }

    /// <summary>挑战退出/重进：B 到介绍页；重进模式在介绍页按 A 回清单（光标复位+锁态刷新）；
    /// 退到活动列表 = 非预期路径（刷新流程到不了这里），停止挂机并报错误。</summary>
    private async Task<int?> HandleChallengeRetreatAsync(CwScreen cw, MlScreen ml)
    {
        if (cw == CwScreen.ChallengeList)
        {
            if (++_attempts > 6) { SetPhase(CwPhase.Flow); return 800; }
            await _engine.Pad.Back();
            return 2000;
        }
        if (cw == CwScreen.EventIntro)
        {
            if (_chRetreatReenter)
            {
                _chRetreatReenter = false;
                SetPhase(CwPhase.ChallengeList);
                _log("[国服周活动·挑战] 重进清单（光标复位到待挑战卡）", LogLevel.Info);
                await _engine.Pad.Confirm(); // 介绍页光标默认在「进入」
                return 2600;
            }
            if (++_attempts <= 4)
            {
                await _engine.Pad.Back();
                return 2200;
            }
            SetPhase(CwPhase.Flow);
            return 800;
        }
        if (cw == CwScreen.EventsList)
        {
            ResetChallengeState();
            _log("[国服周活动] 已退出挑战活动，停止挂机（若非预期请回报日志）", LogLevel.Error);
            _engine.Stop();
            return 0;
        }
        if (ml == MlScreen.HasConfirm)
        {
            // 退出途中的确认弹窗：先 A 清（可能是通关奖励弹窗），多次不消改 B（退出确认类）
            if (++_attempts <= 5)
            {
                _log("[国服周活动·挑战] 退出途中弹窗：A 确认", LogLevel.Info);
                await _engine.Pad.Confirm();
                return 2000;
            }
            await _engine.Pad.Back();
            return 2000;
        }
        if (++_attempts > 8) SetPhase(CwPhase.Flow);
        return 1500;
    }

    /// <summary>挑战子流程状态复位（停止挂机收尾时调用）。</summary>
    private void ResetChallengeState()
    {
        _challengeMode = false;
        _chLocalDone.Clear();
        _chBlocked.Clear();
        _chCurrentCard = 0;
        _chCurPlays = 0;
        _chStaleA = 0;
        _chRefreshs = 0;
        _chRetreatReenter = false;
    }

    private void SetPhase(CwPhase phase)
    {
        _phase = phase;
        _attempts = 0;
    }
}
