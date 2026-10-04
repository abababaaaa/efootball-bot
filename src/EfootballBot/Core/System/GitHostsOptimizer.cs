using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;

namespace EfootballBot.Core.System;

/// <summary>
/// GitHub 直连优化：对 GitHub 关键域名做「候选 IP 收集 → TCP/443 测速 → 写 hosts」，
/// 用于在 GitHub 无法直连（检查更新/下载失败）时把域名指向延迟最低的 IP。写 hosts 需要管理员权限。
/// </summary>
public static class GitHostsOptimizer
{
    /// <summary>GitHub 官方稳定 IP 候选（配合本机 DNS 解析结果一起测速筛选）。</summary>
    private static readonly Dictionary<string, string[]> KnownIps = new()
    {
        ["github.com"] = new[] { "140.82.112.3", "140.82.113.3", "140.82.114.3", "140.82.115.3", "140.82.116.3", "20.205.243.166", "20.248.137.48" },
        ["api.github.com"] = new[] { "140.82.112.5", "140.82.113.5", "140.82.114.5", "140.82.115.5", "140.82.116.5", "20.205.243.168" },
        ["codeload.github.com"] = new[] { "140.82.112.9", "140.82.113.9", "140.82.114.9", "140.82.115.9", "140.82.116.9", "20.205.243.169" },
        ["gist.github.com"] = new[] { "140.82.112.4", "140.82.113.4", "140.82.114.4", "140.82.115.4", "140.82.116.4" },
        ["raw.githubusercontent.com"] = new[] { "185.199.108.133", "185.199.109.133", "185.199.110.133", "185.199.111.133" },
        ["objects.githubusercontent.com"] = new[] { "185.199.108.133", "185.199.109.133", "185.199.110.133", "185.199.111.133" },
        ["github.global.ssl.fastly.net"] = new[] { "185.199.108.153", "185.199.109.153", "185.199.110.153", "185.199.111.153" },
        ["assets-cdn.github.com"] = new[] { "185.199.108.154", "185.199.109.154", "185.199.110.154", "185.199.111.154" },
    };

    private static string HostsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>执行优化并返回多行结果文本。</summary>
    public static string Optimize()
    {
        var sb = new StringBuilder();

        var hosts = HostsPath;
        if (!File.Exists(hosts))
        {
            sb.AppendLine("错误：未找到 hosts 文件。");
            return sb.ToString();
        }

        // 1. 备份
        string backup = hosts + ".github.bak";
        try { File.Copy(hosts, backup, true); sb.AppendLine($"已备份 hosts -> {backup}"); }
        catch (Exception ex) { sb.AppendLine($"备份失败：{ex.Message}"); }

        // 2. 逐个域名测速选最快 IP
        var mappings = new Dictionary<string, string>();
        foreach (var (domain, knownIps) in KnownIps)
        {
            var candidates = new HashSet<string>(knownIps);
            try
            {
                foreach (var addr in Dns.GetHostAddresses(domain))
                    if (addr.AddressFamily == AddressFamily.InterNetwork)
                        candidates.Add(addr.ToString());
            }
            catch { }

            string? best = null;
            int bestMs = int.MaxValue;
            foreach (var ip in candidates)
            {
                int ms = TcpLatency(ip, 443);
                if (ms >= 0 && ms < bestMs) { bestMs = ms; best = ip; }
            }
            if (best is not null)
            {
                mappings[domain] = best;
                sb.AppendLine($"  {domain,-30} -> {best,-16} ({bestMs} ms)");
            }
            else
            {
                sb.AppendLine($"  [跳过] {domain} 全部超时");
            }
        }

        if (mappings.Count == 0)
        {
            sb.AppendLine("未找到任何可用 IP，hosts 未修改。");
            return sb.ToString();
        }

        // 3. 去旧 + 追加
        try
        {
            var domains = KnownIps.Keys.ToHashSet();
            var lines = File.ReadAllLines(hosts).ToList();
            lines.RemoveAll(line =>
            {
                var t = line.Trim();
                if (t == "" || t.StartsWith("#")) return false;
                var noComment = t.Split('#')[0];
                var tokens = noComment.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                return tokens.Skip(1).Any(tok => domains.Contains(tok));
            });
            lines.Add("# ==== GitHub optimize (auto generated) ====");
            foreach (var (d, ip) in mappings) lines.Add($"{ip}\t{d}");
            lines.Add("# ==== GitHub optimize end ====");

            File.WriteAllLines(hosts, lines, new UTF8Encoding(false));
            sb.AppendLine();
            sb.AppendLine($"已写入 {mappings.Count} 条映射。");

            try
            {
                using var p = Process.Start(new ProcessStartInfo("ipconfig", "/flushdns") { CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                p?.WaitForExit(5000);
                sb.AppendLine("DNS 缓存已刷新。");
            }
            catch { }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"写入 hosts 失败（可能需要管理员权限）：{ex.Message}");
        }

        return sb.ToString();
    }

    /// <summary>TCP 建连延迟（毫秒），失败/超时返回 -1。</summary>
    private static int TcpLatency(string ip, int port)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var c = new TcpClient();
            var ar = c.BeginConnect(ip, port, null, null);
            if (ar.AsyncWaitHandle.WaitOne(1200))
            {
                c.EndConnect(ar);
                sw.Stop();
                return (int)sw.ElapsedMilliseconds;
            }
        }
        catch { }
        return -1;
    }
}