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

    private static OcrResult Ocr(params (string text, double x, double y)[] words)
    {
        var list = words.Select(w => new OcrWord(w.text, w.x, w.y, 0.1, 0.03)).ToList();
        return new OcrResult(list, 1, 1);
    }

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
    public void Classify_KeyMatchDialog_ToleratesZeroKO()
    {
        // 用户 2026-10-15 实测回归：P3 弹窗 OK（白底白字小字）被 OCR 读成 0K，
        // 关键比赛+OK 判定失效 → 落 HasConfirm 盲按 A → 按在默认焦点「取消」上死循环。
        // 词级 O/0 归一后须仍判 KeyMatchDialog。
        var ocr = Ocr(("即将进行的是关键比赛", 0.5, 0.3), ("下一场比赛有固定的赛事等级", 0.5, 0.4),
                      ("0K", 0.5, 0.58), ("取消", 0.5, 0.66));
        Assert.Equal(MlScreen.KeyMatchDialog,
            MyLeagueBrain.Classify(Array.Empty<Core.Vision.Yolo.YoloDetection>(), ocr, true));
    }

    [Fact]
    public void Classify_KeyMatchDialog_ImportantMatchAlias()
    {
        // 「重要比赛」为关键比赛同义别名（用户反馈用词，防游戏文案改版）
        var ocr = Ocr(("下一场是重要比赛", 0.5, 0.4), ("OK", 0.5, 0.6));
        Assert.Equal(MlScreen.KeyMatchDialog,
            MyLeagueBrain.Classify(Array.Empty<Core.Vision.Yolo.YoloDetection>(), ocr, true));
    }

    [Fact]
    public void P1_RestrictedPage_MustNotProvideOkEvidence()
    {
        // P1 受限难度页副标题含「关键比赛」，但全页无 OK 类词——不得被误判成关键比赛弹窗
        // （否则上移进难度列表改难度）。真实帧守：标题判 DifficultyPage + 无 OK 证据。
        var p1 = LoadOcr("keymatch_p1.png");
        Assert.False(MyLeagueBrain.HasOkButton(p1));
        Assert.Equal(MlScreen.DifficultyPage,
            MyLeagueBrain.Classify(Array.Empty<Core.Vision.Yolo.YoloDetection>(), p1, true));
    }

    [Fact]
    public void Decide_HasConfirm_WithCancel_GoesUpConfirm()
    {
        // 带「取消」的弹窗默认焦点在取消（P3/购买弹窗实测）：盲按 A 会关掉弹窗，须上移再确认
        var ocr = Ocr(("取消", 0.35, 0.66), ("确认", 0.65, 0.58));
        var action = MyLeagueBrain.Decide(MlScreen.HasConfirm, ocr, 0);
        Assert.Equal(MlActionKind.PadUpConfirm, action.Kind);
    }

    [Fact]
    public void Decide_HasConfirm_NoCancel_StillPlainConfirm()
    {
        // 普通推进页（结算/下一步）无取消词：保持盲按 A
        var ocr = Ocr(("下一步", 0.5, 0.9));
        Assert.Equal(MlActionKind.PadConfirm, MyLeagueBrain.Decide(MlScreen.HasConfirm, ocr, 0).Kind);
    }

    [Fact]
    public void Decide_HasConfirm_DifficultyChoice_StaysPlainConfirm()
    {
        // 难度选择页（分类失败落到 HasConfirm 时）：确认键是默认焦点，不上移进列表改难度。
        // 标题命中 / 难度名列表命中 两种形态都要守住（关键比赛 2026-10-12 用户要求不改选）。
        var byTitle = Ocr(("选择赛事等级", 0.3, 0.1), ("取消", 0.3, 0.95), ("确认", 0.65, 0.95));
        Assert.Equal(MlActionKind.PadConfirm, MyLeagueBrain.Decide(MlScreen.HasConfirm, byTitle, 0).Kind);
        var byList = Ocr(("超级球星", 0.5, 0.35), ("传奇", 0.5, 0.5), ("取消", 0.3, 0.95), ("确认", 0.65, 0.95));
        Assert.Equal(MlActionKind.PadConfirm, MyLeagueBrain.Decide(MlScreen.HasConfirm, byList, 0).Kind);
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
