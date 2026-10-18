using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 国服比赛「跳过比赛✅」三实机帧回归（2026-10-17 用户截图，1280×720）：
// ① 比赛中右上角横幅（跳过比赛+绿勾）→ 仍判 InMatch，Decide 按暂停键（PadStartPress）；
// ② 暂停菜单（跳过比赛[聚焦]+对局获胜绿字/比赛回放/重新开始/放弃）→ CnMatchPause，
//    有「对局获胜」小字 → A 跳过（默认光标即在跳过比赛上）；
// ③ 确认弹窗（确认跳过/关闭，焦点默认在关闭，盖在菜单上）→ 仍判 CnMatchPause，
//    Decide 上移点「确认跳过」——盲按 A 会按在关闭上永远进不去。
// ★暂停菜单含「比赛回放」词，必须先于 比赛回放→HasConfirm 弹窗判定，否则死循环。
public class CnMatchSkipRealFrameTests
{
    private readonly ITestOutputHelper _out;
    public CnMatchSkipRealFrameTests(ITestOutputHelper output) => _out = output;

    private static string CnDir => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestData", "cnmatch");

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

    [Fact]
    public async Task SkipBanner_Frame_InMatch_TriggersStart()
    {
        using var ocr = new OcrRecognizer();
        var data = LoadPngBgra(Path.Combine(CnDir, "cn_skip_banner.png"), out int w, out int h);
        var frame = new FrameData(data, w, h, 0);
        var ocrRes = await ocr.RecognizeAsync(frame);

        var screen = MyLeagueBrain.Classify(Array.Empty<EfootballBot.Core.Vision.Yolo.YoloDetection>(), ocrRes);
        var action = MyLeagueBrain.Decide(screen, ocrRes, 2, frame: frame);

        _out.WriteLine($"screen={screen} action={action.Kind} ({action.Target})");
        foreach (var wd in ocrRes.Words)
            _out.WriteLine($"  W \"{wd.Text}\" @({wd.CenterX:F2},{wd.CenterY:F2})");

        Assert.Equal(MlScreen.InMatch, screen);
        Assert.Equal(MlActionKind.PadStartPress, action.Kind);
    }

    // 实机干扰项：同位置「跳过比赛/累计进球0/3个或以上」目标提示 toast（无绿勾）——
    // 不是可跳横幅，仍判 InMatch 继续挂机，绝不空开暂停菜单
    [Fact]
    public async Task GoalToast_Frame_NotSkippable()
    {
        using var ocr = new OcrRecognizer();
        var data = LoadPngBgra(Path.Combine(CnDir, "cn_skip_goal_toast.png"), out int w, out int h);
        var frame = new FrameData(data, w, h, 0);
        var ocrRes = await ocr.RecognizeAsync(frame);

        var screen = MyLeagueBrain.Classify(Array.Empty<EfootballBot.Core.Vision.Yolo.YoloDetection>(), ocrRes);
        var action = MyLeagueBrain.Decide(screen, ocrRes, 2, frame: frame);

        _out.WriteLine($"screen={screen} action={action.Kind} toast={ocrRes.ContainsJoined("累计进球")}");
        foreach (var wd in ocrRes.Words)
            _out.WriteLine($"  W \"{wd.Text}\" @({wd.CenterX:F2},{wd.CenterY:F2})");

        Assert.Equal(MlScreen.InMatch, screen);
        Assert.Equal(MlActionKind.MatchIdleTap, action.Kind);
    }

    [Fact]
    public async Task PauseMenu_Frame_ClassifyAndSkipWhenWinning()
    {
        using var ocr = new OcrRecognizer();
        var data = LoadPngBgra(Path.Combine(CnDir, "cn_pause_menu.png"), out int w, out int h);
        var ocrRes = await ocr.RecognizeAsync(new FrameData(data, w, h, 0));

        var screen = MyLeagueBrain.Classify(Array.Empty<EfootballBot.Core.Vision.Yolo.YoloDetection>(), ocrRes);
        var action = MyLeagueBrain.Decide(screen, ocrRes, 1);

        _out.WriteLine($"screen={screen} action={action.Kind} ({action.Target}) " +
                       $"win={ocrRes.ContainsJoined("对局获胜")}");
        foreach (var wd in ocrRes.Words)
            _out.WriteLine($"  W \"{wd.Text}\" @({wd.CenterX:F2},{wd.CenterY:F2})");

        Assert.Equal(MlScreen.CnMatchPause, screen);
        Assert.True(ocrRes.ContainsJoined("对局获胜"), "菜单应读到绿色小字 对局获胜");
        Assert.Equal(MlActionKind.PadConfirm, action.Kind);
    }

    [Fact]
    public async Task SkipPopup_Frame_ClassifyAndUpConfirm()
    {
        using var ocr = new OcrRecognizer();
        var data = LoadPngBgra(Path.Combine(CnDir, "cn_skip_popup.png"), out int w, out int h);
        var ocrRes = await ocr.RecognizeAsync(new FrameData(data, w, h, 0));

        var screen = MyLeagueBrain.Classify(Array.Empty<EfootballBot.Core.Vision.Yolo.YoloDetection>(), ocrRes);
        var action = MyLeagueBrain.Decide(screen, ocrRes, 1);

        _out.WriteLine($"screen={screen} action={action.Kind} ({action.Target})");
        foreach (var wd in ocrRes.Words)
            _out.WriteLine($"  W \"{wd.Text}\" @({wd.CenterX:F2},{wd.CenterY:F2})");

        Assert.Equal(MlScreen.CnMatchPause, screen);
        Assert.Equal(MlActionKind.PadUpConfirm, action.Kind);
    }
}
