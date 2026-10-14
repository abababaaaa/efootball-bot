using System.Runtime.InteropServices;

namespace EfootballBot.Core.System;

/// <summary>
/// 进程提权（管理员）状态检测。游戏以管理员运行而本程序未提权时，UIPI（用户界面特权隔离）
/// 会拦截跨完整性级别的画面捕获与窗口消息——表现为预览黑屏/看不到画面
/// （2026-10-11 用户实测：游戏提权时本程序"预览看不到"，管理员启动本程序后恢复正常）。
/// </summary>
public static class Elevation
{
    private const int TokenElevation = 20;          // TOKEN_INFORMATION_CLASS.TokenElevation
    private const uint TokenQuery = 0x0008;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll")]
    private static extern nint OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint h);

    [DllImport("advapi32.dll")]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);

    [DllImport("advapi32.dll")]
    private static extern bool GetTokenInformation(nint token, int infoClass, out uint info, uint len, out uint retLen);

    /// <summary>当前进程是否以管理员提权运行。</summary>
    public static bool IsCurrentProcessElevated()
    {
        try
        {
            using var identity = global::System.Security.Principal.WindowsIdentity.GetCurrent();
            return new global::System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(global::System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>目标进程是否以管理员提权运行。查询失败（进程已退出/无权限）返回 false；
    /// PROCESS_QUERY_LIMITED_INFORMATION + TOKEN_QUERY 跨提权级别允许查询。</summary>
    public static bool IsProcessElevated(int pid)
    {
        nint proc = nint.Zero, token = nint.Zero;
        try
        {
            proc = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (proc == nint.Zero) return false;
            if (!OpenProcessToken(proc, TokenQuery, out token)) return false;
            if (!GetTokenInformation(token, TokenElevation, out uint elevated, sizeof(uint), out _)) return false;
            return elevated != 0;
        }
        catch { return false; }
        finally
        {
            if (token != nint.Zero) CloseHandle(token);
            if (proc != nint.Zero) CloseHandle(proc);
        }
    }
}
