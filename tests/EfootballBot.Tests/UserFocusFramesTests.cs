using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using OpenCvSharp;
using Xunit;

namespace EfootballBot.Tests;

// 用户实机逐磁贴标定帧回归（2026-10-11 晚，1280×720 国际服）：
// 关键发现——赛程表恒亮 ≈182（未读赛程内容自带高亮 ≈ 聚焦亮度），聚焦与否不可辨，
// 已改为"赛程表不参与聚焦竞争，聚焦时按未检出处理"。其余磁贴聚焦 199~207 / 未聚焦 78~89。
// 每帧期望值 = 用户截图时光标所在磁贴（c4dcbd 光标在赛程表 → 期望 null）。
public class UserFocusFramesTests
{
    private static (FrameData Frame, OcrResult Ocr) Load(string name)
    {
        string img = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData", name);
        using var mat = Cv2.ImRead(img, ImreadModes.Color);
        using var bgra = new Mat();
        Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
        int byteCount = (int)(bgra.Total() * bgra.ElemSize());
        var data = new byte[byteCount];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, byteCount);
        var frame = new FrameData(data, mat.Width, mat.Height, 0);
        using var ocr = new OcrRecognizer();
        return (frame, TestOcr.Run(ocr, frame));
    }

    [Theory]
    [InlineData("user_focus_go.png", "前往比赛")]        // 前往比赛=207
    [InlineData("user_focus_points.png", "兑换积分")]
    [InlineData("user_focus_season.png", "赛季纪录")]    // 赛季纪录=199
    [InlineData("user_focus_info.png", "赛事信息")]      // 赛事信息=202
    [InlineData("user_focus_advice.png", "建议")]
    [InlineData("user_focus_i.png", "i")]                // i 图标聚焦：锚定区亮 208
    [InlineData("user_focus_schedule.png", null)]        // 赛程表聚焦不可辨（恒亮 182）→ 未检出
    public void DetectFocus_UserCalibrationFrames(string file, string? expected)
    {
        var (frame, ocr) = Load(file);
        var focus = MyLeagueBrain.DetectFocusedHomeTile(frame, ocr);
        Assert.Equal(expected, focus);
    }
}
