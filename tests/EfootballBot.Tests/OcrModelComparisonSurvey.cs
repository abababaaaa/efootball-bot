using EfootballBot.Core.Capture;
using EfootballBot.Core.Config;
using EfootballBot.Core.Vision;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// OCR 引擎 A/B 对比（真机帧，TestData 固化集）。三个本地 PP-OCRv6-small 配置共用同一套模型文件，
// 只换推理内核/量化精度：SimdPaddle=纯托管 SIMD（默认）、RapidV6 fp32=ONNX Runtime CPU、
// RapidV6 int8=rec 动态量化（det 仍 fp32）。三者命中口径一致（ContainsJoined）。
// 第四个后端 Unlimited-OCR 是外部 VLM 服务，只在服务可达时参与（否则整段跳过并标注）。
// 历史数据见 survey/ocr_model_comparison_20261021.md 与 survey/ocr_engine_benchmark_20261023.md。
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

    private sealed record BenchRow(string Name, int Hits, double AvgMs, double PeakMs, long MemMb, string[] Missed);

    private static FrameData LoadFrame(string name)
    {
        var data = TestFrameLoader.LoadPngBgra(Path.Combine(TestDir, name), out int w, out int h);
        return new FrameData(data, w, h, 0);
    }

    private readonly List<BenchRow> _results = new();

    [Fact]
    public void Compare_Backends_OnRealFrames()
    {
        // 每后端：预热 1 遍 + 计时 2 遍取最小（CPU 基准取 min 最稳）
        RunBackend("SimdPaddle-small", () => new OcrRecognizer(OcrEngineKind.SimdPaddle, preferInt8: false));

        // int8 rec 是开发候选（tools/int8_candidate_v1，未随发布包分发）：测试输出里没有就跳过
        bool int8Present = File.Exists(Path.Combine(AppContext.BaseDirectory,
            "models", "v6", "int8", "PP-OCRv6_rec_small_int8.onnx"));
        if (int8Present)
            RunBackend("RapidV6-small-int8", () => new OcrRecognizer(OcrEngineKind.RapidV6, preferInt8: true));
        else
            _out.WriteLine("（模型目录无 int8 rec，跳过 RapidV6-int8 档）");

        RunBackend("RapidV6-small-fp32", () => new OcrRecognizer(OcrEngineKind.RapidV6, preferInt8: false));

        // Unlimited-OCR：外部服务（默认 127.0.0.1:10000）。2 秒探不到就跳过，不让 CI 依赖外部进程；
        // EFOOTBALL_OCR_UNLIMITED_ENDPOINT 可指向 stub/真实服务做端到端验证
        var unlimitedCfg = new UnlimitedOcrConfig();
        string? ep = Environment.GetEnvironmentVariable("EFOOTBALL_OCR_UNLIMITED_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(ep)) unlimitedCfg.Endpoint = ep;
        RunExternalBackend("Unlimited-OCR（VLM 外部服务）", unlimitedCfg);

        // 汇总表
        _out.WriteLine("");
        _out.WriteLine("| 后端 | 关键词命中 | 平均耗时/帧 | 最慢帧 | 峰值内存 | 漏判 |");
        _out.WriteLine("|---|---|---|---|---|---|");
        foreach (var r in _results)
        {
            _out.WriteLine($"| {r.Name} | {r.Hits}/{Frames.Length} | {r.AvgMs:F0} ms | {r.PeakMs:F0} ms | {r.MemMb} MB | {string.Join("；", r.Missed)} |");
        }

        // 契约（宽松）：本地引擎在固化集上都必须过半，防引擎/模型退化未被发现
        foreach (var r in _results.Where(r => !r.Name.StartsWith("Unlimited")))
            Assert.True(r.Hits >= Frames.Length / 2, $"{r.Name} 关键词命中过少：{r.Hits}/{Frames.Length}");
    }

    private void RunBackend(string name, Func<OcrRecognizer> create) => RunBackend(name, create, passes: 3);

    private void RunExternalBackend(string name, UnlimitedOcrConfig cfg)
    {
        OcrRecognizer ocr;
        try
        {
            ocr = new OcrRecognizer(OcrEngineKind.UnlimitedOcr, unlimitedOcr: cfg);
        }
        catch (Exception ex)
        {
            _out.WriteLine($"（{name} 不可用，跳过：{ex.Message}）");
            return;
        }
        if (ocr.FallbackNote is not null)
        {
            _out.WriteLine($"（{name} 服务不可达，跳过：{ocr.FallbackNote}）");
            ocr.Dispose();
            return;
        }
        RunBackend(name, () => ocr, passes: 1, ownsOcr: false);
        ocr.Dispose();
    }

    private void RunBackend(string name, Func<OcrRecognizer> create, int passes, bool ownsOcr = true)
    {
        var ocr = create();
        var times = new Dictionary<string, double>();
        var hits = new HashSet<string>();
        long memBefore = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
        for (int pass = 0; pass < passes; pass++)
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
        long memAfter = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
        long memMb = Math.Max(0, (memAfter - memBefore) / (1024 * 1024));

        var avg = times.Values.Average();
        var missed = Frames.Where(f => !hits.Contains(f.File)).Select(f => $"{f.File}({f.Keyword})").ToArray();
        _results.Add(new BenchRow(name, hits.Count, avg, times.Values.Max(), memMb, missed));
        _out.WriteLine($"[{ocr.LanguageTag}] 命中 {hits.Count}/{Frames.Length}，平均 {avg:F0} ms/帧，内存 +{memMb} MB");
        foreach (var f in Frames)
            _out.WriteLine($"    {f.File}: {times[f.File]:F0} ms，命中={hits.Contains(f.File)}");
        if (ownsOcr) ocr.Dispose();
    }
}
