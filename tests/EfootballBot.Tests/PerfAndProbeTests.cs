using EfootballBot.Core.Bot;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Config;
using EfootballBot.Core.Input;
using EfootballBot.Core.Observation;
using EfootballBot.Core.Vision;
using System.Text.Json;
using Xunit;

namespace EfootballBot.Tests;

// P1-11 / P0-1 / P1-14 / P0-9 的纯逻辑回归：
// 静帧指纹（采样策略）、输入活性文字指纹、分段耗时 EWMA、OCR 性能档配置往返。
// 这几项都在观察/启动热路径上，改错了不影响编译但会悄悄改变判定，必须有测试钉住。
public class PerfAndProbeTests
{
    private static FrameData Frame(int w, int h, byte fill = 0)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = fill; px[i + 1] = fill; px[i + 2] = fill; px[i + 3] = 255;
        }
        return new FrameData(px, w, h, 0);
    }

    // ---------------- P1-11 静帧指纹 ----------------

    [Fact]
    public void Hash_SamePixels_SameValue()
        => Assert.Equal(ObservationService.HashBgra(Frame(64, 36, 7)),
                        ObservationService.HashBgra(Frame(64, 36, 7)));

    [Fact]
    public void Hash_ChangedPixelOnSampledRow_Differs()
    {
        var a = Frame(64, 36, 7);
        var b = Frame(64, 36, 7);
        b.Bgra[(10 * 64 + 10) * 4] = 200; // 第 10 行（偶数行，参与采样）
        Assert.NotEqual(ObservationService.HashBgra(a), ObservationService.HashBgra(b));
    }

    [Fact]
    public void Hash_OnlyOddRowChanged_IsToleratedBySampling()
    {
        // 采样策略明确是"隔行取样"：只改奇数行不改变指纹（文字/弹窗变化必然跨多行）。
        // 这个测试是记录设计口径——若将来改成逐行，这里要一起改，并重新评估热路径成本。
        var a = Frame(64, 36, 7);
        var b = Frame(64, 36, 7);
        b.Bgra[(11 * 64 + 10) * 4] = 200;
        Assert.Equal(ObservationService.HashBgra(a), ObservationService.HashBgra(b));
    }

    [Fact]
    public void Hash_DifferentSizes_Differ()
        => Assert.NotEqual(ObservationService.HashBgra(Frame(64, 36, 7)),
                           ObservationService.HashBgra(Frame(32, 18, 7)));

    [Fact]
    public void Hash_EmptyFrame_DoesNotThrow()
        => Assert.NotEqual(0ul, ObservationService.HashBgra(new FrameData(Array.Empty<byte>(), 0, 0, 0)));

    // ---------------- P0-1 输入活性文字指纹 ----------------

    private static OcrResult Ocr(params string[] words)
        => new(words.Select((t, i) => new OcrWord(t, 0.1, 0.1 + i * 0.05, 0.2, 0.04)).ToList(), 1280, 720);

    [Fact]
    public void TextSignature_IgnoresWhitespaceAndPunctuation()
        => Assert.Equal(InputLivenessProbe.TextSignature(Ocr("前往 比赛")),
                        InputLivenessProbe.TextSignature(Ocr("前往比赛！")));

    [Fact]
    public void TextSignature_ChangesWhenPageTextChanges()
        => Assert.NotEqual(InputLivenessProbe.TextSignature(Ocr("前往比赛", "取消")),
                           InputLivenessProbe.TextSignature(Ocr("前往比赛", "确认")));

    [Fact]
    public void TextSignature_EmptyForNoWords()
        => Assert.Equal("", InputLivenessProbe.TextSignature(OcrResult.Empty));

    [Fact]
    public void IsReadable_NeedsEnoughText()
    {
        Assert.False(InputLivenessProbe.IsReadable("加载中"));
        Assert.True(InputLivenessProbe.IsReadable("前往比赛取消确认下一步继续"));
    }

    // ---------------- P1-14 分段耗时 ----------------

    [Fact]
    public void PerfTrace_FirstSampleIsExact_ThenEwma()
    {
        var p = new PerfTrace();
        p.Record(frameMs: 10, hashMs: 2, ocrMs: 800, yoloMs: 40, actionMs: 350, roundMs: 1300);
        Assert.Equal(800, p.OcrMs);
        Assert.Equal(1300, p.RoundMs);
        Assert.Equal(1300 - 10 - 2 - 800 - 40 - 350, p.IdleMs);

        // 第二次：α=0.2，800 → 800 + 0.2*(0-800) = 640
        p.Record(frameMs: 10, hashMs: 2, ocrMs: 0, yoloMs: 40, actionMs: 350, roundMs: 500);
        Assert.Equal(640, p.OcrMs, 3);
        Assert.Equal(2, p.Rounds);
    }

    [Fact]
    public void PerfTrace_NeverReportsNegativeIdle()
    {
        var p = new PerfTrace();
        // 人为让各段之和超过整轮（计时取整/并发都可能出现）：其余时间必须钳到 0 而不是负数
        p.Record(10, 10, 10, 10, 10, 20);
        Assert.Equal(0, p.IdleMs);
    }

    [Fact]
    public void PerfTrace_DescribeMentionsAllSegments()
    {
        var p = new PerfTrace();
        p.Record(10, 2, 800, 40, 350, 1300);
        string s = p.Describe();
        Assert.Contains("OCR", s);
        Assert.Contains("YOLO", s);
        Assert.Contains("取帧", s);
    }

    // ---------------- P0-9 OCR 性能档 ----------------

    [Fact]
    public void OcrPerfConfig_RoundTripsThroughJson()
    {
        var cfg = new AppConfig();
        cfg.OcrPerf.DetLimitSideLength = 640;
        string json = JsonSerializer.Serialize(cfg);
        var back = JsonSerializer.Deserialize<AppConfig>(json)!;
        Assert.Equal(640, back.OcrPerf.DetLimitSideLength);
        Assert.True(back.OcrPerf.AnyEnabled);
    }

    [Fact]
    public void OcrPerfConfig_DefaultIsAllOff()
    {
        var cfg = new AppConfig();
        Assert.Equal(0, cfg.OcrPerf.DetLimitSideLength);
        Assert.False(cfg.OcrPerf.LineReread);
        Assert.False(cfg.OcrPerf.AnyEnabled);
    }

    // ---------------- P1-3 诊断 ----------------

    [Fact]
    public void PadDiagnostics_DescribeSlots_ListsFourSlots()
    {
        string s = PadDiagnostics.DescribeSlots();
        Assert.Contains("0=", s);
        Assert.Contains("3=", s);
    }
}
