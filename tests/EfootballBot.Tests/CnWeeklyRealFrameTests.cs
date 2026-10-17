using EfootballBot.Core.Bot.Scenarios.CnWeekly;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Vision;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 校准/回归：用户提供的国服周活动 11 张截图（2026-10-13）→ OCR dump + CnWeeklyBrain 分类。
// 首次运行用于看清 OCR 实际读出的词与坐标（固化分类规则的依据）；
// 之后作为回归锁定（各帧分类不得回退）。国服分类为纯 OCR，不依赖 YOLO。
public class CnWeeklyRealFrameTests
{
    private readonly ITestOutputHelper _out;
    public CnWeeklyRealFrameTests(ITestOutputHelper output) => _out = output;

    private static string TestDir => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..");

    private static string CnDir => Path.Combine(TestDir, "TestData", "cnweekly");

    private static byte[] LoadPngBgra(string file, out int w, out int h)
    {
        using var mat = Cv2.ImRead(file, ImreadModes.Color);
        Assert.False(mat.Empty(), $"图片加载失败: {file}");
        using var bgra = new Mat();
        Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
        int byteCount = (int)(bgra.Total() * bgra.ElemSize());
        var data = new byte[byteCount];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, byteCount);
        w = mat.Width; h = mat.Height;
        return data;
    }

    private static readonly (string File, CwScreen Expected)[] Frames =
    {
        ("cn_home_recommended.png", CwScreen.HomeRecommended),
        ("cn_home_recommended_focus.png", CwScreen.HomeRecommended),
        ("cn_home_match.png", CwScreen.HomeMatch),
        ("cn_events_list.png", CwScreen.EventsList),
        ("cn_event_intro.png", CwScreen.EventIntro),
        ("cn_bracket.png", CwScreen.Bracket),
        ("cn_bracket_2.png", CwScreen.Bracket),
        ("cn_event_home.png", CwScreen.EventHome),
        ("cn_squad.png", CwScreen.Squad),
        ("cn_squad_menu.png", CwScreen.SquadMenu),
        ("cn_squad_menu_cursor.png", CwScreen.SquadMenu),
        ("cn_tour_event_settings.png", CwScreen.EventSettings),
    };

    [Fact]
    public async Task Dump_And_Classify_Cn_Frames()
    {
        using var ocr = new OcrRecognizer();
        var failures = new List<string>();

        foreach (var (name, expected) in Frames)
        {
            var path = Path.Combine(CnDir, name);
            if (!File.Exists(path)) { _out.WriteLine($"==== {name}: missing, skip ===="); continue; }
            var data = LoadPngBgra(path, out int w, out int h);
            var frame = new FrameData(data, w, h, 0);
            var ocrRes = await ocr.RecognizeAsync(frame);
            var screen = CnWeeklyBrain.Classify(Array.Empty<EfootballBot.Core.Vision.Yolo.YoloDetection>(), ocrRes);

            _out.WriteLine($"==== {name} -> {screen} (期望 {expected}) ====");
            foreach (var l in ocrRes.Lines)
                _out.WriteLine($"  L \"{l.Text}\" @({l.CenterX:F2},{l.CenterY:F2})");
            foreach (var wd in ocrRes.Words)
                _out.WriteLine($"  W \"{wd.Text}\" @({wd.CenterX:F2},{wd.CenterY:F2})");

            if (screen != expected)
                failures.Add($"{name}: 判定 {screen}，期望 {expected}");
        }

        Assert.True(failures.Count == 0,
            "国服周活动实机帧分类回归失败：\n" + string.Join("\n", failures));
    }

    // 实机 2026-10-17：球星训练·巡回赛 活动设置面板（教练模式=开 + 教练模式内控制=完全托管，
    // 主页词透暗背景同屏）。固化：面板判 EventSettings（先于巡回赛主页/杯赛型主页）+
    // TourSettingsReady=true。OCR 词与坐标全部打印，判定偏差时可直接据此调参。
    // 主页关闭面板的实机帧暂缺，巡回赛主页分类由 CnWeeklyBrainTests 合成帧锁定。
    [Fact]
    public async Task TourEventSettings_Frame_ClassifyAndReady()
    {
        using var ocr = new OcrRecognizer();
        string img = Path.Combine(CnDir, "cn_tour_event_settings.png");
        var data = LoadPngBgra(img, out int w, out int h);
        var frame = new FrameData(data, w, h, 0);
        var ocrRes = await ocr.RecognizeAsync(frame);

        var screen = CnWeeklyBrain.Classify(
            Array.Empty<EfootballBot.Core.Vision.Yolo.YoloDetection>(), ocrRes);
        bool ready = CnWeeklyBrain.TourSettingsReady(ocrRes);

        _out.WriteLine($"screen={screen} tourSettingsReady={ready}");
        foreach (var l in ocrRes.Lines)
            _out.WriteLine($"  L \"{l.Text}\" @({l.CenterX:F2},{l.CenterY:F2})");
        foreach (var wd in ocrRes.Words)
            _out.WriteLine($"  W \"{wd.Text}\" @({wd.CenterX:F2},{wd.CenterY:F2})");

        Assert.Equal(CwScreen.EventSettings, screen);
        Assert.True(ready, "应核验通过：教练模式=开 + 完全托管");
    }
}
