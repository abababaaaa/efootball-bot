using EfootballBot.Core.Config;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Bot.Scenarios.MyLeague;

/// <summary>
/// 我的联赛挂机场景（大重置第一个框架）：感知（YOLO 6 类 + OCR）→ MyLeagueBrain 决策
/// → 全程模拟手柄操作（Dpad 导航 / A 确认 / B 逃生）。
/// 骨架流程：主菜单"比赛"卡片 → 比赛页进"我的联赛" → 签领奖励一路确认推进 → 比赛画面等待。
/// 国际服游戏不响应 PostMessage 后台鼠标，因此不使用鼠标，全部手柄输入。
/// 国服我的联赛（CnMyLeagueScenario）继承本类：联赛内流程完全一致（难度核对/道具购买/
/// 比赛挂机/终场弹窗链），仅进入导航与确认键通过三个虚钩子（分类/拦截/配置）按国服适配。
/// </summary>
public class MyLeagueScenario : IScenario
{
    protected readonly BotEngine _engine;
    protected readonly Action<string, LogLevel> _log;

    private MlScreen _lastScreen = MlScreen.Unknown;
    private int _streak;

    /// <summary>流程帧记忆：给决策提供"最近见过什么界面"的时间上下文。</summary>
    private readonly MlFlowMemory _memory = new();

    /// <summary>比赛场次跟踪：开赛/终判，驱动 MatchesDone 统计。</summary>
    private readonly MatchSessionTracker _matchSession = new();

    /// <summary>比赛中挂机点击的随机数（挂机节奏区间内取值）。</summary>
    private readonly Random _rnd = new();
    /// <summary>比赛中点击日志节流：每分钟至多一条，防刷屏/日志膨胀。</summary>
    private DateTime _lastTapLogAt = DateTime.MinValue;

    /// <summary>待购道具（按设置生成；每次挂机运行满足一次即不再购买）。</summary>
    private List<MyLeagueBrain.MlItemTarget> _configuredItems = new();
    private bool _itemsInitialized;
    /// <summary>本次运行已确认满足（主页面板核验通过或购买完成后重验通过）。</summary>
    private bool _purchaseSatisfied;
    /// <summary>本轮运行已购买的目标（物品页商店行无法判"已拥有"，用已购集合防重复买）。</summary>
    private readonly HashSet<MyLeagueBrain.MlItemTarget> _bought = new();
    /// <summary>物品页当前光标所在行（1 起；0=未知需锚定）。增量下移导航用。</summary>
    private int _itemRow;
    /// <summary>智能接管提示是否已记录（每次运行一次）。</summary>
    private bool _takeoverLogged;
    /// <summary>TitleScreen 首次出现时间（比赛上下文中按 X 前 30s 宽限，区分加载画面与真标题）。</summary>
    private DateTime? _titleSince;
    /// <summary>关键比赛预告是否已提示（离开预告状态后复位）。</summary>
    private bool _keyMatchAnnounced;
    /// <summary>球员满级提示节流。</summary>
    private DateTime _lastMaxLevelAt = DateTime.MinValue;
    /// <summary>经验值页「大部分球员已满级」日志节流（页面停留多帧只报一次）。</summary>
    private DateTime _lastExpPageMaxLogAt = DateTime.MinValue;
    /// <summary>购买后主页重验次数（超过 2 次仍缺则放弃，防死循环）。</summary>
    private int _verifyRetries;
    /// <summary>下一场为关键比赛（预告弹窗/主页预告已见；比赛结束即消费复位）。</summary>
    private bool _nextMatchIsKey;
    /// <summary>主页连续按 A 前往比赛但画面未离开主页的次数（无响应自愈计数）。</summary>
    private int _goMatchAttempts;
    /// <summary>上次按 A 前往比赛的时间（用于只 dump 一次前往后的弹窗画面 OCR）。</summary>
    private DateTime _lastGoMatchPress = DateTime.MinValue;
    /// <summary>本次前往比赛后是否已 dump 过画面 OCR（防刷屏）。</summary>
    private bool _goMatchOcrDumped;

    /// <summary>购买流程阶段。</summary>
    private enum PurchasePhase
    {
        Idle,          // 未介入（未配置/已满足）
        PanelChecking, // 主页右拨面板核验中
        GoingPoints,   // 盲归位导航至兑换积分（屏幕级确认，不判磁贴亮度）
        GoingItems,    // 积分页进入我的联赛物品
        Buying,        // 物品页选购
        Returning,     // 购完返回主页（重新判断）
    }

    private PurchasePhase _phase = PurchasePhase.Idle;
    /// <summary>导航失败重试计数（每个阶段独立，超过后回 Idle 防死循环）。</summary>
    private int _navAttempts;

    public MyLeagueScenario(BotEngine engine, Action<string, LogLevel> log)
    {
        _engine = engine;
        _log = log;
        // 焦点检测亮度明细 → Debug 日志（导航错判时日志直接给出各磁贴实际亮度/阈值/判定）
        MyLeagueBrain.FocusDiag = s => _log($"[我的联赛] {s}", LogLevel.Debug);
    }

    public virtual string Name => "我的联赛";

    /// <summary>难度/道具购买/停止场次等配置来源。国服子类换用 CnMyLeague 配置段，
    /// 与国际服互不影响（同款玩法、两服各自记设置）。</summary>
    protected virtual MyLeagueConfig LeagueConfig => _engine.Config.MyLeague;

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var obs = await _engine.ObserveAsync(ct);
        var now = DateTime.Now;
        // 角区探针兜底 InMatch 需比赛上下文（流程帧记忆：最近真的见过比赛/起始页）：
        // 菜单页右上角也可能有内容（2026-10-11 用户实机：联赛主页奖励徽章点亮角区探针），
        // 无上下文时宁可判 Unknown 等一拍，绝不把菜单当比赛盲按 A。
        // 冷启动直接落在真实比赛中由比分框时钟主判据覆盖，不依赖角区兜底。
        bool matchContext = ScenarioShared.MatchContext(_memory, now);
        var screen = ClassifyScreen(obs, matchContext);

        _memory.Record(screen, now);

        // 主页无响应自愈计数：只在流程真正推进（开赛/比赛中/关键比赛弹窗被识别）或
        // 离开联赛（回主菜单/比赛页）时复位。此前"画面离开主页即复位"会被「弹窗被盲按 A
        // 取消 → 回主页」的循环每圈清零（用户 2026-10-15 实测：传奇级关键比赛弹窗不识别，
        // 前往比赛↔弹窗死循环，自愈计数永远到不了 6）——连到 6 次 B 退出联赛重进自愈。
        if (screen is MlScreen.MatchStartPage or MlScreen.InMatch or MlScreen.KeyMatchDialog
            or MlScreen.MainMenu or MlScreen.MatchHub)
            _goMatchAttempts = 0;

        // 排障诊断：前往比赛后 15s 内出现的确认页/主页画面，把 OCR 摘要写日志（每次前往只
        // dump 一次）。2026-10-15 传奇级关键比赛弹窗反馈排查时，HasConfirm 帧没有任何 OCR
        // 线索（当时 OCR原文 只在零检测帧输出），远程定位全靠猜——此 dump 补上那一环。
        if ((screen is MlScreen.HasConfirm or MlScreen.LeaguePage) &&
            (now - _lastGoMatchPress).TotalSeconds < 15 && !_goMatchOcrDumped)
        {
            _goMatchOcrDumped = true;
            _log($"[我的联赛] 前往比赛后画面（{screen}）OCR：{OcrDigest(obs.Ocr)}", LogLevel.Debug);
        }

        // 智能接管：启动时识别当前流程阶段（分类驱动，任意界面可续接）
        if (!_takeoverLogged)
        {
            _takeoverLogged = true;
            _log($"[我的联赛] 智能接管：当前界面「{DescribeScreen(screen)}」，从该阶段继续", LogLevel.Success);
        }

        // 关键比赛预告弹窗（P2「下一场是关键比赛」/ P3 固定难度）：下一场难度由游戏指定
        if (screen == MlScreen.KeyMatchDialog) _nextMatchIsKey = true;

        // 关键比赛预告：主页出现「关键比赛」字样 → 提示下次前往比赛为关键比赛（每次进入提示一次）
        bool keyMatchHint = screen == MlScreen.LeaguePage && obs.Ocr.ContainsJoined("关键比赛");
        if (keyMatchHint && !_keyMatchAnnounced)
        {
            _keyMatchAnnounced = true;
            _nextMatchIsKey = true;
            _log("[我的联赛] 预告：下一场前往比赛为关键比赛（难度受限/固定，额外积分与经验）", LogLevel.Success);
        }
        else if (!keyMatchHint)
        {
            _keyMatchAnnounced = false;
        }

        // 场次跟踪：开赛记一条日志；回真主页/超时 → 一场结束，计数（关键比赛单列）。
        // isHome 防"回到菜单"误判：开场过渡帧 LeaguePage（无前往比赛）不关场
        bool isLeagueHome = screen == MlScreen.LeaguePage &&
            (obs.Ocr.ContainsJoined("前往比赛") || obs.Ocr.ContainsJoined("下个赛季"));
        bool wasActive = _matchSession.Active;
        bool matchEnded = _matchSession.Observe(screen, now,
            sawKeyMatchHint: screen == MlScreen.KeyMatchDialog || obs.Ocr.ContainsJoined("关键比赛"),
            isHome: isLeagueHome);
        if (!wasActive && _matchSession.Active)
            _log("[我的联赛] 比赛开始：挂机等待比赛结束（不按键）", LogLevel.Info);
        if (matchEnded)
        {
            _engine.Stats.MatchesDone++;
            if (_matchSession.WasKeyMatch)
            {
                _engine.Stats.KeyMatches++;
                _log($"[我的联赛] 一场关键比赛结束（已完成 {_engine.Stats.MatchesDone} 场，关键 {_engine.Stats.KeyMatches} 场）", LogLevel.Success);
            }
            else
                _log($"[我的联赛] 一场比赛结束，已完成 {_engine.Stats.MatchesDone} 场", LogLevel.Success);

            // 每场比赛结束后复位购买状态机（用户要求）：下一轮回联赛主页必定重新右拨
            // 核验「使用的物品」面板，加成道具耗尽（有效场数一般 3 场）当场补购。
            // 重验 2 次仍缺的放弃守卫保留——同一场内最多尝试 3 次，不会死循环。
            _bought.Clear();
            _verifyRetries = 0;
            _purchaseSatisfied = false;
            _phase = PurchasePhase.Idle;
            _navAttempts = 0;
            _goMatchAttempts = 0;
            _nextMatchIsKey = false;
            _log("[我的联赛] 已复位道具核验：下次回主页重新核查并补购耗尽的加成", LogLevel.Info);

            // 场次计数停止（0=无限跑）：总场次 / 关键比赛任一达标即自动停止挂机。
            // 计数为本此挂机累计（引擎每次启动重建）。Stop 会还原停靠窗口、复位手柄、
            // 取消观察循环，本方法返回后主循环随取消令牌干净退出。
            var mlCfg = LeagueConfig;
            if (mlCfg.StopAfterMatches > 0 && _engine.Stats.MatchesDone >= mlCfg.StopAfterMatches)
            {
                _log($"[我的联赛] 已完成设定场次（{_engine.Stats.MatchesDone}/{mlCfg.StopAfterMatches} 场），自动停止挂机", LogLevel.Success);
                _engine.Stop();
                return 0;
            }
            if (mlCfg.StopAfterKeyMatches > 0 && _engine.Stats.KeyMatches >= mlCfg.StopAfterKeyMatches)
            {
                _log($"[我的联赛] 已完成设定关键比赛（{_engine.Stats.KeyMatches}/{mlCfg.StopAfterKeyMatches} 场），自动停止挂机", LogLevel.Success);
                _engine.Stop();
                return 0;
            }
        }

        // 球员满级提示（开关可关；比赛期间"满级"字样，60s 节流计数一次）
        if (LeagueConfig.NotifyPlayerMaxLevel &&
            obs.Ocr.ContainsJoined("满级") && (now - _lastMaxLevelAt).TotalSeconds > 60)
        {
            _lastMaxLevelAt = now;
            _engine.Stats.PlayersMaxed++;
            _log($"[我的联赛] 球员满级提示（累计 {_engine.Stats.PlayersMaxed} 次）——该换人了", LogLevel.Success);
        }

        // 赛后「经验值」页（推进链中的一页，动作不变仍按 A 下一步）：页面只列出本场
        // 还能获得经验的球员，≤3 人 = 大部分球员已满级（实机 2026-10-17 用户需求：
        // 从页面人数一眼看出阵容成长空间）→ 彩色日志提醒换人。60s 节流防页面停留
        // 多帧连报；跟随「球员满级提示」开关。
        int expPagePlayers = MyLeagueBrain.IsExpResultPage(obs.Ocr)
            ? MyLeagueBrain.CountExpResultPlayers(obs.Ocr)
            : 0;
        if (LeagueConfig.NotifyPlayerMaxLevel && expPagePlayers is > 0 and <= 3 &&
            (now - _lastExpPageMaxLogAt).TotalSeconds > 60)
        {
            _lastExpPageMaxLogAt = now;
            _log($"[我的联赛] 经验值页仅 {expPagePlayers} 名球员获得经验——大部分球员已满级，该换人了", LogLevel.Success);
        }

        if (screen == _lastScreen)
        {
            _streak++;
        }
        else
        {
            _streak = 0;
            _lastScreen = screen;
            _log($"[我的联赛] 界面：{DescribeScreen(screen)}", LogLevel.Info);
        }

        // 错误点标记：误入「我的联赛」说明页（光标停在 i 图标时按了 A）
        if (screen == MlScreen.LeagueInfoPage && _streak == 0)
            _log("[我的联赛] 错误点：误入「我的联赛」说明页（光标停在 i 图标时按了 A）——将按 B 返回主页", LogLevel.Error);

        // 比赛上下文：最近 60s 见过比赛特征，或 90s 内见过起始页（即将开赛，开场过场不按 B）
        // → 无特征界面按比赛过场处理（延长 B 逃生耐心）
        bool inMatchRecently =
            _memory.SawWithin(MlScreen.InMatch, TimeSpan.FromSeconds(60), now) ||
            _memory.SawWithin(MlScreen.MatchStartPage, TimeSpan.FromSeconds(90), now);
        // 模块自有界面拦截（国服子类在此接管主页导航与 RT 确认键）：返回非 null = 本轮已处理
        int? customWait = await InterceptAsync(obs, screen, _streak, inMatchRecently);
        if (customWait is not null) return customWait.Value;

        var action = MyLeagueBrain.Decide(screen, obs.Ocr, _streak, inMatchRecently);
        ScenarioShared.DecisionSnapshot(_log, "我的联赛", screen, _streak, action, obs.Ocr);

        // 标题画面宽限（共享实现，含赛后弹窗链 90s 档，见 ScenarioShared.TitleGrace）：
        // 比赛流程上下文中的「eFootball™+X」帧多为开赛加载画面（实机 bot-20261011-140943：
        // 确认难度开赛后的加载帧被判 TitleScreen 盲按 X）；赛后品牌过场会连续 30s+
        // （实机 2026-10-12：全场结束后 33s 处按 X 打断弹窗链）。真实标题画面
        // （游戏重启/掉线回标题）停留超宽限期后照常按 X 重新进入。冷启动直接落在
        // 标题画面（无流程上下文）不等待。
        if (action.Kind != MlActionKind.PadXPress)
        {
            _titleSince = null;
        }
        else if (ScenarioShared.TitleGrace(_memory, now, ref _titleSince, _log, "我的联赛"))
        {
            return 2000;
        }

        // ---- 我的联赛道具购买状态机（只在联赛主页/积分页/物品页介入）----
        int? purchaseWait = await HandlePurchaseAsync(screen, obs.Ocr, obs.Frame);
        if (purchaseWait is > 0) return purchaseWait.Value;

        // ---- 难度选择页（点前往比赛后出现，非关键比赛）----
        if (screen == MlScreen.DifficultyPage)
            return await HandleDifficultyAsync(obs.Frame, obs.Ocr);

        switch (action.Kind)
        {
            case MlActionKind.PadEnterMatch:
                // 主菜单 → 底部卡片行 → 最左"比赛" → A（到头多按无害，v1 盲导航）
                _log("[我的联赛] 主菜单导航：下→左×6→A 进比赛页", LogLevel.Info);
                await _engine.Pad.Dpad(PadDir.Down);
                await _engine.Pad.DpadRepeat(PadDir.Left, 6);
                await _engine.Pad.Confirm();
                break;

            case MlActionKind.PadConfirm when action.Target is "前往比赛" or "下个赛季":
            {
                // 【识别系统重做 2026-10-11 深夜】不再做磁贴亮度焦点校验（实机不可靠：
                // 赛程表恒亮、面板抬亮、机器差异——当天多次卡死均源于此）。一律先盲归位
                // 序列 Left×4 + Up×6 饱和到前往比赛（左列上下链均为用户确认边：前往比赛
                // --下--> 赛程表 --下--> 赛事信息，反向同链；到头多按无害），再按 A。
                // 位置无关、不依赖任何画面判读；从根上杜绝光标停在 i 上误入说明页。
                // 【主页无响应自愈 2026-10-12】按 A 后画面仍停在主页（右侧面板展开时
                // D-pad/A 无效，或输入未到达游戏）：第 3 次先左拨收回面板再归位；
                // 第 6/9/… 次改按 B 退出联赛由比赛页重进（重进后主页光标回默认位）。
                _goMatchAttempts++;
                if (_goMatchAttempts >= 2)
                {
                    // 连续多次从主页前往比赛仍未推进到开赛：大概率流程上有弹窗不被识别
                    // （按 A 被取消又弹回）。画面摘要进日志供远程定位弹窗文案。
                    _log($"[我的联赛] 注意：第 {_goMatchAttempts} 次前往比赛仍未开赛（疑似有弹窗识别受阻），当前画面 OCR：{OcrDigest(obs.Ocr)}", LogLevel.Warn);
                }
                if (_goMatchAttempts == 3)
                {
                    _log("[我的联赛] 主页连续按 A 未离开：先左拨收回右侧面板再试（面板展开时 D-pad/A 无效）", LogLevel.Warn);
                    await _engine.Pad.RsFlick(PadDir.Left);
                }
                if (_goMatchAttempts >= 6 && (_goMatchAttempts - 6) % 3 == 0)
                {
                    _log("[我的联赛] 主页持续无响应：B 退出联赛重进自愈；若反复出现，请检查是否连接了真实手柄（挂机前须断开）", LogLevel.Warn);
                    await _engine.Pad.Back();
                    break;
                }
                _log($"[我的联赛] 盲归位到{action.Target}（左×4+上×6）后开赛", LogLevel.Info);
                await HomeToGoMatchAsync();
                _log($"[我的联赛] 手柄 A：{action.Target}", LogLevel.Info);
                await _engine.Pad.Confirm();
                _lastGoMatchPress = DateTime.Now;
                _goMatchOcrDumped = false;
                break;
            }

            case MlActionKind.PadConfirm:
                _log($"[我的联赛] 手柄 A：{action.Target}", LogLevel.Info);
                await _engine.Pad.Confirm();
                break;

            case MlActionKind.PadMoveRight:
                _log($"[我的联赛] 比赛页右移：{action.Target}", LogLevel.Info);
                await _engine.Pad.Dpad(PadDir.Right);
                break;

            case MlActionKind.GamepadBack:
                _log("[我的联赛] 无特征界面连续多轮，按 B 逃生", LogLevel.Warn);
                await _engine.Pad.Back();
                break;

            case MlActionKind.PadUpConfirm:
                _log($"[我的联赛] 弹窗上移确认：{action.Target}", LogLevel.Info);
                await _engine.Pad.Dpad(PadDir.Up);
                await _engine.Pad.Confirm();
                break;

            case MlActionKind.PadXPress:
                _log($"[我的联赛] {action.Target}", LogLevel.Info);
                await _engine.Pad.X();
                break;

            case MlActionKind.MatchIdleTap:
            {
                // 比赛中挂机点击：按 A，下一轮间隔取「挂机节奏」设置区间的随机值（拟人化）
                await _engine.Pad.Confirm();
                var t = _engine.Config.Timing;
                int waitMs = _rnd.Next(Math.Min(t.IdleTapMinMs, t.IdleTapMaxMs),
                                       Math.Max(t.IdleTapMinMs, t.IdleTapMaxMs) + 1);
                if ((DateTime.Now - _lastTapLogAt).TotalMinutes >= 1)
                {
                    _lastTapLogAt = DateTime.Now;
                    _log($"[我的联赛] 比赛挂机中：按用户节奏随机点击 A（{waitMs / 1000.0:F1}s 间隔）", LogLevel.Debug);
                }
                return waitMs;
            }

            case MlActionKind.Wait:
                break;
        }
        return action.WaitMs;
    }

    // ---------------- 国服子类扩展钩子 ----------------

    /// <summary>
    /// 界面分类：右上角像素探针 + 比赛上下文 + 亮度探针 → MyLeagueBrain。
    /// 国服子类先判国服主页页签/其他模块残留页（命中记 Unknown 由拦截钩子接管），未命中回落本判定。
    /// </summary>
    protected virtual MlScreen ClassifyScreen(Observation obs, bool matchContext) =>
        MyLeagueBrain.Classify(obs.Detections, obs.Ocr, CornerHasContent(obs.Frame),
            matchContext, IsMostlyLightPage(obs.Frame));

    /// <summary>日志里的界面显示名（国服子类把主页 Unknown 显示成国服页签名，便于远程排障）。</summary>
    protected virtual string DescribeScreen(MlScreen screen) => screen.ToString();

    /// <summary>
    /// 模块自有界面拦截：在共享决策（Decide/购买状态机/难度页）之前接管。
    /// 返回非 null = 本轮已处理（值为下一轮延时）；null = 交给共享流程。国际服无自有界面，恒 null。
    /// </summary>
    protected virtual Task<int?> InterceptAsync(Observation obs, MlScreen screen, int streak, bool inMatchRecently)
        => Task.FromResult<int?>(null);

    /// <summary>
    /// 右上角（AI 徽标区，x≥0.80 / y 0.03~0.18）内容探针：≥10% 采样点非黑（任一通道 &gt;40）
    /// 即视为有画面。实机像素网格：比赛帧该区为球场纹理（非黑），菜单帧为纯黑 (0,0,0)。
    /// 每 3 像素步进采样，~2000 次比较，开销可忽略。
    /// </summary>
    internal static bool CornerHasContent(Capture.FrameData f)
    {
        var bgra = f.Bgra;
        int w = f.Width, h = f.Height;
        if (w < 100 || h < 100 || bgra.Length < w * h * 4) return false;
        int x0 = (int)(w * 0.80);
        int y0 = (int)(h * 0.03), y1 = (int)(h * 0.18);
        int samples = 0, lit = 0;
        for (int y = y0; y < y1; y += 3)
        {
            for (int x = x0; x < w; x += 3)
            {
                int i = (y * w + x) * 4;
                samples++;
                if (bgra[i] > 40 || bgra[i + 1] > 40 || bgra[i + 2] > 40) lit++;
            }
        }
        return samples > 0 && lit * 10 >= samples; // ≥10% 非黑
    }

    /// <summary>
    /// 「我的联赛」说明页探针：中央区域（15%~85%）大面积亮色（RGB 均 &gt;180 的采样 ≥60%）。
    /// 我的联赛各页均为深色主题，说明页是整幅白底文档——强特征；比赛画面（球场/夜场）
    /// 达不到该阈值，且有比分框/角区判据先行。实机 bot-20261011-160559 预览：说明页白底
    /// 黑字，OCR 30 词 / btn_back 0.67。
    /// </summary>
    internal static bool IsMostlyLightPage(Capture.FrameData f)
    {
        var bgra = f.Bgra;
        int w = f.Width, h = f.Height;
        if (w < 100 || h < 100 || bgra.Length < w * h * 4) return false;
        int samples = 0, lit = 0;
        for (int y = (int)(h * 0.15); y < h * 0.85; y += 4)
        {
            for (int x = (int)(w * 0.15); x < w * 0.85; x += 4)
            {
                int i = (y * w + x) * 4;
                samples++;
                if (bgra[i] > 180 && bgra[i + 1] > 180 && bgra[i + 2] > 180) lit++;
            }
        }
        return samples > 0 && lit * 10 >= samples * 6; // ≥60% 亮色
    }

    // ---------------- 我的联赛道具购买状态机 ----------------

    /// <summary>
    /// 购买流程编排（实机 flow_1~8，2026-10-10）：
    /// 主页(上层无弹窗) → 右拨右摇杆切「使用的物品」面板 → 核验已有物品；
    /// 满足 → 交给正常流程（前往比赛）；不满足 → 兑换积分 → 我的联赛物品 → 下滑选购
    /// → 购买弹窗上移确认 → 全部购完 B 返回主页重新判断。
    /// 返回正数=本轮已处理（下一轮延时）；null=未介入（走正常决策）。
    /// </summary>
    private async Task<int?> HandlePurchaseAsync(MlScreen screen, OcrResult ocr, Capture.FrameData frame)
    {
        EnsureConfiguredItems();
        if (_configuredItems.Count == 0) return null;
        // 已满足：交正常流程开赛（按 A 前统一盲归位到前往比赛，见 PadConfirm 分支）
        if (_purchaseSatisfied) return null;

        switch (_phase)
        {
            case PurchasePhase.Idle:
            {
                // 智能接管：启动时已在积分页/物品页（上次运行中断）→ 直接续接购买流程
                if (screen == MlScreen.PointsPage)
                {
                    _phase = PurchasePhase.GoingItems;
                    _navAttempts = 0;
                    _log("[我的联赛] 智能接管：已在积分页，进入我的联赛物品", LogLevel.Info);
                    return 800;
                }
                if (screen == MlScreen.ItemList && ocr.ContainsJoined("我的联赛物品"))
                {
                    _phase = PurchasePhase.Buying;
                    _navAttempts = 0;
                    _itemRow = 0; // 未知行，Buying 首轮自动锚定顶部
                    _log("[我的联赛] 智能接管：已在物品页，继续购买", LogLevel.Info);
                    return 800;
                }

                // 联赛主页（有前往比赛=上层无弹窗，弹窗会被分类为其他界面）
                if (screen != MlScreen.LeaguePage || !ocr.ContainsJoined("前往比赛")) return null;
                if (MyLeagueBrain.ShowsItemsPanel(ocr))
                {
                    // 面板已是「使用的物品」：直接核验
                    return await EvaluatePanelAsync(ocr);
                }
                if (MyLeagueBrain.ShowsChallengePanel(ocr))
                {
                    _log("[我的联赛] 主页右拨：查看「使用的物品」面板", LogLevel.Info);
                    await _engine.Pad.RsFlick(PadDir.Right);
                    _phase = PurchasePhase.PanelChecking;
                    return 1800;
                }
                return null; // 面板状态读不出（OCR 缺词）：不介入，避免误操作
            }

            case PurchasePhase.PanelChecking:
            {
                if (screen == MlScreen.LeaguePage && MyLeagueBrain.ShowsItemsPanel(ocr))
                    return await EvaluatePanelAsync(ocr);
                if (screen == MlScreen.LeaguePage && MyLeagueBrain.ShowsChallengePanel(ocr) && ++_navAttempts <= 2)
                {
                    await _engine.Pad.RsFlick(PadDir.Right); // 再拨一次
                    return 1800;
                }
                if (++_navAttempts > 3)
                {
                    // 带上当前主页 OCR 供远程排障：可看出面板实际停在哪个页签/词是否可读
                    _log($"[我的联赛] 面板切换失败，当前主页 OCR：{OcrDigest(ocr)}", LogLevel.Debug);
                    ResetPhase("面板切换失败");
                    return null;
                }
                return 1500;
            }

            case PurchasePhase.GoingPoints:
            {
                // 【识别系统重做 2026-10-11 深夜】盲归位序列 + 屏幕级确认，不再逐步判光标。
                // 路线设计（2026-10-11 深夜二修）：全程只走用户确认的确定边——
                // 归位前往比赛（左×4+上×6）→ 下(赛程表) → 下(赛事信息) → 右(建议)
                // → 上(赛季纪录) → 上(兑换积分) → A。
                // 不走赛程表 --右-->：该边不确定（大概率赛季纪录/小概率兑换积分），任何固定
                // 后续按键都必错一个分支——接 A 误开赛季纪录页（22:40 实机），接 Up 在
                // Right 直接落兑换积分时回卷第一行（2026-10-11 深夜实测：兑换积分按上可能
                // 落 i，而 i 向下是赛程表不是兑换积分，等于死路）。
                // 按 A 后只认屏幕结果（PointsPage），不看磁贴亮度。误入其他页面先 B 退回；
                // 4 次未进则放弃本次核验（下场比赛后重试）。
                if (screen == MlScreen.PointsPage)
                {
                    _phase = PurchasePhase.GoingItems;
                    _navAttempts = 0;
                    _log("[我的联赛] 积分页：进入「我的联赛物品」", LogLevel.Info);
                    await _engine.Pad.Dpad(PadDir.Right); // 两个选项：租借球员(左) | 我的联赛物品(右)
                    await _engine.Pad.Confirm();
                    return 2500;
                }
                if (screen == MlScreen.LeaguePage &&
                    (ocr.ContainsJoined("前往比赛") || ocr.ContainsJoined("下个赛季")))
                {
                    if (++_navAttempts > 4) { ResetPhase("兑换积分导航失败"); return null; }
                    await HomeToGoMatchAsync();            // 前往比赛
                    await _engine.Pad.Dpad(PadDir.Down);   // → 赛程表（确认边）
                    await _engine.Pad.Dpad(PadDir.Down);   // → 赛事信息（确认边）
                    await _engine.Pad.Dpad(PadDir.Right);  // → 建议（确认边）
                    await _engine.Pad.Dpad(PadDir.Up);     // → 赛季纪录（确认边）
                    await _engine.Pad.Dpad(PadDir.Up);     // → 兑换积分（确认边，自下而上逼近）
                    _log($"[我的联赛] 盲归位导航至兑换积分（第 {_navAttempts} 次，下×2→右→上×2）后按 A", LogLevel.Info);
                    await _engine.Pad.Confirm();
                    return 2500;
                }
                // 意外界面（如误开赛季纪录/赛事信息子页）：B 退回主页再试
                if (++_navAttempts > 4) { ResetPhase("兑换积分导航失败（意外界面）"); return null; }
                _log($"[我的联赛] 导航途中进入意外界面，B 退回（第 {_navAttempts} 次）", LogLevel.Warn);
                await _engine.Pad.Back();
                return 2200;
            }

            case PurchasePhase.GoingItems:
            {
                if (screen == MlScreen.ItemList)
                {
                    _phase = PurchasePhase.Buying;
                    _navAttempts = 0;
                    // 进入物品页：锚定列表顶部（到头无害），此后按行序增量下移，不再回顶
                    await _engine.Pad.DpadRepeat(PadDir.Up, 16);
                    _itemRow = 1;
                    return 1200;
                }
                if (screen == MlScreen.PointsPage && ++_navAttempts <= 3)
                {
                    // 没进去：换导航序列重试（右→A / 右×2→A / 直接 A）
                    _log($"[我的联赛] 重试进入我的联赛物品（第 {_navAttempts} 次）", LogLevel.Warn);
                    if (_navAttempts == 1) { await _engine.Pad.Dpad(PadDir.Right); }
                    else if (_navAttempts == 2) { await _engine.Pad.DpadRepeat(PadDir.Right, 2); }
                    await _engine.Pad.Confirm();
                    return 2500;
                }
                if (_navAttempts > 3) { ResetPhase("进入物品页失败"); return null; }
                return 1500;
            }

            case PurchasePhase.Buying:
            {
                if (screen == MlScreen.PurchaseDialog || screen == MlScreen.HasConfirm)
                    return null; // 确认弹窗交给 Brain 决策（PadUpConfirm / PadConfirm）
                if (screen != MlScreen.ItemList) return null;

                // 找下一个未购买的目标（物品页用已购集合 + 使用中预检防重复买）
                var target = _configuredItems.FirstOrDefault(t => !_bought.Contains(t));
                if (target is null)
                {
                    _log("[我的联赛] 购买完毕，返回主页重新核验", LogLevel.Success);
                    _phase = PurchasePhase.Returning;
                    await _engine.Pad.Back();
                    return 2200;
                }

                // 「使用中」预检：目标行已可见且标记使用中（已购、变灰）→ 跳过（不移动，光标行不变）
                var visible = MyLeagueBrain.FindItemRow(ocr, target);
                if (visible?.InUse == true)
                {
                    _log($"[我的联赛] 「{DescribeItem(target)}」已在使用中，跳过购买", LogLevel.Info);
                    _bought.Add(target);
                    return 1500;
                }

                // 固定行序增量导航（用户确认：状态1~3 / 经验4~6 / 主教练13，按行序升序购买）：
                // 从当前行直接下移 (row - _itemRow) 格——如 第3行买完到第6行只下移3格，不回顶
                int row = MyLeagueBrain.ExpectedItemRow(target);
                if (_itemRow < 1)
                {
                    await _engine.Pad.DpadRepeat(PadDir.Up, 16);
                    _itemRow = 1;
                }
                int down = row - _itemRow;
                _log($"[我的联赛] 购买 {DescribeItem(target)}（第 {row} 行，自第 {_itemRow} 行下移 {Math.Max(down, 0)} 格）", LogLevel.Info);
                _bought.Add(target);
                _itemRow = row;
                if (down > 0)
                    await _engine.Pad.DpadRepeat(PadDir.Down, down);
                await _engine.Pad.Confirm();
                return 2500;
            }

            case PurchasePhase.Returning:
            {
                if (screen == MlScreen.LeaguePage && ocr.ContainsJoined("前往比赛"))
                {
                    // 回到主页：重新走面板核验（满足则导航回前往比赛）
                    _phase = PurchasePhase.Idle;
                    _navAttempts = 0;
                    return 800;
                }
                if (screen is MlScreen.ItemList or MlScreen.PointsPage)
                {
                    if (++_navAttempts > 5) { ResetPhase("返回主页失败"); return null; }
                    await _engine.Pad.Back();
                    return 2200;
                }
                return null;
            }

            // NavigatingGo 阶段已随识别系统重做移除：核验满足直接回 Idle，
            // 正常流程按 A 前会统一盲归位到前往比赛（见 PadConfirm 前往比赛分支）。

            default:
                return null;
        }
    }

    // ---------------- 盲归位导航（识别系统重做 2026-10-11 深夜） ----------------

    /// <summary>
    /// 饱和归位到「前往比赛」：Left×4 + Up×6（位置无关，到头多按无害）。
    /// 依据用户确认的左列链：前往比赛 --下--> 赛程表 --下--> 赛事信息（下/左无路），
    /// 反向同链（赛事信息 --上--> 赛程表 --上--> 前往比赛）；左列即最左。
    /// 从任意位置（含 i 图标、右侧面板收回后的任何落点）约 1~2s 归位，
    /// 完全不依赖磁贴亮度判读。
    /// </summary>
    private async Task HomeToGoMatchAsync()
    {
        await _engine.Pad.DpadRepeat(PadDir.Left, 4);
        await _engine.Pad.DpadRepeat(PadDir.Up, 6);
    }

    // ---------------- 难度选择页 ----------------

    /// <summary>
    /// 难度页处理（实机 difficulty_2~4 + 关键比赛受限页 P1）：
    /// 关键比赛（预告弹窗/主页预告/受限横幅任一命中）→ 一律按游戏默认直接 A 确认，不改选；
    /// 其余：列表前 N 行 = 实际检出的难度选项（普通 7 项），底行(第 N+1) = 返回/取消/确认，
    /// 默认光标在确认，蓝勾 = 当前所选难度。一致（或所选难度不在选项中→按游戏默认）→
    /// 直接 A 确认；不一致 → 上×(N+1-k) 到第 k 行 A 勾选 → 下×(N+1-k) 回底行 → 右×1 到确认 → A 开赛。
    /// </summary>
    private async Task<int> HandleDifficultyAsync(Capture.FrameData frame, OcrResult ocr)
    {
        string wanted = LeagueConfig.Difficulty;
        var options = MyLeagueBrain.DetectDifficultyOptions(ocr);
        var selected = MyLeagueBrain.DetectSelectedDifficulty(frame, ocr);

        // 关键比赛：难度由游戏指定（受限横幅在 OCR 里），按默认直接确认开赛，绝不改选
        // ——用户 2026-10-12 实机反馈（bot-20261012-015835 02:00）：受限页被当普通页
        // 导航（上×2 改了难度），要求关键比赛不按↑。
        if (MyLeagueBrain.RespectGameDifficulty(_nextMatchIsKey, ocr))
        {
            _log("[我的联赛] 关键比赛：难度由游戏指定，按游戏默认直接确认开赛（不改选）", LogLevel.Info);
            await _engine.Pad.Confirm();
            return 3000;
        }

        // 所选难度不在当前选项中（OCR 漏读难度词等）→ 尊重游戏默认（蓝勾），直接确认
        if (!options.Contains(wanted))
        {
            _log($"[我的联赛] 难度「{wanted}」不在可选列表（未检出），按游戏默认确认开赛", LogLevel.Warn);
            await _engine.Pad.Confirm();
            return 3000;
        }
        if (selected == wanted)
        {
            _log($"[我的联赛] 难度已为「{wanted}」，直接确认开赛", LogLevel.Success);
            await _engine.Pad.Confirm();
            return 3000;
        }
        if (selected is null)
        {
            _log("[我的联赛] 蓝勾未检出，按当前难度直接确认开赛", LogLevel.Warn);
            await _engine.Pad.Confirm();
            return 3000;
        }

        int n = options.Count;
        int k = options.IndexOf(wanted) + 1; // 1 起
        int steps = n + 1 - k;
        _log($"[我的联赛] 难度 {selected} → {wanted}（{n} 选项中第 {k} 行）：上×{steps} 勾选 → 下×{steps} 右×1 确认", LogLevel.Info);
        await _engine.Pad.DpadRepeat(PadDir.Up, steps);
        await _engine.Pad.Confirm();                 // 勾选所选难度
        await _engine.Pad.DpadRepeat(PadDir.Down, steps);
        await _engine.Pad.Dpad(PadDir.Right);        // 底行：取消 → 确认
        await _engine.Pad.Confirm();                 // 确认开赛
        return 3500;
    }

    /// <summary>主页面板核验：全部已拥有 → 交正常流程开赛（按 A 前统一盲归位）；
    /// 否则进入 GoingPoints 盲归位导航至兑换积分。
    /// 两个出口都先左拨收回右侧面板——面板展开时 D-pad 磁贴导航无效、且面板内容抬高
    /// 右侧磁贴亮度（2026-10-11 22:10 实机：面板开着按 Left 光标纹丝不动，
    /// 兑换积分=201/赛季纪录=182 假聚焦；当天全部"导航卡死"均源于此）。</summary>
    private async Task<int> EvaluatePanelAsync(OcrResult ocr)
    {
        // 收回面板（右拨展开的逆操作），再开始 D-pad 导航
        await _engine.Pad.RsFlick(PadDir.Left);
        _log("[我的联赛] 收回右侧面板，开始磁贴导航", LogLevel.Info);

        var missing = _configuredItems.Where(t => !MyLeagueBrain.IsOwned(ocr, t)).ToList();
        if (missing.Count == 0)
        {
            _purchaseSatisfied = true;
            _phase = PurchasePhase.Idle; // 正常流程按 A 前统一盲归位到前往比赛
            _navAttempts = 0;
            _log("[我的联赛] 已有物品满足设置，正常前往比赛", LogLevel.Success);
            return 300;
        }
        if (++_verifyRetries > 2)
        {
            _purchaseSatisfied = true;
            _phase = PurchasePhase.Idle; // 放弃本场核验，正常前往比赛（下场比赛后自动重试）
            _navAttempts = 0;
            _log($"[我的联赛] 购买后重验仍缺 {missing.Count} 项，本场放弃（下一场比赛结束后自动重试）", LogLevel.Warn);
            return 300;
        }
        _log($"[我的联赛] 缺 {missing.Count} 项道具（{string.Join("、", missing.Select(DescribeItem))}），智能导航至兑换积分", LogLevel.Info);
        _phase = PurchasePhase.GoingPoints;
        _navAttempts = 0;
        return 1200; // 下一轮走盲归位序列（前往比赛→下×2→右→上×2→A，全确定边）
    }

    /// <summary>按设置生成目标清单（只生成一次；按物品页行序升序排序——增量下移购买）。</summary>
    private void EnsureConfiguredItems()
    {
        if (_itemsInitialized) return;
        _itemsInitialized = true;
        var c = LeagueConfig;
        var targets = new List<MyLeagueBrain.MlItemTarget>();
        if (c.BuyExpBoost)
            targets.Add(new MyLeagueBrain.MlItemTarget(MyLeagueBrain.MlItemKind.ExpBoost, c.ExpBoostLevel));
        if (c.BuyCoachBoost)
            targets.Add(new MyLeagueBrain.MlItemTarget(MyLeagueBrain.MlItemKind.CoachBoost));
        if (c.BuyConditionBoost)
            targets.Add(new MyLeagueBrain.MlItemTarget(MyLeagueBrain.MlItemKind.ConditionBoost, c.ConditionLevel));
        _configuredItems = MyLeagueBrain.SortByItemRow(targets);
        if (_configuredItems.Count > 0)
            _log($"[我的联赛] 道具自动购买已启用（按行序）：{string.Join("、", _configuredItems.Select(DescribeItem))}", LogLevel.Info);
    }

    private void ResetPhase(string reason)
    {
        // 跨阶段失败预算（与 EvaluatePanelAsync 的重验共用 _verifyRetries，比赛结束清零）：
        // 连续 3 次失败本场放弃核验直接开赛——否则 Idle→右拨→失败→Idle 无限循环
        // （实机 bot-20261012-154435：面板切换失败后每 ~25s 重来一次，4 分钟不开赛）。
        _verifyRetries++;
        _log($"[我的联赛] 购买流程退出：{reason}", LogLevel.Warn);
        _phase = PurchasePhase.Idle;
        _navAttempts = 0;
        if (_verifyRetries > 2)
        {
            _purchaseSatisfied = true;
            _log("[我的联赛] 购买流程连续 3 次失败，本场放弃道具核验，直接开赛（下场比赛结束后自动重试）", LogLevel.Warn);
        }
    }

    /// <summary>OCR 行摘要（面板切换失败等远程排障用，截断防日志膨胀）。</summary>
    protected static string OcrDigest(OcrResult ocr)
    {
        var text = string.Join(" | ", ocr.Lines.Select(l => l.Text.Replace(" ", "")));
        return text.Length <= 320 ? text : text[..320];
    }

    private static string DescribeItem(MyLeagueBrain.MlItemTarget t) => t.Kind switch
    {
        MyLeagueBrain.MlItemKind.ExpBoost => $"经验值加成 {t.Level}",
        MyLeagueBrain.MlItemKind.CoachBoost => "教练强化",
        MyLeagueBrain.MlItemKind.ConditionBoost => $"状态加成 {t.Level}",
        _ => t.Kind.ToString(),
    };
}
