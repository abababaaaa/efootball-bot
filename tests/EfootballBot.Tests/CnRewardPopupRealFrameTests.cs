using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Bot.Scenarios.CnWeekly;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 国服周活动赛后「奖励已发放至收件箱」弹窗（用户 2026-11-25 实机截图，1280×720 内容帧）：
//   「确定」蓝色按钮 x≈0.33~0.68 / y≈0.758~0.831（中心 0.503,0.794），背后是活动主页
//   （「RT 前往比赛」「通关奖励」「奖励累计领取2/10次」都还读得到）。
//
// 这一页曾把挂机钉死 56 分钟：OCR 把弹窗的「确定」与右侧「奖励累计领取2/10次」合成一条
// 「确定奖励累计领取210次」（中心 x=0.716，已在按钮外），后台鼠标每一轮都点在按钮右侧空白上，
// 日志刷了 961 轮「奖励领取弹窗确定：第N轮·鼠标(按钮位置 0.716,0.79x)」而画面一动不动。
// 本用例锁三件事：① 这一页必须判 CwRewardResultPopup（不能落活动主页分支去按 A 前往比赛）；
// ② 鼠标落点必须在蓝色按钮里；③ 整窗帧（客户区 + 标题栏，生产环境的真实几何）同样成立。
public class CnRewardPopupRealFrameTests
{
    private readonly ITestOutputHelper _out;
    public CnRewardPopupRealFrameTests(ITestOutputHelper output) => _out = output;

    private const string File_ = "cn_reward_inbox_popup_20261125.png";

    private static string Dir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData", "cnweekly");

    private static FrameData Load(string file)
    {
        using var mat = Cv2.ImRead(file, ImreadModes.Color);
        Assert.False(mat.Empty(), $"图片加载失败: {file}");
        using var bgra = new Mat();
        Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
        var data = new byte[(int)(bgra.Total() * bgra.ElemSize())];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, data.Length);
        return new FrameData(data, mat.Width, mat.Height, 0);
    }

    /// <summary>按生产几何加边：WGC 整窗帧 = 客户区 + 左右各 1px 边框 + 顶部 32px 标题栏。</summary>
    private static FrameData Pad(FrameData src, int left, int right, int top, int bottom)
    {
        int w = src.Width + left + right, h = src.Height + top + bottom;
        var dst = new byte[w * h * 4];
        for (int y = 0; y < src.Height; y++)
            Buffer.BlockCopy(src.Bgra, y * src.Width * 4, dst, ((y + top) * w + left) * 4, src.Width * 4);
        return new FrameData(dst, w, h, 0);
    }

    private async Task<(CwScreen Cw, OcrResult Ocr, IReadOnlyList<YoloDetection> Dets)> Run(FrameData frame)
    {
        using var det = new YoloDetector(
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx"),
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt"),
            new OnnxSessionFactory("CPU"));
        using var ocr = new OcrRecognizer();
        var ocrRes = await ocr.RecognizeAsync(frame);
        var dets = det.Detect(frame);
        _out.WriteLine("---- OCR ----");
        foreach (var w in ocrRes.Words) _out.WriteLine($"  W \"{w.Text}\" @({w.CenterX:F3},{w.CenterY:F3})");
        _out.WriteLine("---- MERGED ----");
        foreach (var w in ocrRes.MergedWords) _out.WriteLine($"  M \"{w.Text}\" @({w.CenterX:F3},{w.CenterY:F3})");
        _out.WriteLine("---- DETS ----");
        foreach (var d in dets) _out.WriteLine($"  {d.ClassName} {d.Score:F2} @({d.CenterX:F3},{d.CenterY:F3})");
        return (CnWeeklyBrain.Classify(dets, ocrRes), ocrRes, dets);
    }

    [Fact]
    public async Task 实机帧_收件箱弹窗_必须判结果弹窗且鼠标落在确定按钮里()
    {
        var frame = Load(Path.Combine(Dir, File_));
        var (cw, ocr, dets) = await Run(frame);

        Assert.True(CnWeeklyBrain.IsRewardResultPopup(ocr));
        Assert.Equal(CwScreen.CwRewardResultPopup, cw);

        var hit = CnAdvance.FindButton(ocr, dets, "确定", "确认", "確定", "OK");
        Assert.NotNull(hit);
        _out.WriteLine($"FindButton = ({hit!.Value.X:F3},{hit.Value.Y:F3})");
        // 蓝色「确定」按钮实测范围（1280×720 内容帧）：x 0.33~0.68 / y 0.74~0.85
        Assert.InRange(hit.Value.X, 0.33, 0.68);
        Assert.InRange(hit.Value.Y, 0.74, 0.85);
    }

    [Fact]
    public async Task 整窗帧_收件箱弹窗_落点仍在确定按钮里()
    {
        // 生产帧是 WGC 整窗帧（1282×752）：加 1px 边框 + 32px 标题栏后归一化坐标整体下移，
        // 落点必须仍在按钮内（按钮高约 0.072，容得下这点偏移）
        var frame = Pad(Load(Path.Combine(Dir, File_)), 1, 1, 32, 0);
        var (cw, ocr, dets) = await Run(frame);

        Assert.Equal(CwScreen.CwRewardResultPopup, cw);
        var hit = CnAdvance.FindButton(ocr, dets, "确定", "确认", "確定", "OK");
        Assert.NotNull(hit);
        _out.WriteLine($"FindButton = ({hit!.Value.X:F3},{hit.Value.Y:F3})（帧 {frame.Width}×{frame.Height}）");
        Assert.InRange(hit.Value.X, 0.33, 0.68);
        Assert.InRange(hit.Value.Y, 0.70, 0.85);
    }
}
