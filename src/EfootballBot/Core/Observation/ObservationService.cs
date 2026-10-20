using EfootballBot.Core.Bot;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Config;
using EfootballBot.Core.System;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Observation;

/// <summary>
/// 观察服务：封装画面捕获 + OCR 识别 + 捕获节流，不含画面分类。
/// 持有 ICaptureProvider（DXGI Desktop Duplication 或 WGC），支持运行时切换捕获后端。
/// 分类逻辑（ProbeEngine.Classify）仍由 BotEngine 负责。
/// </summary>
public sealed class ObservationService : IDisposable
{
    private readonly object _gate = new();
    private ICaptureProvider _provider;
    private readonly OcrRecognizer _ocr;
    private readonly AppConfig _cfg;

    private FrameData? _frame;
    private FrameData? _lastOcrFrame;
    private OcrResult _lastOcr = OcrResult.Empty;
    private ulong? _lastOcrHash;
    private long _lastOcrTick;
    private readonly List<DateTime> _rebuildTimes = new();

    /// <summary>帧陈旧阈值：节流 2000ms/帧，10s 无新帧判定捕获中断。</summary>
    private const int StaleFrameMs = 10_000;
    private DateTime _lastCaptureRestart = DateTime.MinValue;
    private DateTime _lastFreshFrameAt = DateTime.MinValue;   // 最近一次拿到新鲜画面（快速失败判据）
    private DateTime _firstFreshAfterRestartAt = DateTime.MinValue; // 本轮重建后首个新鲜画面（会话寿命判据）
    private int _fastStallCount; // 重建后会话寿命 <8s 的连续次数：重建出 1-2 帧即死的系统性不兼容（矿卡案例）
    private int _zeroFrameRestarts; // 从未出帧会话的重建次数（无帧快路径预算，见 RestartCaptureAsync）
    private bool _backendSavedThisRun; // 本次运行已回写过本机捕获后端记忆（状态升级时复位再写）

    /// <summary>当前游戏窗口（捕获自愈重建时使用）。</summary>
    public GameWindowInfo? Window { get; set; }

    /// <summary>日志回调（由 BotEngine 桥接）。</summary>
    public Action<string, LogLevel>? Log { get; set; }

    public event EventHandler? FrameUpdated;

    public FrameData? LatestFrame => _frame;
    public OcrResult LatestOcr => _lastOcr;
    public long LastOcrTick => _lastOcrTick;
    public ICaptureProvider Provider => _provider;

    public ObservationService(ICaptureProvider provider, OcrRecognizer ocr, AppConfig cfg)
    {
        _provider = provider;
        _ocr = ocr;
        _cfg = cfg;
        _provider.FrameUpdated += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var f = _provider.TryGetLatest();
        if (f is not null)
        {
            _frame = f;
            FrameUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>切换捕获提供者（DXGI / WGC）。会停止旧提供者并开始新提供者。</summary>
    public void SwitchCapture(ICaptureProvider newProvider)
    {
        lock (_gate)
        {
            _provider.FrameUpdated -= OnFrame;
            try { _provider.Stop(); } catch { }
            try { _provider.Dispose(); } catch { }
            _provider = newProvider;
            _provider.FrameUpdated += OnFrame;
        }
    }

    /// <summary>引擎切换捕获后端（如熔断后降级 GDI）后调用：清空熔断/快速失败预算，
    /// 让新后端从零计数，不被旧后端的失败记录连坐。</summary>
    public void ResetCaptureHealth()
    {
        lock (_rebuildTimes) _rebuildTimes.Clear();
        _fastStallCount = 0;
        _zeroFrameRestarts = 0;
        _backendSavedThisRun = false;
        _lastCaptureRestart = DateTime.MinValue;
        _lastFreshFrameAt = DateTime.MinValue;
        _firstFreshAfterRestartAt = DateTime.MinValue;
        _frame = null;
        _lastOcrFrame = null;
        _lastOcrHash = null;
    }

    /// <summary>
    /// 仅切换帧事件订阅到另一个已管理的提供者，不停止/释放任何一方。
    /// 用于 DXGI↔常驻 WGC 实例之间的安全切换（生命周期由 BotEngine 管理）。
    /// </summary>
    public void AttachProvider(ICaptureProvider provider)
    {
        lock (_gate)
        {
            _provider.FrameUpdated -= OnFrame;
            _provider = provider;
            _provider.FrameUpdated += OnFrame;
            _frame = null;
        }
    }

    // ---------------- 捕获自愈 ----------------

    /// <summary>
    /// 等待一帧新鲜画面。捕获中断（全屏切换/最小化/D3D 设备丢失）时
    /// 自动整套重建捕获。只有游戏窗口真正消失才抛异常终止场景。
    /// </summary>
    public async Task<FrameData> WaitForFreshFrameAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var f = _frame;
            if (f is not null && (DateTime.Now - _provider.LastFrameAt).TotalMilliseconds < StaleFrameMs)
            {
                MarkFreshFrame();
                return f;
            }

            // 宽限 2s：启动首帧 / 切屏过渡 / 重建后第一帧
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(100, ct);
                f = _frame;
                if (f is not null && (DateTime.Now - _provider.LastFrameAt).TotalMilliseconds < StaleFrameMs)
                {
                    MarkFreshFrame();
                    return f;
                }
            }

            // 窗口仍在（独占全屏/最小化也能查到）→ 重建捕获；窗口没了才是真终止
            if (Window is null || GameWindow.FindFirst(Window.ProcessName) is null)
                throw new InvalidOperationException("游戏窗口已关闭，停止挂机。");

            await RestartCaptureAsync(ct, DescribeFrameGap());
        }
    }

    /// <summary>记录"刚拿到新鲜画面"。连续健康 ≥2 分钟自动清空熔断预算。
    /// 首次出帧时把当前后端记入本机记忆（capture-backend.json），下次启动直连。</summary>
    private void MarkFreshFrame()
    {
        var now = DateTime.Now;
        if (_lastFreshFrameAt != DateTime.MinValue && (now - _lastFreshFrameAt).TotalSeconds >= 120)
        {
            lock (_rebuildTimes) _rebuildTimes.Clear();
            _fastStallCount = 0;
        }
        if (_firstFreshAfterRestartAt == DateTime.MinValue)
            _firstFreshAfterRestartAt = now;
        _lastFreshFrameAt = now;
        if (!_backendSavedThisRun)
        {
            _backendSavedThisRun = true;
            RememberWorkingBackend();
        }
    }

    /// <summary>把"当前后端出帧正常"写入本机记忆。GDI/WARP 写入是启动提速的关键
    /// （下次不再撞探测链）；WGC 写入用于机器修复后自愈覆盖旧判定。
    /// DXGI 为用户显式选择的模式，不参与记忆（记忆只在 Wgc 模式下消费）。</summary>
    private void RememberWorkingBackend()
    {
        try
        {
            switch (_provider)
            {
                case GdiWindowCapture:
                    CaptureBackendMemory.Save(CaptureBackendKind.Gdi, "GDI 捕获出帧正常");
                    break;
                case WindowCapture wc:
                    CaptureBackendMemory.Save(
                        wc.ForceWarpDevice ? CaptureBackendKind.Warp : CaptureBackendKind.Wgc,
                        wc.ForceWarpDevice ? "WARP 兼容模式出帧正常" : "硬件 WGC 出帧正常");
                    break;
            }
        }
        catch { /* 记忆失败不影响挂机 */ }
    }

    /// <summary>清空全部熔断预算（熔断抛出前调用，GDI 降级后从零计数）。</summary>
    private void ResetBreakerCounters()
    {
        lock (_rebuildTimes) _rebuildTimes.Clear();
        _fastStallCount = 0;
        _zeroFrameRestarts = 0;
    }

    /// <summary>断帧归因：从未出过一帧 vs 最后一帧距今多久（重建日志可读性，矿机排障用）。</summary>
    private string DescribeFrameGap() =>
        _lastFreshFrameAt == DateTime.MinValue
            ? "捕获已建立但迟迟没有产出画面"
            : $"最后画面距今 {(DateTime.Now - _lastFreshFrameAt).TotalSeconds:F0}s";

    /// <summary>整套停止并重建捕获（同窗口、按当前客户区尺寸）。两条熔断路径：
    /// ① 从未出帧（无帧快路径）：重建要么立刻出帧、要么永远不出，不等 10s 间隔、
    ///    第 1 次重建直接切 WARP，第 3 次仍无帧即熔断（全程约 8s，正常机器首帧 <1s 不受影响）；
    /// ② 出过帧又断（DWM 崩溃类，重建本身会冲击 DWM）：保持保守节奏——两次重建至少间隔 10s，
    ///    熔断条件（满足其一）：3 分钟内重建 ≥3 次；或连续 3 次"快速失败"——重建出的会话
    ///    寿命不足 8s 又断（2026-10-13 矿卡用户：重建后出 1-2 帧即死，等满 3 分钟毫无意义，
    ///    快速熔断让引擎尽早降级 GDI）。
    /// 熔断抛出 CaptureUnrecoverableException：捕获链路系统性异常（典型：WGC 会话反复
    /// 触发 DWM 崩溃黑屏），继续重建只会拖垮整个系统。</summary>
    private async Task RestartCaptureAsync(CancellationToken ct, string reason)
    {
        bool neverHadFrame = _lastFreshFrameAt == DateTime.MinValue;

        if (!neverHadFrame && (DateTime.Now - _lastCaptureRestart).TotalSeconds < 10)
        {
            await Task.Delay(1000, ct);
            return;
        }
        _lastCaptureRestart = DateTime.Now;

        if (neverHadFrame)
        {
            // 无帧快路径：会话建立后始终无帧（虚拟显示器/远程会话/无输出显卡机器上硬件 WGC
            // 的系统性失效形态，跨适配器帧表面拷贝不出图）。旧节奏（10s 间隔 ×3 次重建才切
            // WARP、再 ×3 次才熔断）让这类机器每次启动干等约 1 分钟才轮到可用的后端。
            _zeroFrameRestarts++;
            if (_zeroFrameRestarts >= 3)
            {
                ResetBreakerCounters();
                throw new CaptureUnrecoverableException(
                    "画面捕获会话多次建立但始终无帧，已停止重建。" +
                    "常见原因：捕获绑定了无显示输出的显卡、虚拟显示器或远程会话环境。");
            }
            if (_provider is WindowCapture wc && !wc.ForceWarpDevice)
            {
                // 熔断前唯一的机会：强制切 WARP 软渲染设备重建——表面为系统内存，
                // CPU 拷贝必达（v1.x 在这类机器上的实际工作形态）。
                wc.ForceWarpDevice = true;
                _backendSavedThisRun = false; // 兼容模式验证通过后回写 WARP 记忆
                LogInternal("捕获会话建立后始终无帧：直接改用 WARP 软渲染设备重建捕获" +
                    "（虚拟显示器/远程环境兼容模式；再无帧将降级 GDI 捕获）", LogLevel.Warn);
            }
            await Task.Delay(300, ct); // 让旧会话完全释放，立即重建
        }
        else
        {
            // 先归因上一轮会话（寿命是否不足 8s），再为新会话重置首帧时间戳
            bool fastStall = _lastFreshFrameAt != DateTime.MinValue && (
                _firstFreshAfterRestartAt == DateTime.MinValue ||   // 上一轮重建后至今一帧未见
                (_lastFreshFrameAt - _firstFreshAfterRestartAt).TotalSeconds < 8);
            if (fastStall) _fastStallCount++;
            _firstFreshAfterRestartAt = DateTime.MinValue;

            lock (_rebuildTimes)
            {
                _rebuildTimes.RemoveAll(t => (DateTime.Now - t).TotalMinutes >= 3);
                _rebuildTimes.Add(DateTime.Now);
                if (_rebuildTimes.Count >= 3 || _fastStallCount >= 3)
                {
                    // 熔断前最后一次机会：硬件设备 WGC 在虚拟显示器/远程会话机器上跨适配器表面
                    // 失效（GPU 缩放 E_INVALIDARG、CPU 拷贝无产出），重建多少次都无帧；
                    // 强制切 WARP 软渲染设备重建（v1.x 在这些机器上的实际工作形态），仍无帧才熔断降级 GDI。
                    if (_provider is WindowCapture wc && !wc.ForceWarpDevice)
                    {
                        wc.ForceWarpDevice = true;
                        _backendSavedThisRun = false;
                        _rebuildTimes.Clear();
                        _fastStallCount = 0;
                        _firstFreshAfterRestartAt = DateTime.MinValue;
                        LogInternal("捕获反复无帧：自动改用 WARP 软渲染设备重建捕获" +
                            "（虚拟显示器/远程环境兼容模式；仍无帧将降级 GDI 捕获）", LogLevel.Warn);
                    }
                    else
                    {
                        _rebuildTimes.Clear();
                        _fastStallCount = 0;
                        throw new CaptureUnrecoverableException(
                            "画面捕获短时间内反复中断，已停止重建，避免持续崩溃拖垮系统（黑屏/卡死）。" +
                            "常见原因：DWM 或显卡驱动异常（事件查看器可查 dwm.exe 崩溃记录）、" +
                            "捕获绑定了无显示输出的显卡或虚拟显示环境。");
                    }
                }
            }
        }

        LogInternal($"画面捕获中断（{reason}），重建捕获会话…", LogLevel.Warn);

        _frame = null;
        _lastOcrFrame = null;
        try { _provider.Stop(); } catch { } // 会话已失效时 Stop 可能抛异常，兜住走重建
        await Task.Delay(500, ct);

        var (w, h) = GameWindow.GetClientSize(Window!.Handle);
        if (w <= 0 || h <= 0) (w, h) = (1280, 720);
        try
        {
            await Task.Run(() => _provider.Start(Window.Handle, w, h), ct);
            LogInternal($"捕获会话已重建（{w}×{h}），等待画面恢复…", LogLevel.Info);
        }
        catch (Exception ex)
        {
            LogInternal($"捕获重建失败：{ex.Message}；稍后自动重试", LogLevel.Warn);
        }
    }

    // ---------------- OCR ----------------

    /// <summary>
    /// 获取一帧新鲜画面并执行 OCR。若距上次 OCR 不足 ocrIntervalMs 且有缓存，则返回缓存结果。
    /// 不含画面分类——由 BotEngine 调用 ProbeEngine 完成。
    /// </summary>
    public async Task<(FrameData Frame, OcrResult Ocr)> ObserveAsync(CancellationToken ct, int ocrIntervalMs)
    {
        var f = await WaitForFreshFrameAsync(ct);

        long now = Environment.TickCount64;

        if (now - _lastOcrTick < ocrIntervalMs && _lastOcrFrame is not null)
            return (_lastOcrFrame, _lastOcr);

        // 静帧去重：画面与上次 OCR 帧逐字节相同（等待页/静止菜单的常态）→ 识别结果必然
        // 相同，跳过全帧 OCR——PP-OCR CPU 全帧一次几百 ms，是观察循环最大单项 CPU。
        // 命中时返回【新帧】+ 缓存识别，YOLO 仍按新帧节奏跑，不被 OCR 绑死。
        if (_lastOcrHash is { } lastHash && HashBgra(f.Bgra) == lastHash)
        {
            _lastOcrTick = now; // 视同一次 OCR 周期，维持节流节奏
            return (f, _lastOcr);
        }

        await Task.Yield();
        var ocr = await _ocr.RecognizeAsync(f, ct);
        _lastOcrFrame = f;
        _lastOcr = ocr;
        _lastOcrHash = HashBgra(f.Bgra);
        _lastOcrTick = now;
        return (f, ocr);
    }

    /// <summary>帧内容指纹（FNV-1a 64，全字节）。仅用于同帧去重：极小概率的碰撞只是
    /// 多跑/少跑一次 OCR，不同帧误判同帧的概率 ~2^-64，无正确性风险。</summary>
    private static ulong HashBgra(byte[] bgra)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        ulong hash = offset;
        foreach (byte b in bgra)
        {
            hash ^= b;
            hash *= prime;
        }
        return hash;
    }

    // ---------------- 捕获节流 ----------------

    /// <summary>
    /// 按场景状态调整捕获帧间隔（背压节流，降低全屏 DWM 负载）。
    /// 传 0 恢复全速（33ms 跳帧）。
    /// </summary>
    public void ApplyCaptureThrottle(int minFrameIntervalMs)
        => _provider.SetMinFrameInterval(minFrameIntervalMs <= 0 ? 33 : minFrameIntervalMs);

    private void LogInternal(string msg, LogLevel lv) => Log?.Invoke(msg, lv);

    public void Dispose()
    {
        _provider.FrameUpdated -= OnFrame;
        try { _provider.Dispose(); } catch { }
        GC.SuppressFinalize(this);
    }
}
