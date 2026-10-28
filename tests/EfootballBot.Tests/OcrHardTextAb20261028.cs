using System.Text;
using System.Text.RegularExpressions;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 美术字优化 A/B 基准（2026-10-28）：P1 行级重读 × P2 检测参数调优。
// 口径与前三批一致（12 帧 + 期望词，ContainsJoined 命中），并额外给出**词级相同率**
// 与逐帧词表 diff——本批的核心指标是词级，命中数在碎词场景会掩盖退化。
// 16 帧 = 12 固定帧 + 4 硬帧（entry_maxlevel 大字「1」/ focus_fail_live / difficulty_live / inmatch_popup_tr）。
public class OcrHardTextAb20261028
{
    private readonly ITestOutputHelper _out;
    public OcrHardTextAb20261028(ITestOutputHelper output) => _out = output;

    private static string TestDir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData");

    private static readonly (string File, string Keyword)[] KeywordFrames =
    {
        ("difficulty_new_ui.png", "选择赛事等级"),
        ("difficulty_2.png", "选择赛事等级"),
        ("keymatch_p1.png", "关键比赛"),
        ("keymatch_p3.png", "关键比赛"),
        ("matchhub_user_1.png", "我的联赛"),
        ("match_start_page.png", "下一步"),
        ("myleague_exp_result_page.png", "查看经验值"),
        ("itempage_inuse_1.png", "使用的物品"),
        ("next_season.png", "下个赛季"),
        ("fulltime_user_1.png", "下一步"),
        ("home_nav_cursor.png", "前往比赛"),
        ("focus_state_2.png", "赛程表"),
    };

    private static readonly string[] HardFrames =
    {
        "entry_maxlevel.png", "focus_fail_live.png", "difficulty_live_20261020.png", "inmatch_popup_tr.png",
    };

    private static string[] AllFrames => KeywordFrames.Select(f => f.File).Concat(HardFrames).ToArray();

    private sealed record FrameStat(double Ms, string[] Words);
    private sealed record Row(string Name, string Kernel, int Hits, double AvgMs, double PeakMs,
        int Reread, int Changed, string[] Missed);

    private readonly List<Row> _rows = new();
    private readonly Dictionary<string, Dictionary<string, FrameStat>> _data = new();
    private readonly StringBuilder _report = new();

    private const string BaselineName = "A 基线（现状）";

    [Fact]
    public void Ab_LineReread_And_DetTuning()
    {
        // A 基线：Sdcb 默认 det（Bitmap 0.2 / Box 0.45 / Unclip 1.4 / Limit 960），不做行重读
        Run(BaselineName, OcrTuning.Off);

        // P1：仅治逐字碎词（RequireFragments 默认开），放大 2 倍 / 外扩 0.35 行高
        Run("B P1 碎词重读 ×2", new OcrTuning { LineReread = true });
        // P1 放大 3 倍
        Run("C P1 碎词重读 ×3", new OcrTuning { LineReread = true, LineRereadScale = 3 });
        // P1 收紧预算：单帧最多重读 6 行
        Run("D P1 碎词重读 ×2（上限 6 行）", new OcrTuning { LineReread = true, LineRereadMaxLines = 6 });
        // 对照：放开"任何多词行"（第一轮的宽松规则，已知会合并相邻元素）
        Run("E P1 宽松多词行 ×2（对照）", new OcrTuning { LineReread = true, LineRereadRequireFragments = false });

        PrintTable();
        PrintDiffs();

        string reportPath = Path.Combine(AppContext.BaseDirectory, "ocr_hardtext_ab_20261028.txt");
        File.WriteAllText(reportPath, _report.ToString(), new UTF8Encoding(false));
        _out.WriteLine($"（全量词表已写入 {reportPath}）");
    }

    private void Run(string name, OcrTuning tuning)
    {
        using var ocr = new OcrRecognizer(OcrEngineKind.SimdPaddle, tuning: tuning);
        var times = new Dictionary<string, double>();
        var hits = new HashSet<string>();
        int reread = 0, changed = 0;

        for (int pass = 0; pass < 3; pass++)
        {
            bool last = pass == 2;
            foreach (var file in AllFrames)
            {
                var frame = LoadFrame(file);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var res = ocr.RecognizeAsync(frame).GetAwaiter().GetResult();
                sw.Stop();
                times[file] = Math.Min(times.GetValueOrDefault(file, double.MaxValue), sw.Elapsed.TotalMilliseconds);
                if (!last) continue;
                _data[name] = _data.TryGetValue(name, out var d) ? d : new();
                _data[name][file] = new FrameStat(times[file], res.Words.Select(w => w.Text.Trim()).ToArray());
                if (tuning.LineReread)
                {
                    reread += ocr.LastRereadStats.LinesReread;
                    changed += ocr.LastRereadStats.LinesChanged;
                }
                foreach (var kf in KeywordFrames)
                    if (kf.File == file && res.ContainsJoined(kf.Keyword)) hits.Add(file);
            }
        }

        var missed = KeywordFrames.Where(kf => !hits.Contains(kf.File))
            .Select(kf => $"{kf.File}({kf.Keyword})").ToArray();
        var row = new Row(name, tuning.IsOff ? "默认" : "调优", hits.Count,
            times.Values.Average(), times.Values.Max(), reread, changed, missed);
        _rows.Add(row);
        _out.WriteLine($"[{name}] 命中 {row.Hits}/{KeywordFrames.Length}，平均 {row.AvgMs:F0} ms/帧" +
                       (tuning.LineReread ? $"，重读 {reread} 行/改 {changed} 行" : ""));
    }

    private void PrintTable()
    {
        var sb = new StringBuilder();
        sb.AppendLine("| 配置 | 关键词命中 | 平均耗时/帧 | 最慢帧 | 词级相同率 | 重读/改动行 | 漏判 |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var r in _rows)
        {
            string pct = r.Name == BaselineName ? "—" : WordPct(r.Name);
            string rr = r.Reread == 0 && r.Changed == 0 ? "—" : $"{r.Reread}/{r.Changed}";
            sb.AppendLine($"| {r.Name} | {r.Hits}/{KeywordFrames.Length} | {r.AvgMs:F0} ms | {r.PeakMs:F0} ms | {pct} | {rr} | {string.Join("；", r.Missed)} |");
        }
        _out.WriteLine("");
        _out.WriteLine(sb.ToString());
        _report.Insert(0, sb.ToString());
    }

    private static string Loose(string s) => Regex.Replace(s, @"\s+", "");

    private string WordPct(string name)
    {
        var (same, baseOnly, candOnly) = DiffCounts(name, out _);
        int total = same + baseOnly + candOnly;
        return total == 0 ? "n/a" : $"{same * 100.0 / total:F1}%";
    }

    private (int Same, int BaseOnly, int CandOnly) DiffCounts(string name, out bool _unused)
    {
        _unused = false;
        if (!_data.TryGetValue(name, out var cand) || !_data.TryGetValue(BaselineName, out var baseline))
            return (0, 0, 0);
        int sameT = 0, bT = 0, cT = 0;
        foreach (var file in AllFrames)
        {
            var b = baseline[file].Words.Select(Loose).Where(x => x.Length > 0).ToHashSet();
            var c = cand[file].Words.Select(Loose).Where(x => x.Length > 0).ToHashSet();
            sameT += b.Intersect(c).Count();
            bT += b.Except(c).Count();
            cT += c.Except(b).Count();
        }
        return (sameT, bT, cT);
    }

    private void PrintDiffs()
    {
        if (!_data.TryGetValue(BaselineName, out var baseline)) return;
        _out.WriteLine("");
        _out.WriteLine("## 词级差异（vs 基线）");
        foreach (var r in _rows)
        {
            if (r.Name == BaselineName) continue;
            _data.TryGetValue(r.Name, out var cand);
            _out.WriteLine($"[{r.Name}] 相同 {WordPct(r.Name)}");
            foreach (var file in AllFrames)
            {
                var b = baseline[file].Words.Select(Loose).Where(x => x.Length > 0).ToHashSet();
                var c = cand[file].Words.Select(Loose).Where(x => x.Length > 0).ToHashSet();
                var onlyB = b.Except(c).ToArray();
                var onlyC = c.Except(b).ToArray();
                if (onlyB.Length == 0 && onlyC.Length == 0) continue;
                string detail = $"  {file}（同 {b.Intersect(c).Count()}）";
                if (onlyB.Length > 0) detail += $"\n      仅基线: {Clip(onlyB)}";
                if (onlyC.Length > 0) detail += $"\n      仅本档: {Clip(onlyC)}";
                _out.WriteLine(detail);
                _report.AppendLine($"[{r.Name}] {file}");
                if (onlyB.Length > 0) _report.AppendLine($"  仅基线: {string.Join(" | ", onlyB)}");
                if (onlyC.Length > 0) _report.AppendLine($"  仅本档: {string.Join(" | ", onlyC)}");
            }
            _report.AppendLine();
        }
    }

    private static string Clip(string[] words)
    {
        const int max = 10;
        return string.Join(" ", words.Take(max).Select(w => "「" + (w.Length > 22 ? w[..22] + "…" : w) + "」"))
               + (words.Length > max ? $" …共{words.Length}" : "");
    }

    private static FrameData LoadFrame(string name)
    {
        var data = TestFrameLoader.LoadPngBgra(Path.Combine(TestDir, name), out int w, out int h);
        return new FrameData(data, w, h, 0);
    }
}
