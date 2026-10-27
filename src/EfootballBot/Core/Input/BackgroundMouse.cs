using System.Runtime.InteropServices;

namespace EfootballBot.Core.Input;

/// <summary>
/// 面向窗口句柄的后台鼠标模拟（不移动全局光标、不抢前台）。
/// 通过 PostMessage 投递 WM_MOUSEMOVE / WM_LBUTTONDOWN / WM_LBUTTONUP，坐标为客户区像素。
/// 适用于会处理窗口鼠标消息的游戏（如国服 eFootballOnline 窗口化运行时）。
/// 点击前按拟人轨迹多段移动（二次贝塞尔 + 段间对数正态延迟）。
/// </summary>
public sealed class BackgroundMouse
{
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_RBUTTONUP = 0x0205;

    private const int MK_LBUTTON = 0x0001;
    private const int MK_RBUTTON = 0x0002;

    private readonly HumanPacer _pacer;
    private readonly Random _rnd = new();
    /// <summary>上次落点（轨迹从此接续，模拟真实光标连续移动；null=首次点击单步到位）。</summary>
    private int? _lastX;
    private int? _lastY;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public BackgroundMouse(HumanPacer? pacer = null)
    {
        _pacer = pacer ?? new HumanPacer();
    }

    /// <summary>向指定窗口客户区坐标投递一次左键单击（拟人移动→按下→抬起）。</summary>
    public async Task ClickAsync(IntPtr hWnd, int x, int y, int downHoldMs = 60, CancellationToken ct = default)
    {
        await MoveToAsync(hWnd, x, y, ct);
        PostMessage(hWnd, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, LParam(x, y));
        await Task.Delay(downHoldMs, ct);
        PostMessage(hWnd, WM_LBUTTONUP, IntPtr.Zero, LParam(x, y));
    }

    /// <summary>归一化坐标（0~1）单击，需传入客户区尺寸。</summary>
    public Task ClickNormalizedAsync(IntPtr hWnd, double nx, double ny, int clientW, int clientH,
        int downHoldMs = 60, CancellationToken ct = default)
    {
        int x = Math.Clamp((int)Math.Round(nx * clientW), 0, Math.Max(0, clientW - 1));
        int y = Math.Clamp((int)Math.Round(ny * clientH), 0, Math.Max(0, clientH - 1));
        return ClickAsync(hWnd, x, y, downHoldMs, ct);
    }

    /// <summary>仅移动（悬停），用于触发高亮/焦点。同步单步（悬停无须轨迹）。</summary>
    public void Move(IntPtr hWnd, int x, int y)
    {
        _lastX = x;
        _lastY = y;
        PostMessage(hWnd, WM_MOUSEMOVE, IntPtr.Zero, LParam(x, y));
    }

    /// <summary>右键单击（同样先走拟人轨迹）。</summary>
    public async Task RightClickAsync(IntPtr hWnd, int x, int y, int downHoldMs = 60, CancellationToken ct = default)
    {
        await MoveToAsync(hWnd, x, y, ct);
        PostMessage(hWnd, WM_RBUTTONDOWN, (IntPtr)MK_RBUTTON, LParam(x, y));
        await Task.Delay(downHoldMs, ct);
        PostMessage(hWnd, WM_RBUTTONUP, IntPtr.Zero, LParam(x, y));
    }

    /// <summary>同步 SendMessage 版本（等待窗口处理完返回），个别窗口更可靠。保持单步。</summary>
    public void ClickSend(IntPtr hWnd, int x, int y)
    {
        _lastX = x;
        _lastY = y;
        SendMessage(hWnd, WM_MOUSEMOVE, IntPtr.Zero, LParam(x, y));
        SendMessage(hWnd, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, LParam(x, y));
        SendMessage(hWnd, WM_LBUTTONUP, IntPtr.Zero, LParam(x, y));
    }

    /// <summary>拟人移动到目标后短暂停顿；首次点击（无上次落点）单步到位。</summary>
    private async Task MoveToAsync(IntPtr hWnd, int x, int y, CancellationToken ct)
    {
        int? x0 = _lastX, y0 = _lastY;
        _lastX = x;
        _lastY = y;

        if (x0 is null || y0 is null)
        {
            PostMessage(hWnd, WM_MOUSEMOVE, IntPtr.Zero, LParam(x, y));
            await Task.Delay(30, ct);
            return;
        }

        var path = BuildTrajectory(x0.Value, y0.Value, x, y, _rnd);
        foreach (var (px, py) in path)
        {
            PostMessage(hWnd, WM_MOUSEMOVE, IntPtr.Zero, LParam(px, py));
            await Task.Delay(_pacer.Sample(14, 6, 60), ct);
        }
        // 落定微停：真人点击前光标会先稳住一瞬
        await Task.Delay(_pacer.Sample(20, 8, 80), ct);
    }

    /// <summary>生成拟人移动路径：二次贝塞尔（控制点沿中垂线随机偏移 ±35% 距离）+ 末端微抖，
    /// 段数随距离 3~8 段伸缩。纯函数可单测；距离过近返回单点直落。</summary>
    internal static List<(int X, int Y)> BuildTrajectory(int x0, int y0, int x1, int y1, Random rnd)
    {
        double dist = Math.Sqrt((double)(x1 - x0) * (x1 - x0) + (double)(y1 - y0) * (y1 - y0));
        if (dist < 4)
            return new List<(int, int)> { (x1, y1) };

        int segs = (int)Math.Clamp(Math.Ceiling(dist / 140.0), 3, 8);
        double len = Math.Max(1.0, dist);
        double perpX = -(y1 - y0) / len, perpY = (x1 - x0) / len;
        double bow = (rnd.NextDouble() * 2 - 1) * 0.35 * dist;
        double cx = (x0 + x1) / 2.0 + perpX * bow;
        double cy = (y0 + y1) / 2.0 + perpY * bow;

        var pts = new List<(int X, int Y)>(segs);
        for (int i = 1; i <= segs; i++)
        {
            double t = (double)i / segs;
            double it = 1 - t;
            double x = it * it * x0 + 2 * it * t * cx + t * t * x1;
            double y = it * it * y0 + 2 * it * t * cy + t * t * y1;
            // 途经点加 ±2px 手抖（末端不抖，保证点得准）
            double jitter = i == segs ? 0 : (rnd.NextDouble() * 2 - 1) * 2.0;
            pts.Add(((int)Math.Round(x + jitter), (int)Math.Round(y - jitter)));
        }
        return pts;
    }

    private static IntPtr LParam(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));
}
