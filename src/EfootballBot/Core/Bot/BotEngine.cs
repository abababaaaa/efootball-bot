using System.Diagnostics;
using EfootballBot.Core.Action;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Config;
using EfootballBot.Core.Input;
using EfootballBot.Core.Graph;
using EfootballBot.Core.Observation;
using EfootballBot.Core.Overlay;
using EfootballBot.Core.Probe;
using EfootballBot.Core.System;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Bot;

/// <summary>挂机统计。</summary>
public sealed class BotStats
{
    public int MatchesDone;
    public int KeyMatches;
    public int NormalMatches;
    public int Forfeited;
    public int RewardsClaimed;
    public int EventsDone;
    public DateTime? StartedAt;
    public GameScreen LastScreen = GameScreen.Unknown;
    public string LastScreenEvidence = "";
    public string LastError = "";
}

public sealed class BotEngine : IDisposable
{
    private readonly AppConfig _cfg;
    public readonly VirtualGamepad360 Pad360;
    public readonly Pad Pad;
    public readonly ActionExecutor Actions;
    public readonly OverlayEngine Overlay;
    public readonly WindowCapture Capture = new();
    private DxgiDesktopCapture? _dxgi;
    public readonly OcrRecognizer Ocr;
    public readonly ObservationService Observation;
    private readonly ProbeEngine _probeEngine;
    public readonly PageGraph Graph;
    public readonly NavigationService Navigation;
    public readonly BotStats Stats = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Observation? _lastObs;
    private long _lastUnknownDumpTick;
    private GameWindowInfo? _window;

    public event Action<string, LogLevel>? Log;
    public event Action<BotStats>? StatsChanged;
    public event Action<Observation>? Observed;

    // ---------------- 可视化面板事件（Worker→GUI 图谱面板） ----------------
    public event Action<ScreenEvent>? ScreenChanged;
    public event Action<ProbeEvent>? ProbeUpdated;
    public event Action<NavEvent>? NavPathFound;

    public bool IsRunning => _loop is { IsCompleted: false };
    public GameWindowInfo? Window => _window;
    public FrameData? LatestFrame => Observation.LatestFrame;
    public OcrResult LatestOcr => Observation.LatestOcr;

    public BotEngine(AppConfig cfg)
    {
        _cfg = cfg;
        Pad360 = new VirtualGamepad360();
        Pad = new Pad(Pad360, cfg.Timing);
        Actions = new ActionExecutor(this, Pad);
        Overlay = new OverlayEngine(this, Actions, Pad);
        Ocr = new OcrRecognizer(cfg.OcrEngine, cfg.OcrLanguage);
        LogInternal($"OCR 引擎：{Ocr.LanguageTag}（{Ocr.EngineKind}）", LogLevel.Info);
        _probeEngine = new ProbeEngine(PageCatalog.Pages);
        Graph = new PageGraph(PageCatalog.Edges);
        Navigation = new NavigationService(
            Graph,
            new BotNavIO(this),
            new NavLimits(_cfg.Nav.MaxHops, _cfg.Nav.UnknownBudget, _cfg.Nav.TimeoutMinutes),
            LogInternal,
            ev => NavPathFound?.Invoke(ev));
        Observation = new ObservationService(Capture, Ocr, cfg);
        Observation.Log = (msg, lv) => LogInternal(msg, lv);

        Capture.CaptureClosed += (_, msg) => LogInternal($"画面捕获已结束：{msg}", LogLevel.Warn);
    }

    public IReadOnlyList<GameWindowInfo> DetectWindows() => GameWindow.FindAll();

    // ---------------- 生命周期 ----------------

    public async Task StartAsync(string mode)
    {
        if (IsRunning) return;
        _probeEngine.Reset();
        var token = await PrepareSessionAsync(mode == "cndaily" ? CnConfig.ProcessName : null);

        _loop = Task.Run(async () =>
        {
            try
            {
                // M0 导航框架：启动后先执行一次全页面智能寻路到模式枢纽。
                // 到达后的比赛/活动循环待 M1 场景重写接入，当前寻路结束后仅保持观察。
                GameScreen? targetScreen = mode switch
                {
                    "myleague" => GameScreen.MyLeagueHome,
                    "intlweekly" => GameScreen.EventHub,
                    _ => null,
                };

                if (targetScreen is { } ts)
                {
                    LogInternal($"开始智能寻路：任意画面 → {ScreenName(ts)}", LogLevel.Info);
                    var navResult = await GoToAsync(ts.ToString(), null, token);
                    LogInternal(navResult.Ok
                        ? $"寻路成功，已到达{ScreenName(ts)}（M0 导航验证通过，等待场景循环接入）"
                        : $"寻路结束未到达目标（{navResult.Outcome}）：{navResult.Message}",
                        navResult.Ok ? LogLevel.Success : LogLevel.Warn);
                }
                else
                {
                    LogInternal($"引擎框架已就绪（模式：{mode}），该模式暂未接入寻路，仅观察画面", LogLevel.Success);
                }

                while (!token.IsCancellationRequested)
                {
                    await ObserveAsync(token);
                    await Task.Delay(500, token);
                }
            }
            catch (OperationCanceledException)
            {
                LogInternal("已停止。", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Stats.LastError = ex.Message;
                LogInternal($"运行异常：{ex}", LogLevel.Error);
            }
            finally
            {
                StatsChanged?.Invoke(Stats);
            }
        }, token);

        EmitStats();
    }

    /// <summary>队列/单场景共用的会话初始化：驱动检测 → 找窗口 → 启动捕获 → 接手柄 → 建取消令牌。</summary>
    private async Task<CancellationToken> PrepareSessionAsync(string? processName = null)
    {
        if (!VirtualGamepad360.IsDriverInstalled())
            throw new InvalidOperationException("未检测到 ViGEmBus 驱动，请先在“环境检测”中安装。");

        processName ??= GameWindow.GlobalProcessName;
        _window = GameWindow.FindFirst(processName)
            ?? throw new InvalidOperationException(processName.Contains("Online")
                ? "未找到正在运行的国服游戏窗口（eFootballOnline），请先启动「实况足球在线」。"
                : "未找到正在运行的 eFootball 游戏窗口，请先启动游戏。");

        // 不再强制修改游戏窗口（独占全屏会抵抗，且改动用户窗口副作用大）。
        // 非 720p 只在开始捕获时给一条性能提醒；高分辨率的捕获开销由帧池高≤720 降采样缓解。

        // 已在捕获另一个窗口（如国际服→国服切换）时，先停掉旧捕获
        if (Capture.IsRunning && Capture.Hwnd != _window.Handle)
        {
            LogInternal("切换游戏模块：重新绑定画面捕获窗口", LogLevel.Info);
            Capture.Stop();
        }
        if (_dxgi is { IsRunning: true } && _dxgi.Hwnd != _window.Handle)
            _dxgi.Stop();

        if (!Capture.IsRunning && !(_dxgi?.IsRunning ?? false))
        {
            var (w, h) = GameWindow.GetClientSize(_window.Handle);
            if (w <= 0 || h <= 0) (w, h) = (1280, 720);
            if (h > 760)
                LogInternal($"当前游戏分辨率 {w}×{h} 高于 720p，已自动降采样；DXGI 模式可显著降低高分辨率下的捕获开销", LogLevel.Warn);
            await StartCaptureAsync(w, h);
        }

        Observation.Window = _window;

        Pad360.Connect();
        _ = Task.Run(EnsureHidHideIsolation); // 静默检查虚拟手柄黑名单覆盖（不阻塞启动）
        LogInternal($"虚拟手柄已连接（XInput 玩家 {Pad360.UserIndex + 1}），OCR 语言：{Ocr.LanguageTag}", LogLevel.Success);

        Stats.StartedAt = DateTime.Now;
        _cts = new CancellationTokenSource();
        return _cts.Token;
    }

    /// <summary>
    /// 虚拟手柄实例可能跨会话变化（黑名单按完整实例路径匹配）。
    /// 连接后轮询枚举：若实例未被黑名单覆盖且已启用隔离，静默触发最高权限计划任务同步。
    /// 读黑名单（GET）普通权限可用；sync 子进程自行提权执行 SET，全程无 UAC 弹窗。
    /// </summary>
    private async Task EnsureHidHideIsolation()
    {
        try
        {
            var hh = _cfg.HidHide;
            if (!hh.Enabled || !Input.HidHideManager.IsDriverInstalled()) return;

            // ViGEm 设备经系统注册有延迟，最多等 3 秒
            List<string> vigem = new();
            for (int i = 0; i < 10; i++)
            {
                vigem = Input.HidDeviceEnumerator.FindViGEmInstances();
                if (vigem.Count > 0) break;
                await Task.Delay(300);
            }
            if (vigem.Count == 0) return;

            var bl = Input.HidHideIoctl.GetMultiString(Input.HidHideIoctl.IoctlGetBlacklist);
            if (vigem.All(v => bl.Contains(v, StringComparer.OrdinalIgnoreCase))) return; // 已覆盖

            LogInternal($"检测到虚拟手柄实例未加入 HidHide 黑名单（{string.Join(";", vigem)}），触发静默同步", LogLevel.Info);
            Input.HidHideManager.RunSyncTaskSilently();
        }
        catch (Exception ex)
        {
            LogInternal($"HidHide 黑名单检查跳过：{ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>
    /// 按配置启动捕获后端：DXGI 桌面复制（默认，高性能/全分辨率）或 WGC（备用，窗口专属/可被遮挡）。
    /// DXGI 启动异常或 5s 内无桌面帧时自动回退 WGC，保证挂机可用。
    /// </summary>
    private async Task StartCaptureAsync(int w, int h)
    {
        if (_cfg.CaptureMode == CaptureMode.Dxgi)
        {
            try
            {
                _dxgi ??= new DxgiDesktopCapture();
                if (!_dxgi.IsRunning)
                {
                    LogInternal($"开始捕获游戏窗口（DXGI 桌面复制，{w}×{h}，高性能全分辨率）", LogLevel.Info);
                    LogInternal("DXGI 捕获整个显示器画面：挂机时游戏窗口请勿最小化或被其他窗口遮挡", LogLevel.Warn);
                    await Task.Run(() => _dxgi.Start(_window!.Handle, w, h));
                }
                // Desktop Duplication 仅在桌面内容变化时产出帧，等待首帧最多 5s
                var deadline = DateTime.Now.AddSeconds(5);
                while (DateTime.Now < deadline && _dxgi.LastFrameAt == DateTime.MinValue)
                    await Task.Delay(100);
                if (_dxgi.LastFrameAt == DateTime.MinValue)
                    throw new TimeoutException("DXGI 5s 内未收到桌面帧（画面静止/设备不支持/被远程桌面占用）");

                if (Observation.Provider != _dxgi)
                {
                    try { Capture.Stop(); } catch { }
                    Observation.AttachProvider(_dxgi);
                }
                LogInternal("DXGI 桌面复制捕获已就绪", LogLevel.Success);
                return;
            }
            catch (Exception ex)
            {
                LogInternal($"DXGI 捕获启动失败，自动回退 WGC：{ex.Message}", LogLevel.Warn);
                try { _dxgi?.Stop(); } catch { }
            }
        }

        if (!Capture.IsRunning)
        {
            LogInternal($"开始捕获游戏窗口（WGC 窗口捕获，{w}×{h}，后台可捕获/可被遮挡）", LogLevel.Info);
            await Task.Run(() => Capture.Start(_window!.Handle, w, h));
        }
        if (Observation.Provider != Capture)
            Observation.AttachProvider(Capture);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { Pad360.ResetAll(); } catch { }
        _lastObs = null;
        // 注意：此处不断开虚拟手柄。手柄一旦被游戏看到「断开→重连」，
        // eFootball 会把光标停靠到顶部头部的「手柄归属」图标上，重连设备的
        // Left/A 全部失效（焦点不在磁贴行）。设备在 Stop→Start 间保持存活，
        // XInput 槽位在应用退出（Dispose）时释放；如需立刻让位给真实手柄请关闭本程序。
    }

    // ---------------- 观察 ----------------

    /// <summary>OCR 间隔自适应：比赛中放宽到 2000ms，其余用配置值（默认 900ms）。</summary>
    private int EffectiveOcrIntervalMs()
        => _lastObs?.Screen == GameScreen.InMatch
            ? Math.Max(_cfg.Timing.OcrIntervalMs, 2000)
            : _cfg.Timing.OcrIntervalMs;

    /// <summary>
    /// 获取一帧新鲜画面 → OCR → 分类。捕获+OCR+节流由 ObservationService 负责，
    /// 此处仅做画面分类、统计更新和事件派发。
    /// </summary>
    public async Task<Observation> ObserveAsync(CancellationToken ct)
    {
        var (f, ocr) = await Observation.ObserveAsync(ct, EffectiveOcrIntervalMs());

        // 距上次 OCR 不足间隔时 ObservationService 返回缓存，直接复用上一次分类结果
        if (_lastObs is not null && _lastObs.Frame == f && _lastObs.Ocr == ocr)
            return _lastObs;

        // 声明式探针引擎 ProbeEngine 分类画面
        var probeObs = new Observation(f, ocr, GameScreen.Unknown, 0, Array.Empty<string>());
        var newResult = _probeEngine.Classify(probeObs);
        GameScreen screen = newResult.Screen;
        double score = newResult.RawScore;
        List<string> ev = newResult.Evidence;

        var obs = new Observation(f, ocr, screen, score, ev);
        _lastObs = obs;
        Observation.ApplyCaptureThrottle(screen);

        // ---------------- 可视化事件派发（非阻塞，订阅方自行异常保护） ----------------
        ScreenChanged?.Invoke(new ScreenEvent
        {
            Screen = screen,
            Score = score,
            Evidence = ev,
        });

        var topCands = _probeEngine.GetTopCandidates(probeObs, 5);
        if (topCands.Count > 0)
        {
            ProbeUpdated?.Invoke(new ProbeEvent
            {
                TopCandidates = topCands.Select(c => new ProbeCandidate
                {
                    Screen = c.Screen,
                    RawScore = c.Raw,
                    Evidence = c.Ev,
                }).ToList(),
            });
        }

        if (screen != Stats.LastScreen)
        {
            Stats.LastScreen = screen;
            Stats.LastScreenEvidence = string.Join(",", ev);
            LogInternal($"画面 → {ScreenName(screen)}（{score:F1} {Stats.LastScreenEvidence}）", LogLevel.Debug);
            EmitStats();
        }

        // 未识别时周期性输出 OCR 原文，便于校准关键词
        long now = Environment.TickCount64;
        if (screen == GameScreen.Unknown && now - _lastUnknownDumpTick > 5000)
        {
            _lastUnknownDumpTick = now;
            var words = ocr.MergedWords.Take(25).Select(w => w.Text);
            LogInternal($"[OCR原文] {string.Join(" | ", words)}", LogLevel.Warn);
        }

        Observed?.Invoke(obs);
        return obs;
    }

    /// <summary>等待进入任一指定画面。</summary>
    public async Task<Observation> WaitForAnyAsync(GameScreen[] set, int timeoutS, CancellationToken ct, string? why = null)
    {
        var sw = Stopwatch.StartNew();
        var last = _lastObs;
        while (sw.Elapsed.TotalSeconds < timeoutS)
        {
            ct.ThrowIfCancellationRequested();
            last = await ObserveAsync(ct);
            if (set.Contains(last.Screen)) return last;
            await Task.Delay(250, ct);
        }
        throw new TimeoutException($"等待画面 [{string.Join("/", set.Select(ScreenName))}] 超时（{timeoutS}s）{(why is null ? "" : "：" + why)}");
    }

    /// <summary>等待画面离开当前状态。</summary>
    public async Task<bool> WaitLeaveAsync(GameScreen screen, int timeoutS, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < timeoutS)
        {
            ct.ThrowIfCancellationRequested();
            var o = await ObserveAsync(ct);
            if (o.Screen != screen) return true;
            await Task.Delay(250, ct);
        }
        return false;
    }

    // ---------------- 通用交互原语 ----------------

    /// <summary>
    /// 处理断线弹窗 / 通用确认框，返回 true 表示做过处理。
    /// 委托给 OverlayEngine。
    /// </summary>
    public Task<bool> HandlePopupsAsync(Observation o, CancellationToken ct)
        => Overlay.HandlePopupsAsync(o, ct);

    /// <summary>
    /// 在垂直菜单中按 OCR 文字找到目标项：逐项下移并检测该项高亮（黄 / 亮白），命中后按 A。
    /// </summary>
    public Task<bool> SelectMenuByTextAsync(string[] normKeywords, CancellationToken ct, int maxItems = 8)
        => Actions.SelectMenuByTextAsync(normKeywords, ct, maxItems);

    /// <summary>
    /// 卡片白色高亮占比：联赛主页焦点卡整卡变白底黑字（白占比高），
    /// 非焦点卡为深灰底+少量白色标题文字。用于判定 Down→Right 后焦点落在哪张卡。
    /// 区域以卡片标题词为基准向左扩到图标、向右/上下扩出卡面左上主体。
    /// </summary>
    internal static double CardWhiteScore(FrameData frame, OcrWord w)
    {
        var r = new NormRect(
            Math.Max(0, w.X - 0.10), Math.Max(0, w.Y - 0.05),
            Math.Min(1, w.W + 0.24), Math.Min(1, w.H + 0.11));
        return FrameAnalyzer.ColorRatio(frame, HsvFilter.White, r);
    }

    /// <summary>
    /// 指针感知点击：找到目标按钮（OK/下一步/确定），检测其是否高亮（指针已在上面），
    /// 不在则按方向键朝它移动指针，到位后按 A。返回是否成功点击。
    /// 焦点检测使用蓝+青+亮多色滤镜，兼容 eFootball 各种按钮高亮风格。
    /// exactMatch=true 时用精确词匹配（避免 "ok" 误匹配 "Fukuoka" 等队名）。
    /// </summary>
    public Task<bool> PressButtonAsync(string[] targetKeywords, CancellationToken ct, int maxMoves = 8, bool exactMatch = false)
        => Actions.PressButtonAsync(targetKeywords, ct, maxMoves, exactMatch);

    /// <summary>卡死恢复：优先找“下一步/确定/确认”按 A；否则等待重观察。委托给 OverlayEngine。</summary>
    public Task RecoverAsync(Observation o, CancellationToken ct)
        => Overlay.RecoverAsync(o, ct);

    public void ResetUnknown() => Overlay.ResetUnknown();

    // ---------------- 导航 ----------------

    /// <summary>
    /// 二维卡片/磁贴导航：根据 OCR 坐标把焦点移动到目标卡片并按 A。
    /// 通过比较各候选卡片外扩区域的白色描边亮度找当前焦点。
    /// 选词一律用原始 Words（不能用 MergedWords 行合并词：同一 y 高度的多张卡标题会被
    /// 拼成「赛程表兑换积分」这类横跨两卡的假词，中心被左卡拖走导致只上下移动、永不右移）。
    /// </summary>
    public Task<bool> FocusAndConfirmAsync(string[] targetKeywords, string[] allCardKeywords, CancellationToken ct, int maxMoves = 14)
        => Actions.FocusAndConfirmAsync(targetKeywords, allCardKeywords, ct, maxMoves);

    /// <summary>
    /// 在 OCR 中定位某个卡片标题词的真实外接框，优先级：
    /// ① 原始词完全相等；② 原始词包含关键词且最短；
    /// ③ 原始碎词按阅读序拼接后包含关键词（中文常被逐字拆开）→ 命中间的词框合并；
    /// ④ 兜底：行合并词中包含关键词且最短的（可能横跨多卡，仅最后手段）。
    /// </summary>
    internal static OcrWord? FindCardWord(OcrResult ocr, string keyword)
    {
        keyword = OcrText.Norm(keyword);
        var words = ocr.Words
            .Select(w => (Word: w, Norm: OcrText.Norm(w.Text)))
            .Where(t => t.Norm.Length > 0)
            .ToList();

        // ① 完全相等
        var exact = words.Where(t => t.Norm == keyword)
            .Select(t => t.Word)
            .OrderBy(w => w.W * w.H).FirstOrDefault();
        if (exact is not null) return exact;

        // ② 单个原始词包含关键词，取最短（避开粘连长句）
        var contains = words.Where(t => t.Norm.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Word.Text.Length)
            .Select(t => t.Word).FirstOrDefault();
        if (contains is not null) return contains;

        // ③ 碎词序列（先按 Y 行、再按 X）拼出关键词：把命中间的碎词外接框合并成一个 OcrWord
        var ordered = words.OrderBy(t => t.Word.Y).ThenBy(t => t.Word.X).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            var sb = new global::System.Text.StringBuilder();
            for (int j = i; j < ordered.Count && sb.Length < keyword.Length + 8; j++)
            {
                sb.Append(ordered[j].Norm);
                string s = sb.ToString();
                int idx = s.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                // 关键词在拼接串中的字符区间 [idx, idx+keyword.Length)，
                // 反推覆盖该区间的碎词（假设归一化不改长度，极个别误识时区间会略偏）
                int pos = 0;
                var hit = new List<OcrWord>();
                foreach (var t in ordered.Skip(i))
                {
                    int next = pos + t.Norm.Length;
                    if (next > idx && pos < idx + keyword.Length) hit.Add(t.Word);
                    pos = next;
                    if (pos >= idx + keyword.Length) break;
                }
                if (hit.Count > 0)
                {
                    double x0 = hit.Min(w => w.X), y0 = hit.Min(w => w.Y);
                    double x1 = hit.Max(w => w.X + w.W), y1 = hit.Max(w => w.Y + w.H);
                    return new OcrWord(keyword, x0, y0, x1 - x0, y1 - y0);
                }
            }
        }

        // ④ 兜底：行合并词里最短的命中（可能横跨多卡）
        return ocr.MergedWords
            .Where(w => OcrText.Norm(w.Text).Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .OrderBy(w => w.Text.Length)
            .FirstOrDefault();
    }

    /// <summary>
    /// 全页面智能寻路：任意画面出发，闭环导航到目标页（观察→过境处理→BFS 单跳→落点验证）。
    /// 半途接续免费获得；返回详细停机结果。
    /// </summary>
    public Task<NavResult> GoToAsync(string targetPage, NavContext? ctx = null, CancellationToken ct = default)
        => Navigation.NavigateToAsync(targetPage, ctx ?? new NavContext(), ct);

    // ---------------- 杂项 ----------------

    public void LogInternal(string msg, LogLevel lv)
    {
        Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] {msg}", lv);
        EmitStats();
    }

    private void EmitStats() => StatsChanged?.Invoke(Stats);

    public static string ScreenName(GameScreen s) => s switch
    {
        GameScreen.Unknown => "未知",
        GameScreen.Loading => "加载中",
        GameScreen.TitleScreen => "标题画面",
        GameScreen.Home => "主页",
        GameScreen.MatchHub => "比赛菜单",
        GameScreen.EventHub => "活动列表",
        GameScreen.EventInfo => "挑战活动详情",
        GameScreen.ChallengeList => "挑战清单",
        GameScreen.EventSettings => "活动设置",
        GameScreen.DailyRewards => "每日奖励",
        GameScreen.MyLeagueHome => "我的联赛主页",
        GameScreen.Schedule => "赛程表",
        GameScreen.KeyMatchBadge => "关键比赛",
        GameScreen.PreMatch => "赛前设置",
        GameScreen.InMatch => "比赛中",
        GameScreen.HalfTime => "中场",
        GameScreen.FullTime => "全场结束",
        GameScreen.Result => "比赛结果",
        GameScreen.Rewards => "奖励页",
        GameScreen.CampaignBoard => "活动棋盘",
        GameScreen.ItemsBar => "道具栏",
        GameScreen.ShopConfirm => "购买确认",
        GameScreen.Dialog => "确认弹窗",
        GameScreen.Disconnected => "断线",
        _ => s.ToString(),
    };

    public void Dispose()
    {
        Stop();
        try { Observation.Dispose(); } catch { }
        try { Ocr.Dispose(); } catch { }
        try { _dxgi?.Dispose(); } catch { }
        try { Pad360.Dispose(); } catch { }
        GC.SuppressFinalize(this);
    }
}
