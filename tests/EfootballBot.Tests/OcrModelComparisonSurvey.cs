using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// OCR 引擎 A/B 对比（真机帧，TestData 固化集）。两引擎共用同一套 PP-OCRv6-small 模型文件，
// 只换推理内核：RapidV6=ONNX Runtime CPU / SimdPaddle=纯托管 SIMD（默认引擎）。
// 历史数据（2026-10-21，medium 模型还在时的四后端对比）见 survey/ocr_model_comparison_20261021.md：
// 命中 10/12 三 PP-OCR 持平，SimdPaddle-medium 1085ms < RapidV6-small 1764ms < RapidV6-medium 3703ms，
// Windows 内置 9/12 @52ms（已删除引擎，质量不达挂机判定线）。
public class OcrModelComparisonSurvey
{
    private readonly ITestOutputHelper _out;
    public OcrModelComparisonSurvey(ITestOutputHelper output) => _out = output;

    private static string TestDir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData");

    /// <summary>对比帧与期望关键词（固定 UI 词，来自各 RealFrame 测试已锁定的判定词）。</summary>
    private static readonly (string File, string Keyword)[] Frames =
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

    private static FrameData LoadFrame(string name)
    {
        var data = TestFrameLoader.LoadPngBgra(Path.Combine(TestDir, name), out int w, out int h);
        return new FrameData(data, w, h, 0);
    }

    private readonly List<(string Name, int Hits, double AvgMs, string[] Missed)> _results = new();

    [Fact]
    public void Compare_Backends_OnRealFrames()
    {
        // 每后端：预热 1 遍 + 计时 2 遍取最小（CPU 基准取 min 最稳）；两引擎统一 fp32 口径
        RunBackend("SimdPaddle-small", () => new OcrRecognizer(OcrEngineKind.SimdPaddle, preferInt8: false));
        RunBackend("RapidV6-small", () => new OcrRecognizer(OcrEngineKind.RapidV6, preferInt8: false));

        // 汇总表
        _out.WriteLine("");
        _out.WriteLine("| 后端 | 关键词命中 | 平均耗时/帧 | 漏判 |");
        _out.WriteLine("|---|---|---|---|");
        foreach (var r in _results)
        {
            _out.WriteLine($"| {r.Name} | {r.Hits}/{Frames.Length} | {r.AvgMs:F0} ms | {string.Join("；", r.Missed)} |");
        }

        // 契约（宽松）：默认引擎与备用引擎在固化集上都必须过半，防引擎/模型退化未被发现
        foreach (var r in _results)
            Assert.True(r.Hits >= Frames.Length / 2, $"{r.Name} 关键词命中过少：{r.Hits}/{Frames.Length}");
    }

    private void RunBackend(string name, Func<OcrRecognizer> create)
    {
        using var ocr = create();
        var times = new Dictionary<string, double>();
        var hits = new HashSet<string>();
        for (int pass = 0; pass < 3; pass++)
        {
            foreach (var (file, keyword) in Frames)
            {
                var frame = LoadFrame(file);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var res = TestOcr.Run(ocr, frame);
                sw.Stop();
                times[file] = Math.Min(times.GetValueOrDefault(file, double.MaxValue), sw.Elapsed.TotalMilliseconds);
                if (res.ContainsJoined(keyword)) hits.Add(file);
            }
        }

        var avg = times.Values.Average();
        var missed = Frames.Where(f => !hits.Contains(f.File)).Select(f => $"{f.File}({f.Keyword})").ToArray();
        _results.Add((name, hits.Count, avg, missed));
        _out.WriteLine($"[{ocr.LanguageTag}] 命中 {hits.Count}/{Frames.Length}，平均 {avg:F0} ms/帧");
        foreach (var f in Frames)
            _out.WriteLine($"    {f.File}: {times[f.File]:F0} ms，命中={hits.Contains(f.File)}");
    }
}
