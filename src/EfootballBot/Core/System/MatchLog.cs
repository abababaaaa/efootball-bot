using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EfootballBot.Core.System;

/// <summary>
/// 对战日志（对应 xjbt 监控工具的 matchlogs.csv）：每场比赛结束（远端切换或数据停止）时
/// 追加一行，记录模式（服务器/P2P）、远端地址、区域、本场最小/平均/最大延迟、丢包率与时长。
/// 落盘在 data\network\matchlog.csv，便于事后看「哪片区域的服务器/对手更容易卡」。
/// </summary>
public static class MatchLog
{
    private const string Header = "start_time,mode,remote,region,min_ms,avg_ms,max_ms,loss_pct,duration_s";

    public static string FilePath =>
        Path.Combine(AppPaths.DataDir, "network", "matchlog.csv");

    /// <summary>追加一场记录；写失败时静默（监控不能因为日志挂掉）。</summary>
    public static void Append(DateTime startedAt, string mode, string remote, string region,
                             int minMs, int avgMs, int maxMs, double lossPct, int durationSec)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
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
              .Append(durationSec).AppendLine();
            File.AppendAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }

    private static string Csv(string s)
        => s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? '"' + s.Replace("\"", "\"\"") + '"' : s;
}
