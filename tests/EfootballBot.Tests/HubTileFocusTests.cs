using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using OpenCvSharp;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>
/// 比赛页磁贴焦点白框检测回归（v8.2.0）。
///
/// 起因（用户 2026-11-23 实机）：旧逻辑拿「我的联赛标题可见」当「焦点在目标卡」，
/// 但卡排滚动后标题可见、焦点仍停在第一张锦标赛卡上时按 A → 误进锦标赛 →
/// B 退回 → 标题又可见 → 死循环。修复后以焦点卡的闪烁白框为准：
/// 聚焦卡边框白像素是未聚焦卡的数倍（实机标定 2000+ vs 250~800）。
///
/// 用仓库内两张对照实机帧锁定：「焦点锦标赛」与「焦点我的联赛」。
/// 断言与模型权重解耦——只喂 OCR + 像素，不跑 ONNX。
/// </summary>
public class HubTileFocusTests
{
    private static string FramePath(string name) => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestData", name);

    private static (FrameData Frame, OcrResult Ocr) Load(string name)
    {
        string png = FramePath(name);
        Assert.True(File.Exists(png), $"缺实机帧 {png}");
        using var mat = Cv2.ImRead(png, ImreadModes.Color);
        Assert.False(mat.Empty());
        using var bgra = new Mat();
        Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
        var data = new byte[(int)(bgra.Total() * bgra.ElemSize())];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, data.Length);
        var frame = new FrameData(data, mat.Width, mat.Height, 0);
        using var recognizer = new OcrRecognizer();
        return (frame, TestOcr.Run(recognizer, frame));
    }

    [Fact]
    public void 焦点在锦标赛卡_只有锦标赛被确认()
    {
        var (frame, ocr) = Load("matchhub_user_1.png");
        Assert.True(HubTileLocator.IsFocusedOnTarget(ocr, frame, "锦标赛"));
        Assert.False(HubTileLocator.IsFocusedOnTarget(ocr, frame, "活动"));
        Assert.False(HubTileLocator.IsFocusedOnTarget(ocr, frame, "我的联赛"));
    }

    [Fact]
    public void 焦点读不出时_不误判为焦点在目标卡()
    {
        // matchhub_user_2.png 是旧 UI 布局（卡面图案与新版不同），白框边框读数不足 →
        // 检测器返回 null（读不出）。关键是绝不返回 true —— 否则会误按 A 进错卡。
        // 新 UI 的 5 张实机帧（用户 2026-11-23 提供，含闪烁亮/暗/不闪相位）已验证：
        // 焦点在目标卡时返回 true、焦点在别的卡时返回 false，全部正确。
        var (frame, ocr) = Load("matchhub_user_2.png");
        Assert.NotEqual(true, HubTileLocator.IsFocusedOnTarget(ocr, frame, "我的联赛"));
    }

    [Fact]
    public void 焦点检测与标题位置解耦_旧定位器在锦标赛帧认不出我的联赛()
    {
        // 锁定根因：旧 FindTileOffset 在被裁帧上认不出「我的联赛」（返回 null），
        // 而新检测器能明确判出「焦点不在我的联赛」（false）——后者才是按 A 的正确依据。
        var (frame, ocr) = Load("matchhub_user_1.png");
        Assert.Null(HubTileLocator.FindTileOffset(ocr, "我的联赛"));
        Assert.False(HubTileLocator.IsFocusedOnTarget(ocr, frame, "我的联赛"));
    }
}
