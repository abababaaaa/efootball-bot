using EfootballBot.Core.Capture;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Vision;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 分析：我的联赛道具页截图 → OCR（提取官方道具名称与行结构）
public class MyLeagueItemsAnalysisTests
{
    private readonly ITestOutputHelper _out;
    public MyLeagueItemsAnalysisTests(ITestOutputHelper output) => _out = output;

    private static string TestDir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..");

    [Fact]
    public void Analyze_Items_Page()
    {
        string img = Path.Combine(TestDir, "TestData", "myleague_items_user_1.png");
        if (!File.Exists(img)) { _out.WriteLine("no fixture, skip"); return; }
        using var mat = Cv2.ImRead(img, ImreadModes.Color);
        Assert.False(mat.Empty(), $"图片加载失败: {img}");
        using var bgra = new Mat();
        Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
        int byteCount = (int)(bgra.Total() * bgra.ElemSize());
        var data = new byte[byteCount];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, byteCount);
        var frame = new FrameData(data, mat.Width, mat.Height, 0);

        using var ocr = new OcrRecognizer();
        var ocrRes = ocr.RecognizeAsync(frame).GetAwaiter().GetResult();

        _out.WriteLine($"==== myleague_items_user_1.png ({mat.Width}x{mat.Height}) ====");
        _out.WriteLine("-- OCR words --");
        foreach (var w in ocrRes.Words)
            _out.WriteLine($"  \"{w.Text}\" @({w.CenterX:F3},{w.CenterY:F3})");
        _out.WriteLine("-- OCR lines --");
        foreach (var l in ocrRes.Lines)
            _out.WriteLine($"  \"{l.Text}\" @({l.CenterX:F3},{l.CenterY:F3})");
    }
}
