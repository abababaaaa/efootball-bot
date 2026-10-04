using System.Diagnostics;
using System.Runtime;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Config;
using EfootballBot.Core.Input;
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
    public readonly WindowCapture Capture = new();
    public readonly OcrRecognizer Ocr;
    private readonly ScreenClassifier _classifier;
    public readonly BotStats Stats = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private FrameData? _frame;
    private OcrResult _ocr = OcrResult.Empty;
    private Observation? _lastObs;
    private long _lastOcrTick;
    private long _lastUnknownDumpTick;
    private GameWindowInfo? _window;
    private GameWindow.SavedWindowState? _savedWinState;
    private (int X, int Y)? _offscreenPos;
    private bool _minimizedGame;
    private DateTime _gcLastRun = DateTime.Now;
    private int _unknownStreak;

    public event Action<string, LogLevel>? Log;
    public event Action<BotStats>? StatsChanged;
    public event Action<Observation>? Observed;

    public bool IsRunning => _loop is { IsCompleted: false };
    public GameWindowInfo? Window => _window;
    public FrameData? LatestFrame => _frame;
    public OcrResult LatestOcr => _ocr;

    public BotEngine(AppConfig cfg)
    {
        _cfg = cfg;
        Pad360 = new VirtualGamepad360();
        Pad = new Pad(Pad360, cfg.Timing);
        Ocr = new OcrRecognizer(cfg.OcrLanguage);
        _classifier = new ScreenClassifier();

        Capture.FrameUpdated += OnFrame;
        Capture.CaptureClosed += (_, msg) => LogInternal($"画面捕获已结束：{msg}", LogLevel.Warn);
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var f = Capture.TryGetLatest();
        if (f is not null) _frame = f;
    }

    public IReadOnlyList<GameWindowInfo> DetectWindows() => GameWindow.FindAll();

    // ---------------- 生命周期 ----------------

    public async Task StartAsync(string mode)
    {
        if (IsRunning) return;
        _classifier.Reset(); // 清除上一次的比赛锁定迟滞状态
        var token = await PrepareSessionAsync(mode == "cndaily" ? CnConfig.ProcessName : null);

        _loop = Task.Run(async () =>
        {
            try
            {
                ScenarioBase scenario = mode switch
                {
                    "myleague" => new MyLeagueScenario(this, _cfg, token),
                    "daily" => new IntlWeeklyScenario(this, _cfg, token),
                    "intlweekly" => new IntlWeeklyScenario(this, _cfg, token),
                    "cndaily" => new CnDailyScenario(this, _cfg, token),
                    _ => throw new ArgumentOutOfRangeException(nameof(mode)),
                };
                LogInternal($"场景启动：{scenario.DisplayName}", LogLevel.Success);
                await scenario.RunAsync();
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

        // 挂机自动窗口化：全屏超宽（如 3440×1440）下 DWM 每帧合成近 500 万像素是卡顿根因，
        // 与捕获无关（停止捕获 DWM 仍 49%）。窗口化到 1280×720 后 DWM 合成像素量降 6 倍，
        // 且该分辨率正是全部识别参数的校准基准。停止挂机时还原。
        if (_cfg.AutoWindowizeOnStart)
        {
            var saved = GameWindow.WindowizeTo(_window.Handle);
            if (saved is not null)
            {
                await Task.Delay(600); // 等游戏重排 UI
                // 验证窗口化是否生效：游戏可能抵抗样式修改（独占全屏），
                // 此时客户区仍 >760 → 回退样式 + 移到屏幕外（DWM 合成开销归零）
                var (_, _, _, _, _, ch) = GameWindow.GetWindowFrame(_window.Handle);
                if (ch > 0 && ch <= 760)
                {
                    _savedWinState = saved;
                    LogInternal("已将游戏窗口化为 1280×720（降低系统负载；停止挂机时自动还原）", LogLevel.Info);
                }
                else
                {
                    GameWindow.RestoreWindow(_window.Handle, saved);
                    var pos = GameWindow.MoveOffscreen(_window.Handle);
                    if (pos is not null)
                    {
                        _offscreenPos = pos.Value;
                        LogInternal("窗口化被游戏抵抗，已将游戏窗口移到屏幕外（画面不可见但挂机正常；停止时移回）", LogLevel.Info);
                    }
                    else if (GameWindow.MinimizeWindow(_window.Handle))
                    {
                        // 最后的降级：最小化（DWM 对最小化窗口合成开销为零，WGC 通常仍能捕获）
                        _minimizedGame = true;
                        LogInternal("窗口化与屏幕外移动均被抵抗，已最小化游戏窗口（画面不可见但挂机正常；停止时还原）", LogLevel.Info);
                    }
                    else
                    {
                        LogInternal("窗口化/屏幕外移动/最小化均被游戏抵抗，保持全屏运行（可能较卡）", LogLevel.Warn);
                    }
                    await Task.Delay(300);
                }
            }
        }

        // 已在捕获另一个窗口（如国际服→国服切换）时，先停掉旧捕获
        if (Capture.IsRunning && Capture.Hwnd != _window.Handle)
        {
            LogInternal("切换游戏模块：重新绑定画面捕获窗口", LogLevel.Info);
            Capture.Stop();
        }

        if (!Capture.IsRunning)
        {
            var (w, h) = GameWindow.GetClientSize(_window.Handle);
            if (w <= 0 || h <= 0) (w, h) = (1280, 720);
            LogInternal($"开始捕获游戏窗口（{w}×{h}，后台可捕获）", LogLevel.Info);
            await Task.Run(() => Capture.Start(_window.Handle, w, h));
        }

        Pad360.Connect();
        LogInternal($"虚拟手柄已连接（XInput 玩家 {Pad360.UserIndex + 1}），OCR 语言：{Ocr.LanguageTag}", LogLevel.Success);

        Stats.StartedAt = DateTime.Now;
        _gcLastRun = DateTime.Now;
        _cts = new CancellationTokenSource();
        return _cts.Token;
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { Pad360.ResetAll(); } catch { }
        _frame = null;
        _lastObs = null;
        // 还原挂机开始时自动窗口化的游戏窗口
        if (_savedWinState is not null && _window is not null)
        {
            try
            {
                GameWindow.RestoreWindow(_window.Handle, _savedWinState);
                LogInternal("已还原游戏窗口尺寸。", LogLevel.Info);
            }
            catch { }
            _savedWinState = null;
        }
        if (_offscreenPos is not null && _window is not null)
        {
            try
            {
                GameWindow.MoveBack(_window.Handle, _offscreenPos.Value.X, _offscreenPos.Value.Y);
                LogInternal("已将游戏窗口移回屏幕内。", LogLevel.Info);
            }
            catch { }
            _offscreenPos = null;
        }
        if (_minimizedGame && _window is not null)
        {
            try
            {
                GameWindow.RestoreFromMinimize(_window.Handle);
                LogInternal("已还原游戏窗口。", LogLevel.Info);
            }
            catch { }
            _minimizedGame = false;
        }
        // 注意：此处不断开虚拟手柄。手柄一旦被游戏看到「断开→重连」，
        // eFootball 会把光标停靠到顶部头部的「手柄归属」图标上，重连设备的
        // Left/A 全部失效（焦点不在磁贴行）。设备在 Stop→Start 间保持存活，
        // XInput 槽位在应用退出（Dispose）时释放；如需立刻让位给真实手柄请关闭本程序。
    }

    // ---------------- 观察 ----------------

    /// <summary>
    /// 按画面状态调整捕获帧间隔（背压节流，降低全屏 DWM 负载）：
    /// 比赛中 2000ms（挂机只需低频确认未结束）；加载/未知 500ms；其余全速 33ms。
    /// 选卡相关画面（活动列表/挑战清单/弹窗）必须全速：MoveAndSettle 依赖 150ms 轮询签名。
    /// </summary>
    private void ApplyCaptureThrottle(GameScreen screen)
    {
        int ms = screen switch
        {
            GameScreen.InMatch => 2000,
            GameScreen.Loading or GameScreen.Unknown => 500,
            _ => 33,
        };
        Capture.SetMinFrameInterval(ms);
    }

    /// <summary>OCR 间隔自适应：比赛中放宽到 2000ms，其余用配置值（默认 900ms）。</summary>
    private int EffectiveOcrIntervalMs()
        => _lastObs?.Screen == GameScreen.InMatch
            ? Math.Max(_cfg.Timing.OcrIntervalMs, 2000)
            : _cfg.Timing.OcrIntervalMs;

    private int[] _frameSize = new int[2];

    public async Task<Observation> ObserveAsync(CancellationToken ct)
    {
        // 等待第一帧
        int wait = 0;
        while (_frame is null && wait < 200)
        {
            await Task.Delay(100, ct);
            wait++;
            ct.ThrowIfCancellationRequested();
        }
        var f = _frame ?? throw new InvalidOperationException("没有可用的游戏画面（游戏窗口可能已最小化）。");

            // 窗口尺寸变化时重建捕获
            if (_window is not null)
            {
                var (cw, ch) = GameWindow.GetClientSize(_window.Handle);
                if (cw > 0 && ch > 0 && (cw != f.Width || ch != f.Height) && (_frameSize[0] != cw || _frameSize[1] == 0))
                {
                    _frameSize[0] = cw; _frameSize[1] = ch;
                    Capture.Resize(cw, ch);
                }
            }

            long now = Environment.TickCount64;
            // 定期 GC（每 5 分钟，仅当配置开启时）
            if (_cfg.PeriodicGc && (DateTime.Now - _gcLastRun).TotalMinutes >= 5)
            {
                _gcLastRun = DateTime.Now;
                LogInternal("执行定期内存整理（GC.Collect Aggressive）…", LogLevel.Debug);
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                GC.WaitForPendingFinalizers();
                LogInternal($"GC 完成。Gen0={GC.CollectionCount(0)} Gen1={GC.CollectionCount(1)} Gen2={GC.CollectionCount(2)} Mem={GC.GetTotalMemory(false):N0}", LogLevel.Debug);
            }
            if (now - _lastOcrTick < EffectiveOcrIntervalMs() && _lastObs is not null)
                return _lastObs;

            await Task.Yield();
            var ocr = await Ocr.RecognizeAsync(f, ct);
            _ocr = ocr;
            _lastOcrTick = now;

            var (screen, score, ev) = _classifier.Classify(ocr, f);
            var obs = new Observation(f, ocr, screen, score, ev);
            _lastObs = obs;
            ApplyCaptureThrottle(screen);

            if (screen != Stats.LastScreen)
            {
                Stats.LastScreen = screen;
                Stats.LastScreenEvidence = string.Join(",", ev);
                LogInternal($"画面 → {ScreenName(screen)}（{score:F1} {Stats.LastScreenEvidence}）", LogLevel.Debug);
                EmitStats();
            }

            // 未识别时周期性输出 OCR 原文，便于校准关键词
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

    public Task<Observation> WaitForAsync(GameScreen screen, int timeoutS, CancellationToken ct)
        => WaitForAnyAsync(new[] { screen }, timeoutS, ct);

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
    /// </summary>
    public async Task<bool> HandlePopupsAsync(Observation o, CancellationToken ct)
    {
        if (o.Is(GameScreen.Disconnected))
        {
            LogInternal("检测到断线 / 通信错误弹窗，尝试确认返回…", LogLevel.Warn);
            await Pad.Confirm();
            await Task.Delay(2000, ct);
            return true;
        }
        if (o.Is(GameScreen.Dialog))
        {
            // 关键比赛弹窗：OK 按钮在右下角，需先 Up 把焦点移过去
            if (o.Ocr.Contains("关键比赛") || o.Ocr.Contains("keymatch"))
            {
                LogInternal("关键比赛确认弹窗：方向上 → A", LogLevel.Info);
                await Pad.Dpad(PadDir.Up);
                await Task.Delay(500, ct);
            }
            // 普通弹窗 OK/确定 默认就是焦点，直接按 A
            await Pad.Confirm();
            await Task.Delay(700, ct);
            return true;
        }
        return false;
    }

    /// <summary>
    /// 在垂直菜单中按 OCR 文字找到目标项：逐项下移并检测该项高亮（黄 / 亮白），命中后按 A。
    /// </summary>
    public async Task<bool> SelectMenuByTextAsync(string[] normKeywords, CancellationToken ct, int maxItems = 8)
    {
        for (int i = 0; i < maxItems; i++)
        {
            ct.ThrowIfCancellationRequested();
            var o = await ObserveAsync(ct);
            foreach (var kw in normKeywords)
            {
                var w = o.Ocr.FindFirst(kw);
                if (w is null) continue;
                var region = new NormRect(
                    Math.Max(0, w.X - 0.04), Math.Max(0, w.Y - w.H * 0.6),
                    Math.Min(1, w.W + 0.08), w.H * 2.2);
                double yellow = FrameAnalyzer.ColorRatio(o.Frame, HsvFilter.Yellow, region);
                double bright = FrameAnalyzer.ColorRatio(o.Frame, HsvFilter.Bright, region);
                if (yellow > 0.04 || bright > 0.35)
                {
                    LogInternal($"菜单选中「{kw}」（y={yellow:F2} b={bright:F2}），按 A", LogLevel.Debug);
                    await Pad.Confirm();
                    return true;
                }
            }
            await Pad.Dpad(PadDir.Down);
        }
        return false;
    }

    /// <summary>
    /// 指针感知点击：找到目标按钮（OK/下一步/确定），检测其是否蓝底高亮（指针已在上面），
    /// 不在则按方向键朝它移动指针，到位后按 A。返回是否成功点击。
    /// </summary>
    public async Task<bool> PressButtonAsync(string[] targetKeywords, CancellationToken ct, int maxMoves = 8)
    {
        // 弹窗里常见可聚焦按钮词（用于定位“当前指针在哪”）
        string[] allButtons = { "ok", "确定", "确认", "取消", "下一步", "跳过", "返回", "是", "否", "yes", "no", "cancel", "skip", "next", "back" };

        for (int move = 0; move < maxMoves; move++)
        {
            ct.ThrowIfCancellationRequested();
            var o = await ObserveAsync(ct);

            OcrWord? target = null;
            foreach (var kw in targetKeywords)
            {
                target = o.Ocr.FindFirst(kw);
                if (target is not null) break;
            }
            if (target is null) return false; // 目标都不在屏幕上

            // 目标按钮区域蓝底占比（蓝底 = 指针停留的按钮）
            var tRegion = new NormRect(
                Math.Max(0, target.X - 0.06), Math.Max(0, target.Y - target.H),
                Math.Min(1, target.W + 0.12), target.H * 3.0);
            double tBlue = FrameAnalyzer.ColorRatio(o.Frame, HsvFilter.Blue, tRegion);

            if (tBlue > 0.30)
            {
                LogInternal($"指针已在「{target.Text}」上（blue={tBlue:F2}），按 A", LogLevel.Debug);
                await Pad.Confirm();
                await Task.Delay(600, ct);
                return true;
            }

            // 找当前指针所在按钮：候选按钮中蓝底最高者
            OcrWord? focus = null;
            double focusBlue = 0;
            foreach (var kw in allButtons)
            {
                foreach (var w in o.Ocr.FindAll(kw))
                {
                    var region = new NormRect(
                        Math.Max(0, w.X - 0.06), Math.Max(0, w.Y - w.H),
                        Math.Min(1, w.W + 0.12), w.H * 3.0);
                    double blue = FrameAnalyzer.ColorRatio(o.Frame, HsvFilter.Blue, region);
                    if (blue > focusBlue) { focusBlue = blue; focus = w; }
                }
            }

            if (focus is null || focusBlue < 0.15)
            {
                // 找不到蓝色焦点：弹窗主按钮（OK/下一步/确定）默认就是焦点，直接按 A。
                // 只有第 0 轮尝试按一次 → 移动指针，之后都直接按 A 兜底，避免反复 Right 把焦点移走。
                if (move >= 1)
                {
                    LogInternal($"未定位到指针，直接按 A 确认「{target.Text}」", LogLevel.Debug);
                    await Pad.Confirm();
                    await Task.Delay(600, ct);
                    return true;
                }
                LogInternal($"未定位到指针，按 → 尝试聚焦「{target.Text}」", LogLevel.Debug);
                await Pad.Dpad(PadDir.Right);
                await Task.Delay(400, ct);
                continue;
            }

            double dx = target.CenterX - focus.CenterX;
            double dy = target.CenterY - focus.CenterY;
            LogInternal($"移动指针：「{focus.Text}」→「{target.Text}」 dx={dx:F2} dy={dy:F2}", LogLevel.Debug);

            if (Math.Abs(dx) < 0.05 && Math.Abs(dy) < 0.06)
            {
                // 已对准但蓝底不足：直接按 A 尝试
                await Pad.Confirm();
                await Task.Delay(600, ct);
                return true;
            }
            if (Math.Abs(dx) > Math.Abs(dy))
                await Pad.Dpad(dx > 0 ? PadDir.Right : PadDir.Left);
            else
                await Pad.Dpad(dy > 0 ? PadDir.Down : PadDir.Up);
            await Task.Delay(450, ct);
        }
        return false;
    }

    /// <summary>连续按 B 尝试回到顶层主页。</summary>
    public async Task BackToHomeAsync(CancellationToken ct)
    {
        LogInternal("正在回到游戏主页…", LogLevel.Info);
        for (int i = 0; i < 8; i++)
        {
            ct.ThrowIfCancellationRequested();
            var o = await ObserveAsync(ct);
            if (o.Is(GameScreen.Home)) return;
            if (o.Is(GameScreen.Disconnected) || o.Is(GameScreen.Dialog))
            {
                await HandlePopupsAsync(o, ct);
                continue;
            }
            await Pad.Back();
            await Task.Delay(900, ct);
        }
    }

    /// <summary>卡死恢复：优先找“下一步/确定/确认”按 A；否则按 B；连续失败回主页。</summary>
    public async Task RecoverAsync(Observation o, CancellationToken ct)
    {
        _unknownStreak++;

        // 优先：发现「下一步 / OK / 确定 / 确认」→ 直接按 A（弹窗主按钮默认为焦点）
        var next = o.Ocr.FindFirst("下一步") ?? o.Ocr.FindFirst("ok")
                   ?? o.Ocr.FindFirst("确定") ?? o.Ocr.FindFirst("确认") ?? o.Ocr.FindFirst("next");
        if (next is not null)
        {
            LogInternal($"未识别画面，但发现「{next.Text}」，直接按 A", LogLevel.Warn);
            await Pad.Confirm();
            await Task.Delay(700, ct);
            return;
        }

        // 直播画面已有草皮视觉判定、赛前过场在比赛流程内部处理，外层出现未知通常是
        // 加载/黑屏——按 B 可能取消匹配，先只等待；连续多轮仍未恢复才回主页
        if (_unknownStreak <= 2)
        {
            LogInternal("未识别画面，等待 2s 后重新观察（不按 B，避免误退/取消匹配）…", LogLevel.Warn);
        }
        else
        {
            _unknownStreak = 0;
            await BackToHomeAsync(ct);
        }
        await Task.Delay(2000, ct);
    }

    public void ResetUnknown() => _unknownStreak = 0;

    // ---------------- 导航 ----------------

    /// <summary>
    /// 二维卡片/磁贴导航：根据 OCR 坐标把焦点移动到目标卡片并按 A。
    /// 通过比较各候选卡片外扩区域的白色描边亮度找当前焦点。
    /// </summary>
    public async Task<bool> FocusAndConfirmAsync(string[] targetKeywords, string[] allCardKeywords, CancellationToken ct, int maxMoves = 14)
    {
        for (int move = 0; move < maxMoves; move++)
        {
            ct.ThrowIfCancellationRequested();
            var o = await ObserveAsync(ct);

            OcrWord? target = null;
            foreach (var kw in targetKeywords)
            {
                target = o.Ocr.FindFirst(kw);
                if (target is not null) break;
            }
            if (target is null)
            {
                await Pad.Dpad(PadDir.Right);
                await Task.Delay(400, ct);
                continue;
            }

            // 找当前焦点卡片：候选卡片中白色描边比例最高者
            OcrWord? focus = null;
            double focusScore = 0;
            foreach (var kw in allCardKeywords)
            {
                foreach (var w in o.Ocr.FindAll(kw))
                {
                    var region = new NormRect(
                        Math.Max(0, w.X - 0.09), Math.Max(0, w.Y - 0.30),
                        Math.Min(1, w.W + 0.18), Math.Min(1, w.H + 0.34));
                    double white = FrameAnalyzer.ColorRatio(o.Frame, HsvFilter.White, region);
                    if (white > focusScore) { focusScore = white; focus = w; }
                }
            }

            if (focus is null || focusScore < 0.015)
            {
                // 无法定位焦点：先往左上收拢
                await Pad.Dpad(PadDir.Up);
                await Task.Delay(300, ct);
                continue;
            }

            double dx = target.CenterX - focus.CenterX;
            double dy = target.CenterY - focus.CenterY;
            LogInternal($"焦点导航：当前「{focus.Text}」→ 目标「{target.Text}」 dx={dx:F2} dy={dy:F2}", LogLevel.Debug);

            if (Math.Abs(dx) < 0.06 && Math.Abs(dy) < 0.08)
            {
                await Pad.Confirm();
                return true;
            }
            if (Math.Abs(dx) > Math.Abs(dy))
                await Pad.Dpad(dx > 0 ? PadDir.Right : PadDir.Left);
            else
                await Pad.Dpad(dy > 0 ? PadDir.Down : PadDir.Up);
            await Task.Delay(450, ct);
        }
        return false;
    }

    /// <summary>半途友好的导航：如果已在 MatchHub/MyLeagueHome 就直接接上，不强制 BackToHome 回主页重导。</summary>
    public async Task EnterMyLeagueAsync(CancellationToken ct)
    {
        var cur = await ObserveAsync(ct);

        // 已经在联赛主页：什么都不用做
        if (cur.Is(GameScreen.MyLeagueHome)
            || cur.Ocr.Contains("下一场") || cur.Ocr.Contains("排名") || cur.Ocr.Contains("阵容"))
        {
            LogInternal("已在我的联赛主页，直接接上", LogLevel.Info);
            return;
        }

        // 已经在比赛菜单：跳过回主页，直接 Right×2
        if (cur.Is(GameScreen.MatchHub) || cur.Ocr.Contains("活动") && cur.Ocr.Contains("联赛"))
        {
            LogInternal("已在比赛菜单，Right×2 → 我的联赛", LogLevel.Info);
            await Pad.Dpad(PadDir.Right);
            await Task.Delay(350, ct);
            await Pad.Dpad(PadDir.Right);
            await Task.Delay(500, ct);
            await Pad.Confirm();
            await Task.Delay(4500, ct);
            var after = await ObserveAsync(ct);
            if (after.Is(GameScreen.MyLeagueHome)
                || after.Ocr.Contains("下一场") || after.Ocr.Contains("排名") || after.Ocr.Contains("阵容"))
                return;
            throw new InvalidOperationException("无法进入「我的联赛」，请确认已进入比赛菜单。");
        }

        // 其他画面才回主页重导
        await BackToHomeAsync(ct);
        await WaitForAsync(GameScreen.Home, 30, ct);

        LogInternal("导航：主页 → Down×2 → Left×2 → 「比赛」磁贴", LogLevel.Info);
        await Pad.Dpad(PadDir.Down);
        await Task.Delay(200, ct);
        await Pad.Dpad(PadDir.Down);
        await Task.Delay(300, ct);
        await Pad.Dpad(PadDir.Left);
        await Task.Delay(200, ct);
        await Pad.Dpad(PadDir.Left);
        await Task.Delay(500, ct);
        await Pad.Confirm();
        await Task.Delay(2500, ct);

        var hub = await WaitForAnyAsync(new[] { GameScreen.MatchHub, GameScreen.MyLeagueHome, GameScreen.EventHub }, 20, ct, "进入比赛菜单");
        if (hub.Is(GameScreen.MyLeagueHome)) return;

        LogInternal("导航：比赛菜单 → Right×2 → 「我的联赛」", LogLevel.Info);
        await Pad.Dpad(PadDir.Right);
        await Task.Delay(350, ct);
        await Pad.Dpad(PadDir.Right);
        await Task.Delay(500, ct);
        await Pad.Confirm();
        await Task.Delay(4500, ct);

        var res = await ObserveAsync(ct);
        if (res.Is(GameScreen.MyLeagueHome)
            || res.Ocr.Contains("下一场") || res.Ocr.Contains("排名") || res.Ocr.Contains("阵容"))
            return;

        throw new InvalidOperationException("无法进入「我的联赛」，请确认已进入比赛菜单。");
    }

    /// <summary>半途友好的导航：已在 EventHub/EventInfo/MatchHub 就直接接上，不强制回主页重导。</summary>
    public async Task EnterEventHubAsync(CancellationToken ct)
    {
        var cur = await ObserveAsync(ct);

        // 已经在活动列表/详情/挑战清单：直接接上
        if (cur.Is(GameScreen.EventHub) || cur.Is(GameScreen.EventInfo)
            || cur.Is(GameScreen.ChallengeList) || cur.Is(GameScreen.ChallengeHome))
        {
            LogInternal("已在活动列表，直接接上", LogLevel.Info);
            return;
        }

        // 已经在比赛菜单：跳过回主页，直接收拢到最左"活动"卡
        if (cur.Is(GameScreen.MatchHub) || cur.Ocr.Contains("活动"))
        {
            LogInternal("已在比赛菜单，收拢到最左「活动」卡", LogLevel.Info);
            for (int i = 0; i < 2; i++)
            {
                await Pad.Dpad(PadDir.Up);
                await Task.Delay(150, ct);
            }
            for (int i = 0; i < 8; i++)
            {
                await Pad.Dpad(PadDir.Left);
                await Task.Delay(150, ct);
            }
            await Task.Delay(400, ct);
            await Pad.Confirm();
            await Task.Delay(2500, ct);
            var landed = await ObserveAsync(ct);
            if (landed.Is(GameScreen.EventHub) || landed.Is(GameScreen.EventInfo)) return;
        }

        // 其他画面才回主页重导
        await BackToHomeAsync(ct);
        await WaitForAsync(GameScreen.Home, 30, ct);

        LogInternal("导航：主页 → Down×2 → Left×2 → 「比赛」磁贴", LogLevel.Info);
        await Pad.Dpad(PadDir.Down);
        await Task.Delay(200, ct);
        await Pad.Dpad(PadDir.Down);
        await Task.Delay(300, ct);
        await Pad.Dpad(PadDir.Left);
        await Task.Delay(200, ct);
        await Pad.Dpad(PadDir.Left);
        await Task.Delay(500, ct);
        await Pad.Confirm();
        await Task.Delay(2500, ct);

        var hub = await WaitForAnyAsync(new[] { GameScreen.MatchHub, GameScreen.EventHub }, 20, ct, "进入比赛菜单");
        if (hub.Is(GameScreen.EventHub)) return;

        LogInternal("导航：比赛菜单收拢到最左「活动」卡", LogLevel.Info);
        for (int i = 0; i < 2; i++)
        {
            await Pad.Dpad(PadDir.Up);
            await Task.Delay(150, ct);
        }
        for (int i = 0; i < 8; i++)
        {
            await Pad.Dpad(PadDir.Left);
            await Task.Delay(150, ct);
        }
        await Task.Delay(400, ct);
        await Pad.Confirm();
        await Task.Delay(2500, ct);

        var landed2 = await ObserveAsync(ct);
        if (landed2.Is(GameScreen.EventHub) || landed2.Is(GameScreen.EventInfo)
            || landed2.Is(GameScreen.Loading) || landed2.Is(GameScreen.PreMatch))
            return;

        throw new InvalidOperationException("无法进入「活动」列表。");
    }

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
        try { Capture.Dispose(); } catch { }
        try { Pad360.Dispose(); } catch { }
        GC.SuppressFinalize(this);
    }
}
