using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace EfootballBot.Core.System;

/// <summary>
/// 强制 P2P 匹配（移植自 xjbt forceP2P 的 PORT ONLY 模式，20260813 版默认模式）。
/// 原理：屏蔽游戏到测速服务器的 UDP 目标端口（5521/10000/30000/50000），游戏判定无法服务器转发，
/// 只能给出点对点（P2P）对手。原工具用 WinDivert 拦包，这里改用 Windows 防火墙出站规则实现：
/// 不随包驱动、不触发杀软误报，规则绑定 eFootball.exe，删除即还原。
/// 实测口径见 _research/etk_xjbt_20260813（包内 3.xxx.exe 字符串）。
/// 可选 TCP 规则（来自包内 92.efootballfirewallrule.bat）：出站 TCP 30000-35000,5736，
/// 压掉 TCP 对战模式，进一步逼向 P2P。P2P 匹配等待会明显变长，务必可一键关闭。
/// </summary>
public static class ForcedP2P
{
    public const string UdpRuleName = "efootball-bot-p2p-udp";
    public const string TcpRuleName = "efootball-bot-p2p-tcp";

    /// <summary>测速/服务器转发端口（UDP 出站）。</summary>
    private const string UdpPorts = "5521,10000,30000,50000";

    /// <summary>TCP 服务器模式端口段（可选，默认不加）。</summary>
    private const string TcpPorts = "30000-35000,5736";

    /// <summary>定位国际服游戏本体（运行中进程锚定 → Steam 库回退）。找不到返回 null。</summary>
    public static string? ResolveGameExe(string? hintDir = null)
    {
        try
        {
            // 注意：不能给 params 参数直接传 null（会被当成 null 数组），分支调用
            return string.IsNullOrWhiteSpace(hintDir)
                ? GameSettingsLocator.LocateGameExe(true)
                : GameSettingsLocator.LocateGameExe(true, hintDir);
        }
        catch { return null; }
    }

    /// <summary>UDP 规则是否已存在（实读 netsh，不读配置）。</summary>
    public static bool IsEnabled => RuleExists(UdpRuleName);

    /// <summary>TCP 规则是否已存在。</summary>
    public static bool TcpEnabled => RuleExists(TcpRuleName);

    public static bool RuleExists(string name)
    {
        var (code, _) = Run($"advfirewall firewall show rule name=\"{name}\"");
        return code == 0;
    }

    /// <summary>
    /// 启用强制 P2P。返回 (成功, 说明)。非管理员时 netsh 会拒绝（提示用管理员运行本程序）。
    /// </summary>
    public static (bool ok, string message) Enable(string exePath, bool tcpToo)
    {
        if (!GitHostsOptimizer.IsAdministrator())
            return (false, "需要管理员权限：请以管理员身份运行本程序。");
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return (false, "未定位到国际服游戏本体（eFootball.exe）。先启动一次游戏，或先点「打开设置工具」形成路径记忆。");

        // 先删旧规则（避免重复叠加），失败不影响后续 add
        Disable();
        var sb = new StringBuilder();

        var (c1, o1) = Run($"advfirewall firewall add rule name=\"{UdpRuleName}\" " +
                           $"dir=out action=block program=\"{exePath}\" protocol=UDP " +
                           $"remoteport={UdpPorts} enable=yes");
        if (c1 != 0) return (false, $"UDP 规则写入失败：{Trim(o1)}");
        sb.Append($"已屏蔽测速/转发端口 UDP {UdpPorts}");

        if (tcpToo)
        {
            var (c2, o2) = Run($"advfirewall firewall add rule name=\"{TcpRuleName}\" " +
                               $"dir=out action=block program=\"{exePath}\" protocol=TCP " +
                               $"remoteport={TcpPorts} enable=yes");
            if (c2 != 0) sb.Append($"，TCP 规则失败：{Trim(o2)}");
            else sb.Append($"，并屏蔽 TCP {TcpPorts}");
        }
        return (true, sb.ToString());
    }

    /// <summary>关闭：删除两条规则（不存在也视为成功）。</summary>
    public static (bool ok, string message) Disable()
    {
        if (!GitHostsOptimizer.IsAdministrator())
            return (false, "需要管理员权限：请以管理员身份运行本程序。");
        Run($"advfirewall firewall delete rule name=\"{UdpRuleName}\"");
        Run($"advfirewall firewall delete rule name=\"{TcpRuleName}\"");
        bool still = RuleExists(UdpRuleName) || RuleExists(TcpRuleName);
        return still ? (false, "规则删除后仍检测到残留，请到防火墙高级设置手工删除。")
                     : (true, "已还原（规则已删除）。");
    }

    // ---------------- 进程调用 ----------------

    private static (int code, string output) Run(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh")
            {
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            if (p is null) return (-1, "无法启动 netsh");
            // 先等退出再读输出：netsh 输出很短（<4KB），不会填满管道；反过来读会给无限阻塞留口子
            if (!p.WaitForExit(4000)) { try { p.Kill(true); } catch { } return (-1, "netsh 超时"); }
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            return (p.ExitCode, output);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    private static string Trim(string s)
    {
        s = s.Trim();
        return s.Length <= 160 ? s : s[..160] + "…";
    }
}
