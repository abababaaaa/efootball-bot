using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 焦点检测回归：7 张全高光实机截图 + i 聚焦 + 历史帧
// （赛程表高光弱 133 vs 基线 105；其余 183~200；i 图标区固定位置 165~211）
public class FocusDetectTests
{
    private readonly ITestOutputHelper _out;
    public FocusDetectTests(ITestOutputHelper output) => _out = output;

    private static string TestDir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..");

    private static (FrameData Frame, OcrResult Ocr) Load(string name)
    {
        string img = Path.Combine(TestDir, "TestData", name);
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
    [InlineData("focus_state_1.png", "前往比赛")]
    [InlineData("focus_state_2.png", "i")]           // i 聚焦：图标区高亮，文字磁贴全暗
    [InlineData("focus_state_3.png", null)]          // 赛程表聚焦亮度不可检（弱高光 133≈不可区分）→ 未检出
    [InlineData("focus_state_4.png", "兑换积分")]
    [InlineData("focus_state_5.png", "赛季纪录")]
    [InlineData("focus_state_6.png", "建议")]
    [InlineData("focus_state_7.png", "赛事信息")]
    [InlineData("purchase_flow_1.png", "前往比赛")]
    public void DetectFocus_AllStates(string file, string? expected)
    {
        var (frame, ocr) = Load(file);
        var focus = MyLeagueBrain.DetectFocusedHomeTile(frame, ocr);
        Assert.Equal(expected, focus);
    }

    // ---------------- 导航图（用户提供的焦点转移规则） ----------------

    [Theory]
    [InlineData(null, "兑换积分", PadDir.Right)]      // 未检出=赛程表：右再验证（赛程表聚焦亮度不可检）
    [InlineData("前往比赛", "兑换积分", PadDir.Down)]  // 前往比赛出发第一步必须向下进赛程表
    [InlineData("i", "兑换积分", PadDir.Down)]
    [InlineData("赛程表", "兑换积分", PadDir.Right)]    // 大概率到赛季纪录，需再验证
    [InlineData("赛季纪录", "兑换积分", PadDir.Up)]
    [InlineData("建议", "兑换积分", PadDir.Up)]         // 建议→上→赛季纪录
    [InlineData("赛事信息", "兑换积分", PadDir.Up)]     // 赛事信息→上→赛程表（下/左无路）
    [InlineData("兑换积分", "兑换积分", null)]           // 已到目标
    [InlineData(null, "前往比赛", PadDir.Up)]          // 未检出=赛程表：上
    [InlineData("i", "前往比赛", PadDir.Left)]          // i--左-->前往比赛（前往比赛--右-->i 的反向，用户 2026-10-11 确认）
    [InlineData("赛程表", "前往比赛", PadDir.Up)]
    [InlineData("兑换积分", "前往比赛", PadDir.Left)]    // 兑换积分→左→赛程表（上未确认）
    [InlineData("赛季纪录", "前往比赛", PadDir.Left)]    // 赛季纪录→左→赛程表
    [InlineData("赛事信息", "前往比赛", PadDir.Up)]      // 赛事信息→上→赛程表
    [InlineData("建议", "前往比赛", PadDir.Up)]          // 建议→上→赛季纪录
    [InlineData("前往比赛", "前往比赛", null)]
    public void PlanNav_UserGraph(string? focus, string target, PadDir? expected)
    {
        Assert.Equal(expected, MyLeagueBrain.PlanHomeNavStep(focus, target));
    }
}
