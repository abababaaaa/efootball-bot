using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EfootballBot.Core.System;

/// <summary>
/// 对战日志（对应 xjbt 监控工具的 matchlogs.csv）：每场比赛结束（远端切换或数据停止）时
/// 追加一行，记录模式（服务器/P2P）、远端地址、区域、本场最小/平均/最大延迟、丢包率、时长，
/// v7.0.2 起再加 P95、抖动、样本数与一句话评级（方便事后对「哪片区域/哪个时段容易卡」做回归）。
/// 落盘在 data\network\matchlog.csv。
/// </summary>
public static class MatchLog
{
    private const string Header = "start_time,mode,remote,region,min_ms,avg_ms,max_ms,loss_pct,duration_s,p95_ms,jitter_ms,samples,grade,bars,tcping_ms";

    public static string FilePath =>
        Path.Combine(AppPaths.DataDir, "network", "matchlog.csv");

    /// <summary>追加一场记录；写失败时静默（监控不能因为日志挂掉）。</summary>
    public static void Append(DateTime startedAt, string mode, string remote, string region,
                             int minMs, int avgMs, int maxMs, double lossPct, int durationSec,
                             int p95Ms = -1, int jitterMs = -1, int samples = -1, string grade = "",
                             double bars = -1, int tcpingMs = -1)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            MigrateLegacyHeader();
            bool fresh = !File.Exists(FilePath) || new FileInfo(FilePath).Length == 0;
            var sb = new StringBuilder();
            if (fresh) sb.AppendLine(Header);
            sb.Append(startedAt.ToString("yyyy-MM-dd HH:mm:ss")).Append(',')
              .Append(Csv(mode)).Append(',')
              .Append(Csv(remote)).Append(',')
              .Append(Csv(region)).Append(',')
              .Append(minMs < 0 ? "" : minMs.ToString()).Append(',')
              .Append(avgMs < 0 ? "" : avgMs.ToString()).Append(',')
              .Append(maxMs).Append(',')
              .Append(lossPct.ToString("0.#")).Append(',')
              .Append(durationSec).Append(',')
              .Append(p95Ms <= 0 ? "" : p95Ms.ToString()).Append(',')
              .Append(jitterMs < 0 ? "" : jitterMs.ToString()).Append(',')
              .Append(samples < 0 ? "" : samples.ToString()).Append(',')
              .Append(Csv(grade)).Append(',')
              .Append(bars > 0 ? bars.ToString("0.0") : "").Append(',')
              .Append(tcpingMs >= 0 ? tcpingMs.ToString() : "").AppendLine();
            File.AppendAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }

    /// <summary>
    /// 旧版 9 列 matchlog 直接往下追加会错列：检测到旧表头就整份改名留档（matchlog-v1-*.csv），
    /// 新表重新起头。只在真正要写的时候做一次，不影响历史数据。
    /// </summary>
    private static void MigrateLegacyHeader()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length == 0) return;
            using var reader = new StreamReader(fs, Encoding.UTF8, true, 512, leaveOpen: true);
            string? head = reader.ReadLine();
            if (head is null || head.Contains("p95_ms", StringComparison.Ordinal)) return;
            reader.Dispose();
            fs.Dispose();
            string dir = Path.GetDirectoryName(FilePath)!;
            string legacy = Path.Combine(dir, $"matchlog-v1-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            File.Move(FilePath, legacy, overwrite: true);
        }
        catch { }
    }

    private static string Csv(string s)
        => s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? '"' + s.Replace("\"", "\"\"") + '"' : s;
}
