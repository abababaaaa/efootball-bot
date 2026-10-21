using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 比赛页 hub 新 UI（2027，2026-10-21 实机帧）回归：
// 卡排 锦标赛/活动/eFootball™联赛/我的联赛（最右卡被屏幕右缘裁掉）。
// 卡死链：①美术字标题行并读成「比赛小V1100000Rank」，IsMatchHub 精确等值「比赛」恒假
// → 分类落 LeaguePage → 「联赛页无关键词」B 逃 → 主菜单重进 → 永久循环（用户实报"走到不动"）；
// ②就算认出 MatchHub，新 UI 无旧版预览区，焦点确认「我的联赛 y<0.6」恒假 → 无限右移。
// 修复：①强证据 锦标赛+争取升级至更高组别 同屏（eFootball™联赛卡描述全游戏唯一）；
// ②焦点确认放开 y 限制认磁贴行标签（未聚焦时该卡被裁只出碎片词）。
public class MatchHubNewUiRealFrameTests
{
    private readonly ITestOutputHelper _out;
    public MatchHubNewUiRealFrameTests(ITestOutputHelper output) => _out = output;

    private static FrameData LoadFrame(string name)
    {
        var data = TestFrameLoader.LoadPngBgra(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData", name), out int w, out int h);
        return new FrameData(data, w, h, 0);
    }

    [Fact]
    public void NewUiHub_ClassifiesMatchHub_EvenWithoutYoloDets()
    {
        // 无 YOLO 检测（空 dets）也要正判——比生产环境（有 btn_back 框）更严
        var frame = LoadFrame("matchhub_new_ui_20261021.jpg");
        using var ocr = new OcrRecognizer(OcrEngineKind.SimdPaddle, preferInt8: false);
        var res = TestOcr.Run(ocr, frame);

        var ml = MyLeagueBrain.Classify(
            new System.Collections.Generic.List<EfootballBot.Core.Vision.Yolo.YoloDetection>(), res);
        Assert.Equal(MlScreen.MatchHub, ml);
    }

    [Fact]
    public void NewUiHub_UnfocusedCard_RightShiftNotConfirm()
    {
        // 未聚焦时最右 我的联赛 卡被裁掉（只读出 我/自 碎片）→ 必须右移，不得直接按 A
        var frame = LoadFrame("matchhub_new_ui_20261021.jpg");
        using var ocr = new OcrRecognizer(OcrEngineKind.SimdPaddle, preferInt8: false);
        var res = TestOcr.Run(ocr, frame);

        var act = MyLeagueBrain.Decide(MlScreen.MatchHub, res, screenStreak: 0);
        Assert.Equal(MlActionKind.PadMoveRight, act.Kind);
    }

    [Fact]
    public void OldUiHub_StillClassifiesMatchHub()
    {
        // 旧 UI 实机帧不回归：磁贴词 + 精确标题路径仍有效
        var frame = LoadFrame("matchhub_user_1.png");
        using var ocr = new OcrRecognizer(OcrEngineKind.SimdPaddle, preferInt8: false);
        var res = TestOcr.Run(ocr, frame);

        var ml = MyLeagueBrain.Classify(
            new System.Collections.Generic.List<EfootballBot.Core.Vision.Yolo.YoloDetection>(), res);
        Assert.Equal(MlScreen.MatchHub, ml);
    }
}
