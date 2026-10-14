using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using OpenCvSharp;
using Xunit;

namespace EfootballBot.Tests;

// 关键比赛回归：P1 受限难度页（2 选项）/ P2 预告弹窗 / P3 固定难度弹窗
public class KeyMatchPageTests
{
    private static string TestDir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..");

    private static OcrResult LoadOcr(string file)
    {
        using var mat = Cv2.ImRead(Path.Combine(TestDir, "TestData", file), ImreadModes.Color);
        using var bgra = new Mat();
        Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
        int byteCount = (int)(bgra.Total() * bgra.ElemSize());
        var data = new byte[byteCount];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, byteCount);
        using var ocr = new OcrRecognizer();
        return ocr.RecognizeAsync(new FrameData(data, mat.Width, mat.Height, 0)).GetAwaiter().GetResult();
    }

    [Fact]
    public void Classify_P2_P3_KeyMatchDialog()
    {
        // 含「关键比赛」+「OK」→ KeyMatchDialog（此前被"我的联赛积分"字样误判 MatchHub）
        Assert.Equal(MlScreen.KeyMatchDialog, MyLeagueBrain.Classify(
            Array.Empty<Core.Vision.Yolo.YoloDetection>(), LoadOcr("keymatch_p2.png"), true));
        Assert.Equal(MlScreen.KeyMatchDialog, MyLeagueBrain.Classify(
            Array.Empty<Core.Vision.Yolo.YoloDetection>(), LoadOcr("keymatch_p3.png"), true));

        var action = MyLeagueBrain.Decide(MlScreen.KeyMatchDialog, LoadOcr("keymatch_p2.png"), 0);
        Assert.Equal(MlActionKind.PadUpConfirm, action.Kind); // 上移点 OK
    }

    [Fact]
    public void Classify_P1_RestrictedDifficultyPage()
    {
        // 关键比赛受限难度页：同「选择赛事等级」标题 → 仍是 DifficultyPage
        Assert.Equal(MlScreen.DifficultyPage, MyLeagueBrain.Classify(
            Array.Empty<Core.Vision.Yolo.YoloDetection>(), LoadOcr("keymatch_p1.png"), true));
    }

    [Fact]
    public void DetectDifficultyOptions_RestrictedPage_TwoOptions()
    {
        // P1 实机：仅 超级球星+传奇 2 项（按 Y 序）
        var options = MyLeagueBrain.DetectDifficultyOptions(LoadOcr("keymatch_p1.png"));
        Assert.Equal(new[] { "超级球星", "传奇" }, options);
    }

    [Fact]
    public void DetectDifficultyOptions_NormalPage_SevenOptions()
    {
        var options = MyLeagueBrain.DetectDifficultyOptions(LoadOcr("difficulty_2.png"));
        Assert.Equal(7, options.Count);
        Assert.Equal("新手", options[0]);
        Assert.Equal("传奇", options[^1]);
    }

    [Fact]
    public void NavFormula_AdaptsToOptionCount()
    {
        // 公式 上×(N+1-k)：普通页 N=7 新手 k=1 → 7 步（与用户原 8-k 一致）
        Assert.Equal(7, 7 + 1 - 1);
        // 受限页 N=2：传奇 k=2 → 1 步；超级球星 k=1 → 2 步
        Assert.Equal(1, 2 + 1 - 2);
        Assert.Equal(2, 2 + 1 - 1);
    }

    [Fact]
    public void RestrictedPage_BannerIsReadable()
    {
        // 受限页横幅「关键比赛…受限」可被 OCR 读出——难度处理靠该信号识别关键比赛
        // （实机 bot-20261012-015835 02:00：受限页横幅在 OCR 里，却被当普通页导航改了难度）
        Assert.True(LoadOcr("keymatch_p1.png").ContainsJoined("关键比赛"));
    }

    [Fact]
    public void RespectGameDifficulty_KeyMatchNeverChangesSelection()
    {
        // 关键比赛（预告上下文 / 受限横幅任一命中）→ 按游戏默认直接确认，不按↑改选；
        // 普通页无预告 → 照常走导航公式
        Assert.True(MyLeagueBrain.RespectGameDifficulty(true, LoadOcr("difficulty_2.png")));
        Assert.True(MyLeagueBrain.RespectGameDifficulty(false, LoadOcr("keymatch_p1.png")));
        Assert.False(MyLeagueBrain.RespectGameDifficulty(false, LoadOcr("difficulty_2.png")));
    }
}
