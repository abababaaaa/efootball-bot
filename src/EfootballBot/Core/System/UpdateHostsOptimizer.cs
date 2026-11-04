using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace EfootballBot.Core.System;

/// <summary>
/// 周四更新加速（移植自 xjbt Thursday helper）：eFootball 的更新 CDN
/// <c>d1ln4m3c7n87ju.cloudfront.net</c> 有多个边缘 IP，本机 DNS 常返回较远/较慢的一个。
/// 做法：解析该域名的全部 IP → TCP/443 测速（CDN 是 HTTPS，建连延迟比 ICMP 更贴近真实体验，
/// 失败再退 ICMP）→ 取最快者写入 hosts + 刷新 DNS 缓存。写 hosts 需要管理员权限。
/// 与 <see cref="GitHostsOptimizer"/> 独立：各自维护带标记的区块，互不覆盖，可单独还原。
/// </summary>
public static class UpdateHostsOptimizer
{
    /// <summary>eFootball 更新分发域名（包内 6.xxx.exe 字符串实证）。</summary>
    public const string UpdateHost = "d1ln4m3c7n87ju.cloudfront.net";

    private const string BeginMark = "# ==== efootball update optimize (auto generated) ====";
    private const string EndMark = "# ==== efootball update optimize end ====";

    private static string HostsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    /// <summary>hosts 里是否已有本工具的映射块。</summary>
    public static bool IsApplied
    {
        get
        {
            try { return File.Exists(HostsPath) && File.ReadAllText(HostsPath).Contains(BeginMark); }
            catch { return false; }
        }
    }

    /// <summary>当前 hosts 里该域名指向的 IP；没有返回 null。</summary>
    public static string? CurrentIp
    {
        get
        {
            try
            {
                if (!File.Exists(HostsPath)) return null;
                foreach (var line in File.ReadAllLines(HostsPath))
                {
                    var t = line.Trim();
                    if (t.Length == 0 || t.StartsWith("#")) continue;
                    var tokens = t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length >= 2 && tokens.Skip(1).Contains(UpdateHost)) return tokens[0];
                }
            }
            catch { }
            return null;
        }
    }

    /// <summary>测速并写入 hosts。返回多行结果文本（给页面/日志用）。</summary>
    public static string Apply()
    {
        var sb = new StringBuilder();
        if (!GitHostsOptimizer.IsAdministrator())
            return "需要管理员权限：请以管理员身份运行本程序。";

        // 1. 候选 IP：DNS 全部 + 少量已知 CloudFront 边缘兜底（解析失败时仍可测）
        var candidates = new HashSet<string>();
        try
        {
            foreach (var a in Dns.GetHostAddresses(UpdateHost))
                if (a.AddressFamily == AddressFamily.InterNetwork) candidates.Add(a.ToString());
        }
        catch (Exception ex) { sb.AppendLine($"解析 {UpdateHost} 失败：{ex.Message}"); }

        if (candidates.Count == 0)
        {
            sb.AppendLine("拿不到任何候选 IP，hosts 未修改。");
            return sb.ToString();
        }
        sb.AppendLine($"候选 {candidates.Count} 个：{string.Join(", ", candidates)}");

        // 2. TCP/443 测速（CDN 场景比 ICMP 准），全失败退 ICMP
        string? best = null;
        int bestMs = int.MaxValue;
        foreach (var ip in candidates)
        {
            int ms = NetworkProbe.TcpLatency(ip, 443, 1500);
            if (ms < 0) ms = NetworkProbe.IcmpPing(ip, 1200);
            sb.AppendLine($"  {ip,-16} {(ms < 0 ? "超时" : ms + " ms")}");
            if (ms >= 0 && ms < bestMs) { bestMs = ms; best = ip; }
        }
        if (best is null)
        {
            sb.AppendLine("全部超时，hosts 未修改。");
            return sb.ToString();
        }

        // 3. 备份 + 写块（先删旧块，保留 GitHostsOptimizer 的块）
        try
        {
            string backup = HostsPath + ".efb-update.bak";
            try { File.Copy(HostsPath, backup, true); sb.AppendLine($"已备份 hosts -> {backup}"); }
            catch (Exception ex) { sb.AppendLine($"备份失败（继续）：{ex.Message}"); }

            var lines = File.ReadAllLines(HostsPath).ToList();
            RemoveBlock(lines);
            lines.Add(BeginMark);
            lines.Add($"{best}\t{UpdateHost}");
            lines.Add(EndMark);
            File.WriteAllLines(HostsPath, lines, new UTF8Encoding(false));
            sb.AppendLine($"已写入：{UpdateHost} -> {best}（{bestMs} ms）");
            FlushDns(sb);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"写入 hosts 失败（需要管理员权限）：{ex.Message}");
        }
        return sb.ToString();
    }

    /// <summary>还原：只删本工具自己的块，不影响其他 hosts 条目。</summary>
    public static (bool ok, string message) Restore()
    {
        if (!GitHostsOptimizer.IsAdministrator())
            return (false, "需要管理员权限：请以管理员身份运行本程序。");
        try
        {
            var lines = File.ReadAllLines(HostsPath).ToList();
            if (!RemoveBlock(lines)) return (true, "hosts 里没有本工具的记录，无需还原。");
            File.WriteAllLines(HostsPath, lines, new UTF8Encoding(false));
            var sb = new StringBuilder("已移除更新加速记录。");
            FlushDns(sb);
            return (true, sb.ToString());
        }
        catch (Exception ex) { return (false, $"还原失败：{ex.Message}"); }
    }

    /// <summary>删掉标记块内的行；返回是否有改动。</summary>
    private static bool RemoveBlock(List<string> lines)
    {
        int start = lines.FindIndex(l => l.Trim() == BeginMark);
        int end = lines.FindIndex(l => l.Trim() == EndMark);
        if (start < 0)
        {
            // 兼容：块被手工改过时按域名删多余行
            int removed = lines.RemoveAll(l =>
            {
                var t = l.Trim();
                if (t.Length == 0 || t.StartsWith("#")) return false;
                return t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                        .Skip(1).Contains(UpdateHost);
            });
            return removed > 0;
        }
        if (end < start) end = start;
        lines.RemoveRange(start, Math.Min(end - start + 1, lines.Count - start));
        return true;
    }

    private static void FlushDns(StringBuilder sb)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ipconfig", "/flushdns")
            { CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, UseShellExecute = false });
            p?.WaitForExit(5000);
            sb.Append("DNS 缓存已刷新。");
        }
        catch { }
    }
}
