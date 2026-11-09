using EfootballBot.Core.Capture;
using EfootballBot.Core.Config;

namespace EfootballBot.Core.Vision.Sidecar;

/// <summary>
/// 用 Rust sidecar 取代进程内捕获的 <see cref="ICaptureProvider"/> 实现。
/// 数据流：vision-svc（DXGI 复制 → 客户区裁剪 → 共享内存）→ 本类轮询帧环 → <see cref="FrameData"/>。
/// 与 <c>WindowCapture</c>/<c>DxgiDesktopCaptureVortice</c> 接口等价，可直接替换。
/// </summary>
public sealed class SidecarCaptureProvider : ICaptureProvider
{
    private readonly VisionSidecarConfig _cfg;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private FrameData? _latest;
    private volatile int _minIntervalMs;
    private long _lastPublishTick;
    private nint _hwnd;
    private DateTime _lastFrameAt = DateTime.MinValue;

    public SidecarCaptureProvider(VisionSidecarConfig cfg) => _cfg = cfg;

    public event EventHandler? FrameUpdated;
    /// <summary>sidecar 日志（Warn/Error 会转成运行日志）。</summary>
    public event Action<string, string>? SidecarLog;

    public VisionSidecarClient? Client { get; private set; }
    public bool IsRunning => _loop is { IsCompleted: false };
    public nint Hwnd => _hwnd;
    public DateTime LastFrameAt => _lastFrameAt;
    public bool HasFrame => _latest is not null;

    /// <summary>sidecar 是否可用（exe 存在）。GUI 可据此灰掉开关。</summary>
    public static bool IsAvailable(VisionSidecarConfig? cfg) =>
        VisionSidecarLocator.Locate(cfg?.ExePath) is not null;

    public void Start(nint hwnd, int clientW, int clientH)
    {
        if (IsRunning) return;
        _hwnd = hwnd;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(() => RunAsync(hwnd, clientW, clientH, ct), ct);
    }

    private async Task RunAsync(nint hwnd, int w, int h, CancellationToken ct)
    {
        VisionSidecarClient? client = null;
        try
        {
            client = await VisionSidecarClient.StartAsync(_cfg, hwnd, w, h, ct).ConfigureAwait(false);
            Client = client;
            VisionSidecarRuntime.RegisterCapture(this);
            client.Notification += OnNotification;

            var start = await client.SendAsync(
                new VisionMsg
                {
                    Cmd = "start",
                    Hwnd = hwnd,
                    Fps = (uint)Math.Clamp(_cfg.Fps, 1, 120),
                    Backend = _cfg.Backend,
                },
                5000, ct).ConfigureAwait(false);
            if (start.Ev == "error")
            {
                SidecarLog?.Invoke("Error", $"sidecar start 失败：{start.Text}");
                return;
            }

            SidecarLog?.Invoke("Info", $"sidecar 已接管捕获（PID {client.Pid} v{client.Version}，帧环 {client.Ring.Name}）");

            while (!ct.IsCancellationRequested)
            {
                if (client.Ring.TryReadLatest(out var frame))
                {
                    Publish(frame);
                }
                await Task.Delay(8, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SidecarLog?.Invoke("Error", $"sidecar 捕获失败：{ex.Message}");
        }
        finally
        {
            VisionSidecarRuntime.UnregisterCapture(this);
            if (client is not null)
            {
                client.Notification -= OnNotification;
                try { await client.SendAsync(VisionMsg.CmdOf("stop"), 1000).ConfigureAwait(false); } catch { }
                await client.DisposeAsync().ConfigureAwait(false);
            }
            Client = null;
        }
    }

    private void OnNotification(VisionMsg msg)
    {
        switch (msg.Ev)
        {
            case "log":
                SidecarLog?.Invoke(msg.Level ?? "Info", msg.Text ?? "");
                break;
            case "error":
                SidecarLog?.Invoke("Error", msg.Text ?? "sidecar 报错");
                break;
            case "frame":
                // 帧通知只用于统计；实际像素从帧环读（事件里不带图像）。
                // backend 只在第一次出现时记一笔，方便确认 auto 是否切到了 WGC。
                if (!_backendLogged && msg.Backend is { Length: > 0 } b)
                {
                    _backendLogged = true;
                    SidecarLog?.Invoke("Info", $"sidecar 捕获后端 = {b}");
                }
                break;
        }
    }

    private bool _blackWarned;
    private bool _backendLogged;

    private void Publish(FrameData frame)
    {
        // Desktop Duplication 在虚拟显示器/远程会话机器上会稳定返回全黑帧（本仓库
        // BackgroundParker/虚拟显示器那几份 spec 里早有记录）。这里只提醒一次，
        // 上层 BotEngine 的黑帧校验会据此回退到 WGC。
        if (!_blackWarned && IsMostlyBlack(frame))
        {
            _blackWarned = true;
            SidecarLog?.Invoke("Warn", "sidecar 捕获到的画面接近全黑（桌面复制在黑屏/虚拟显示器/远程会话下会这样），将回退 WGC");
        }

        _latest = frame;
        var now = Environment.TickCount64;
        var min = _minIntervalMs;
        if (min > 0 && now - _lastPublishTick < min) return;
        _lastPublishTick = now;
        _lastFrameAt = DateTime.Now;
        FrameUpdated?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsMostlyBlack(FrameData frame)
    {
        // 采样 1/64 像素的亮度，和 BotEngine 的黑帧阈值同口径（<8 视为黑）
        var bgra = frame.Bgra;
        var step = 4 * 64;
        long sum = 0;
        var n = 0;
        for (var i = 0; i + 3 < bgra.Length; i += step)
        {
            sum += bgra[i] + bgra[i + 1] + bgra[i + 2];
            n++;
        }
        return n == 0 || sum / (n * 3) < 8;
    }

    public FrameData? TryGetLatest() => _latest;

    /// <summary>这一帧是否就是本侧车刚产出的那一帧（供 OCR 决定要不要走上传拷贝）。</summary>
    public bool IsCurrentFrame(FrameData frame)
    {
        var latest = _latest;
        return latest is not null
            && frame.TimestampMs == latest.TimestampMs
            && frame.Width == latest.Width
            && frame.Height == latest.Height;
    }

    public void SetMinFrameInterval(int ms) => _minIntervalMs = Math.Max(0, ms);

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _loop?.Wait(3000); } catch { }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    public void Dispose() => Stop();
}
