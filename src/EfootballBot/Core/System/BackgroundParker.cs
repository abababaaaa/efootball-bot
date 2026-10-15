using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using EfootballBot.Core.Bot;
using EfootballBot.Core.Logging;

namespace EfootballBot.Core.System;

/// <summary>
/// 后台停靠：挂机期间把游戏窗口整体移到所有显示器构成的虚拟屏幕之外。
/// 纯 user32 窗口坐标操作（与用户手动把窗口拖出屏幕在系统层面无法区分），
/// 不注入、不挂钩、不读写游戏进程——反作弊检测面（进程/内存/输入/驱动）完全没碰到。
/// 窗口不在屏幕上但未最小化：游戏照常渲染、WGC/GDI 照常捕获、XInput 手柄输入照常生效。
/// 看门狗周期校验窗口位置（有的游戏在加载/切场景时会自己把窗口搬回屏幕中央），被挪回即重新移出。
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

    /// <summary>开始停靠并启动看门狗。重复调用会先还原再重新停靠。</summary>
    public void Start(nint hwnd)
    {
        Stop();
        if (hwnd == nint.Zero) return;
        _hwnd = hwnd;
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
        SaveParkRecord(_hwnd); // 落盘：强制关闭/更新/崩溃等非正常退出后，下次启动自动还原
        _log("后台停靠已开启：游戏窗口已移出屏幕（主屏幕不可见，点击其他软件不会跳出；停止挂机后自动还原位置）", LogLevel.Success);
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

    /// <summary>移出屏幕（保留窗口尺寸，不改变 Z 序、不抢激活）。
    /// 失败原因经 win32 错误码归类：err=5(拒绝访问)=游戏提权而本程序未提权，UIPI 禁止移动其窗口
    /// （2026-10-15 实机：国际服游戏以管理员运行，SetWindowPos 恒 ERROR_ACCESS_DENIED，
    /// 画面/手柄不受影响，仅停靠失效）；API 成功但坐标被还原=独占全屏强制还原。</summary>
    private bool Park()
    {
        int x = ComputeParkX(
            GetSystemMetrics(SM_XVIRTUALSCREEN),
            GetSystemMetrics(SM_CXVIRTUALSCREEN));
        _ = Marshal.GetLastWin32Error(); // 丢弃陈旧错误码，保证下面读到的是本次调用的
        if (!SetWindowPos(_hwnd, nint.Zero, x, _original.Top,
                0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE))
        {
            _parkApiError = Marshal.GetLastWin32Error();
            return false;
        }
        if (GetWindowRect(_hwnd, out var rc) && rc.Left == x) return true;
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
                int parkX = ComputeParkX(
                    GetSystemMetrics(SM_XVIRTUALSCREEN),
                    GetSystemMetrics(SM_CXVIRTUALSCREEN));
                if (rc.Left == parkX && rc.Top == _original.Top) continue;

                // 游戏把窗口搬回了屏幕（或用户拖动）：重新移出
                if (SetWindowPos(hwnd, nint.Zero, parkX, _original.Top,
                        0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE))
                {
                    Corrections++;
                    _log($"看门狗：游戏窗口被挪回屏幕（{rc.Left},{rc.Top}），已重新移出（第 {Corrections} 次）", LogLevel.Info);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { /* 看门狗异常静默：停靠失败不影响挂机主流程 */ }
    }

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

    /// <summary>停靠成功后落盘：进程名 + 原矩形。强制关闭/更新/崩溃等非正常退出后，
    /// 下次启动按记录还原（正常停止走 Stop 还原并删除记录）。</summary>
    private static void SaveParkRecord(nint hwnd)
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
            GetWindowRect(hwnd, out var rc);
            var rec = new ParkRecord(procName, hwnd, rc.Left, rc.Top,
                rc.Right - rc.Left, rc.Bottom - rc.Top, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
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
    {
        try
        {
            if (!File.Exists(ParkRecordPath)) return;
            var rec = JsonSerializer.Deserialize<ParkRecord>(File.ReadAllText(ParkRecordPath));
            if (rec is null) { DeleteParkRecord(); return; }

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
                return;
            }

            if (IsIconic(hwnd))
                ShowWindow(hwnd, SW_RESTORE);
            SetWindowPos(hwnd, nint.Zero, rec.Left, rec.Top, rec.Width, rec.Height,
                SWP_NOZORDER | SWP_NOACTIVATE);
            DeleteParkRecord();
        }
        catch
        {
            // 还原失败不抛出（各调用点均为兜底路径）
            try { DeleteParkRecord(); } catch { }
        }
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out int lpdwProcessId);
}
