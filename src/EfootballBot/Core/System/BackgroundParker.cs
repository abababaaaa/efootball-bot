using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using EfootballBot.Core.Bot;
using EfootballBot.Core.Logging;

namespace EfootballBot.Core.System;

/// <summary>停靠目标矩形：非 null 时窗口停靠到该矩形（虚拟显示器后台），
/// null = 旧行为（移出虚拟屏幕右边界外）。</summary>
public readonly record struct ParkRect(int X, int Y, int Width, int Height);

/// <summary>
/// 后台停靠：挂机期间把游戏窗口整体移到所有显示器构成的虚拟屏幕之外。
/// 纯 user32 窗口坐标操作（与用户手动把窗口拖出屏幕在系统层面无法区分），
/// 不注入、不挂钩、不读写游戏进程——反作弊检测面（进程/内存/输入/驱动）完全没碰到。
/// 窗口不在屏幕上但未最小化：游戏照常渲染、WGC/GDI 照常捕获、XInput 手柄输入照常生效。
/// 看门狗周期校验窗口位置（有的游戏在加载/切场景时会自己把窗口搬回屏幕中央），被挪回即重新移出。
/// ★停靠移动本身会触发部分游戏自行缩窗（2026-10-20 实测：eFootball 新版被移出屏幕后立即把窗口
/// 缩到 70×39、客户区清零——渲染死、WGC 冻帧、手柄交互死，bot 对冻结画面按键=「乱点」）。
/// 实测定案：停靠后在屏外原地把尺寸恢复一次即可，游戏不会再次缩窗，输入/捕获全部正常。
/// 因此 Park 成功后与看门狗都要做尺寸校验，被缩即原地恢复。
/// 前提：游戏未提权（提权进程的窗口本程序移不动，UIPI 拒绝），失败原因按 win32 错误码区分提示。
/// </summary>
public sealed class BackgroundParker
{
    private readonly Action<string, LogLevel> _log;
    private readonly int _watchdogIntervalMs;

    private nint _hwnd;
    private RECT _original;
    private bool _hadOriginal;
    private int _parkApiError;
    private ParkRect? _target;
    private CancellationTokenSource? _cts;

    /// <summary>看门狗把窗口重新移出屏幕的次数（游戏自行搬回的次数）。</summary>
    public int Corrections { get; private set; }
    public bool IsParked { get; private set; }

    public BackgroundParker(Action<string, LogLevel> log, int watchdogIntervalMs = 2500)
    {
        _log = log;
        _watchdogIntervalMs = Math.Clamp(watchdogIntervalMs, 500, 60_000);
    }

    /// <summary>停靠点 X 坐标：虚拟屏幕右边界外 64px（纯函数便于单测）。</summary>
    internal static int ComputeParkX(int virtualScreenX, int virtualScreenWidth) =>
        virtualScreenX + virtualScreenWidth + 64;

    /// <summary>窗口是否被游戏自行缩小（纯函数便于单测）：原宽≥800 且当前宽不到原宽一半。
    /// 2026-10-20 实测：eFootball 被停靠移出屏幕后会缩到 70×39、客户区清零，渲染与手柄交互全死。</summary>
    internal static bool WindowShrunk(int originalWidth, int currentWidth) =>
        originalWidth >= 800 && currentWidth * 2 < originalWidth;

    /// <summary>在停靠点原地把窗口恢复为原始尺寸（不改变 Z 序、不抢激活）。</summary>
    private void RestoreSizeAt(int parkX)
    {
        SetWindowPos(_hwnd, nint.Zero, parkX, _original.Top,
            _original.Right - _original.Left, _original.Bottom - _original.Top,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>开始停靠并启动看门狗。重复调用会先还原再重新停靠。
    /// <paramref name="target"/> 非 null 时停靠到该矩形（虚拟显示器后台），否则移出屏幕。</summary>
    public void Start(nint hwnd, ParkRect? target = null)
    {
        Stop();
        if (hwnd == nint.Zero) return;
        _hwnd = hwnd;
        _target = target;
        _hadOriginal = GetWindowRect(hwnd, out _original);
        Corrections = 0;
        _parkApiError = 0;

        if (IsIconic(hwnd))
        {
            _log("后台停靠跳过：游戏窗口当前处于最小化状态", LogLevel.Warn);
            return;
        }
        // 全屏（最大化）窗口被系统钉在显示器上，且游戏内不允许更改显示模式/切换无边框：
        // 不做移出，跳过停靠（避免移出失败或窗口行为异常）
        if (IsZoomed(hwnd))
        {
            _hadOriginal = GetWindowRect(hwnd, out _original);
            _log("后台停靠跳过：全屏（最大化）状态下无法移出窗口，请退出全屏后再启用后台停靠", LogLevel.Warn);
            return;
        }
        if (!Park())
        {
            _log(DescribeFailure(_parkApiError), LogLevel.Warn);
            return;
        }

        IsParked = true;
        // 落盘：强制关闭/更新/崩溃等非正常退出后，下次启动自动还原。
        // 必须存移出前的原矩形（_original）——此刻窗口已在屏幕外，读当前坐标会把
        // 屏幕外坐标当原位置存下来，之后所有还原路径都等于没还原（游戏永远回不来）。
        SaveParkRecord(_hwnd, _original);
        // 停靠移动可能触发游戏自行缩窗（渲染/输入全死）：稍候片刻做一次尺寸校验，
        // 被缩就在屏外原地把尺寸恢复（实测恢复一次后游戏不再缩）
        Thread.Sleep(600);
        int parkLeft = _target?.X ?? ComputeParkX(GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_CXVIRTUALSCREEN));
        if (GetWindowRect(_hwnd, out var afterPark) &&
            WindowShrunk(_original.Right - _original.Left, afterPark.Right - afterPark.Left))
        {
            RestoreSizeAt(parkLeft);
            _log("后台停靠：游戏窗口被自行缩小（缩窗会杀死渲染与输入），已恢复尺寸", LogLevel.Warn);
        }
        _log(_target is { } t
            ? $"后台停靠已开启：游戏窗口已停靠到虚拟显示器（{t.Width}×{t.Height}，物理屏幕不可见；停止挂机后自动还原位置）"
            : "后台停靠已开启：游戏窗口已移出屏幕（主屏幕不可见，点击其他软件不会跳出；停止挂机后自动还原位置）",
            LogLevel.Success);
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => WatchAsync(_hwnd, _cts.Token));
    }

    /// <summary>停止看门狗并还原窗口原位置（幂等；未停靠过则什么都不做）。</summary>
    public void Stop()
    {
        if (_cts is not null)
        {
            try { _cts.Cancel(); } catch { }
            _cts.Dispose();
            _cts = null;
        }
        if (!IsParked) return;
        IsParked = false;
        try
        {
            // 还原挂机前原位置与尺寸；窗口已关闭时 SetWindowRect 自然失败，无需处理
            if (_hadOriginal && _hwnd != nint.Zero)
                SetWindowPos(_hwnd, nint.Zero, _original.Left, _original.Top,
                    _original.Right - _original.Left, _original.Bottom - _original.Top,
                    SWP_NOZORDER | SWP_NOACTIVATE);
            DeleteParkRecord(); // 正常还原完成，清除落盘记录
            _log($"后台停靠已还原游戏窗口原位置（看门狗校正 {Corrections} 次）", LogLevel.Info);
        }
        catch { /* 还原失败不影响停止流程 */ }
    }

    /// <summary>停靠（不改变 Z 序、不抢激活）：目标矩形非 null 时移到该矩形（含尺寸），
    /// 否则移出屏幕（保留窗口尺寸）。
    /// 失败原因经 win32 错误码归类：err=5(拒绝访问)=游戏提权而本程序未提权，UIPI 禁止移动其窗口
    /// （2026-10-15 实机：国际服游戏以管理员运行，SetWindowPos 恒 ERROR_ACCESS_DENIED，
    /// 画面/手柄不受影响，仅停靠失效）；API 成功但坐标被还原=独占全屏强制还原。</summary>
    private bool Park()
    {
        int x, y, w, h;
        if (_target is { } t)
        {
            // 虚拟屏停靠同样保持窗口原尺寸（SWP_NOSIZE）：强改窗口尺寸会让游戏画面
            // 与内部渲染分辨率不一致（1280×720 客户区被拉成 1920×1080），
            // 感知/ROI 是归一化坐标，挪位置不缩放才是安全操作
            x = t.X; y = t.Y; w = 0; h = 0;
        }
        else
        {
            x = ComputeParkX(GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_CXVIRTUALSCREEN));
            y = _original.Top; w = 0; h = 0;
        }
        _ = Marshal.GetLastWin32Error(); // 丢弃陈旧错误码，保证下面读到的是本次调用的
        if (!SetWindowPos(_hwnd, nint.Zero, x, y, w, h,
                (w == 0 ? SWP_NOSIZE : 0u) | SWP_NOZORDER | SWP_NOACTIVATE))
        {
            _parkApiError = Marshal.GetLastWin32Error();
            return false;
        }
        if (GetWindowRect(_hwnd, out var rc) && rc.Left == x && (w == 0 || rc.Top == y)) return true;
        _parkApiError = 0; // API 成功但位置被游戏强制还原 → 按独占全屏归类
        return false;
    }

    /// <summary>停靠失败原因归类（纯函数便于单测）。</summary>
    internal static string DescribeFailure(int parkApiError) =>
        parkApiError == ERROR_ACCESS_DENIED
            ? "后台停靠失败：游戏正以管理员权限运行，而本程序未提权，系统(UIPI)禁止移动提权进程的窗口。" +
              "请右键本程序选「以管理员身份运行」重启后再挂机，或到游戏/Steam 的 属性→兼容性 取消「以管理员身份运行此程序」"
            : "后台停靠失败：SetWindowPos 被拒绝或窗口位置被强制还原" +
              "（独占全屏模式下无法移出，请在游戏内改为无边框窗口模式）";

    private async Task WatchAsync(nint hwnd, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_watchdogIntervalMs, ct);
                if (!IsWindow(hwnd) || IsParked == false) return;
                if (IsIconic(hwnd)) continue; // 用户手动最小化：不与之对抗

                if (!GetWindowRect(hwnd, out var rc)) continue;
                int parkX = _target?.X ?? ComputeParkX(
                    GetSystemMetrics(SM_XVIRTUALSCREEN),
                    GetSystemMetrics(SM_CXVIRTUALSCREEN));
                int parkTop = _target?.Y ?? _original.Top;
                if (rc.Left != parkX || rc.Top != parkTop)
                {
                    // 游戏把窗口搬回了屏幕（或用户拖动）：重新停靠
                    if (SetWindowPos(hwnd, nint.Zero, parkX, parkTop,
                            0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE))
                    {
                        Corrections++;
                        _log($"看门狗：游戏窗口被挪回屏幕（{rc.Left},{rc.Top}），已重新停靠（第 {Corrections} 次）", LogLevel.Info);
                    }
                    // 移回动作可能再次触发自行缩窗，交给下面的尺寸校验（本或下轮处理）
                    if (!GetWindowRect(hwnd, out rc)) continue;
                }
                // 尺寸校验：停靠状态下游戏自行缩窗（渲染/输入全死）即原地恢复尺寸
                if (WindowShrunk(_original.Right - _original.Left, rc.Right - rc.Left))
                {
                    RestoreSizeAt(parkX);
                    Corrections++;
                    _log($"看门狗：游戏窗口被自行缩小至 {rc.Right - rc.Left}x{rc.Bottom - rc.Top}，" +
                         $"已恢复尺寸（第 {Corrections} 次）", LogLevel.Info);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { /* 看门狗异常静默：停靠失败不影响挂机主流程 */ }
    }

    private const int SM_CXFULLSCREEN = 16; // 主屏幕宽（不含任务栏）
    private const int SM_CYFULLSCREEN = 17; // 主屏幕高（不含任务栏）
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const int ERROR_ACCESS_DENIED = 5;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hWnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint hWnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    // ---------------- 停靠状态落盘与恢复（防非正常退出把游戏丢在屏幕外） ----------------

    private static string ParkRecordPath =>
        Path.Combine(AppPaths.DataDir, "parked-window.json");

    private sealed record ParkRecord(string ProcessName, nint Hwnd, int Left, int Top, int Width, int Height, string ParkedAt);

    /// <summary>停靠成功后落盘：进程名 + 移出前的原矩形。强制关闭/更新/崩溃等非正常退出后，
    /// 下次启动按记录还原（正常停止走 Stop 还原并删除记录）。</summary>
    private static void SaveParkRecord(nint hwnd, RECT original)
    {
        try
        {
            string procName = "";
            try
            {
                _ = GetWindowThreadProcessId(hwnd, out int pid);
                using var p = global::System.Diagnostics.Process.GetProcessById(pid);
                procName = p.ProcessName;
            }
            catch { }
            var rec = new ParkRecord(procName, hwnd, original.Left, original.Top,
                original.Right - original.Left, original.Bottom - original.Top,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Directory.CreateDirectory(Path.GetDirectoryName(ParkRecordPath)!);
            File.WriteAllText(ParkRecordPath, JsonSerializer.Serialize(rec, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 落盘失败只影响异常退出的自动还原 */ }
    }

    private static void DeleteParkRecord()
    {
        try { File.Delete(ParkRecordPath); } catch { }
    }

    /// <summary>
    /// 尝试把被停靠的游戏窗口还原到原位置（供非正常退出后的各恢复点调用）：
    /// ① 记录的 hwnd 仍有效直接还原；② 否则按记录的进程名找主窗口还原。
    /// 成功（或游戏已关闭无需还原）后删除记录。始终安全，可重复调用。
    /// </summary>
    /// <summary>是否存在未还原的停靠记录（供引擎检测"GDI+停靠=黑屏"场景）。</summary>
    public static bool HasParkRecord() => File.Exists(ParkRecordPath);

    /// <summary>立即还原停靠的窗口（带日志）。用于 GDI 兜底捕获在停靠状态下取到黑帧时，
    /// 放弃隐藏保挂机功能（驱动异常导致 WGC/DXGI 均不可用时唯一可行的捕获方式）。</summary>
    public static void RestoreNow(Action<string, LogLevel> log)
    {
        log("GDI 兜底捕获在停靠状态下取到黑帧（PrintWindow 无法取到屏外 D3D 内容），" +
            "自动还原窗口位置：本次放弃隐藏，优先保证挂机可用", LogLevel.Warn);
        log("提示：重启电脑修复显卡驱动后，WGC 捕获 + 后台停靠即可兼得", LogLevel.Info);
        TryRestoreFromRecord();
    }

    public static void TryRestoreFromRecord()
        => TryRestoreFromRecord(out _);

    /// <summary>带结果版：返回是否实际还原了窗口（游戏已关闭/无记录=false，message 说明原因）。</summary>
    public static bool TryRestoreFromRecord(out string message)
    {
        message = "";
        try
        {
            if (!File.Exists(ParkRecordPath))
            {
                message = "没有停靠记录";
                return false;
            }
            var rec = JsonSerializer.Deserialize<ParkRecord>(File.ReadAllText(ParkRecordPath));
            if (rec is null) { DeleteParkRecord(); message = "停靠记录损坏已清除"; return false; }

            nint hwnd = rec.Hwnd;
            bool valid = hwnd != nint.Zero && IsWindow(hwnd);
            if (!valid)
            {
                // 进程重启后 hwnd 失效：按进程名找主窗口
                try
                {
                    foreach (var p in global::System.Diagnostics.Process.GetProcessesByName(rec.ProcessName))
                    {
                        using (p)
                        {
                            if (p.MainWindowHandle != nint.Zero) { hwnd = p.MainWindowHandle; valid = true; break; }
                        }
                    }
                }
                catch { }
            }
            if (!valid)
            {
                // 游戏窗口已关闭：无需还原
                DeleteParkRecord();
                message = "游戏窗口已关闭，无需还原";
                return false;
            }

            if (IsIconic(hwnd))
                ShowWindow(hwnd, SW_RESTORE);
            SetWindowPos(hwnd, nint.Zero, rec.Left, rec.Top, rec.Width, rec.Height,
                SWP_NOZORDER | SWP_NOACTIVATE);
            DeleteParkRecord();
            message = $"已按停靠记录把游戏窗口还原到原位置（{rec.Left},{rec.Top}）";
            return true;
        }
        catch (Exception ex)
        {
            // 还原失败不抛出（各调用点均为兜底路径）
            message = $"还原停靠记录失败：{ex.Message}";
            try { DeleteParkRecord(); } catch { }
            return false;
        }
    }

    // ---------------- 窗口防卡死（手动一键拉回） ----------------

    /// <summary>两服游戏进程名（国际服 eFootball 系 + 国服 eFootballOnline）。</summary>
    private static readonly string[] GameProcessNames =
        { "eFootball", "eFootball2024", "efootballsteam", "eFootballOnline" };

    /// <summary>
    /// 窗口防卡死：游戏窗口被留在屏幕外回不来时的一键恢复。
    /// ① 有停靠记录 → 按记录还原原位置；② 无记录/还原失败 → 找到两服任一游戏窗口
    /// 居中到主屏幕（保留窗口尺寸）。无论窗口当年是怎么跑出屏幕的都能拉回来。
    /// </summary>
    public static void RescueGameWindow(Action<string, LogLevel> log)
    {
        if (HasParkRecord())
        {
            log("检测到停靠记录，正在把游戏窗口还原到原位置…", LogLevel.Info);
            if (TryRestoreFromRecord(out string msg))
            {
                log(msg, LogLevel.Success);
                return;
            }
            log(msg + "，改为把游戏窗口居中到主屏幕", LogLevel.Info);
        }

        GameWindowInfo? win = null;
        foreach (var name in GameProcessNames)
        {
            win = GameWindow.FindFirst(name);
            if (win is not null) break;
        }
        if (win is null)
        {
            log("窗口防卡死：未发现正在运行的游戏窗口，无需处理", LogLevel.Warn);
            return;
        }

        try
        {
            if (IsIconic(win.Handle))
                ShowWindow(win.Handle, SW_RESTORE);
            if (!GetWindowRect(win.Handle, out var rc))
            {
                log("窗口防卡死：读取游戏窗口尺寸失败", LogLevel.Error);
                return;
            }
            int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
            int x = Math.Max(0, (GetSystemMetrics(SM_CXFULLSCREEN) - w) / 2);
            int y = Math.Max(0, (GetSystemMetrics(SM_CYFULLSCREEN) - h) / 2);
            if (SetWindowPos(win.Handle, nint.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE))
                log($"窗口防卡死：已把游戏窗口移回主屏幕中央（{x},{y}）", LogLevel.Success);
            else
                log("窗口防卡死：移动游戏窗口被系统拒绝（游戏若以管理员运行，请以管理员身份运行本程序后再试）", LogLevel.Error);
        }
        catch (Exception ex)
        {
            log($"窗口防卡死失败：{ex.Message}", LogLevel.Error);
        }
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out int lpdwProcessId);
}
