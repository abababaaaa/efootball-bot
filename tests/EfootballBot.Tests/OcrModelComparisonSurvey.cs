using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// OCR 识别模型/引擎 A/B 对比（真机帧，TestData 固化集）。
// 背景：2026-10-21 反馈包实机——用户机 WGC 无帧降级 GDI 且选了 Windows 内置 OCR，
// 界面词大量碎词/误识导致全模块卡环；为「设置页选模型」提供量化依据：
// 高精度 medium / 轻量 small / SimdPaddle 纯托管 / Windows 内置，同帧对比关键词命中与耗时。
// 结论归档：survey/ocr_model_comparison_20261021.md（由本测试输出整理）。
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
        // 每后端：预热 1 遍 + 计时 2 遍取最小（CPU 基准取 min 最稳）
        RunBackend("RapidV6-small", () => new OcrRecognizer(OcrEngineKind.RapidV6, recModel: OcrRecModelKind.Small));
        RunBackend("RapidV6-medium", () => new OcrRecognizer(OcrEngineKind.RapidV6, recModel: OcrRecModelKind.Medium));
        RunBackend("SimdPaddle-medium", () => new OcrRecognizer(OcrEngineKind.SimdPaddle, recModel: OcrRecModelKind.Medium));
        try
        {
            RunBackend("Windows", () => new OcrRecognizer(OcrEngineKind.Windows));
        }
        catch (Exception ex)
        {
            _out.WriteLine($"Windows 引擎不可用：{ex.Message}");
        }

        // 汇总表
        _out.WriteLine("");
        _out.WriteLine("| 后端 | 关键词命中 | 平均耗时/帧 | 漏判 |");
        _out.WriteLine("|---|---|---|---|");
        foreach (var r in _results)
        {
            _out.WriteLine($"| {r.Name} | {r.Hits}/{Frames.Length} | {r.AvgMs:F0} ms | {string.Join("；", r.Missed)} |");
        }

        // 契约（宽松）：medium 与 small 在固化集上都必须过半，防「默认档换模型后大幅退化」 unnoticed
        foreach (var r in _results.Where(r => r.Name.StartsWith("RapidV6")))
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
                var res = ocr.RecognizeAsync(frame).GetAwaiter().GetResult();
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
