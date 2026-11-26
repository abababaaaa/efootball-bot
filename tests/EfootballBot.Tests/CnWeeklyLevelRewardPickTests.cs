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

// 「特殊奖励」单卡版页面（用户 2026-11-25 实机，晋级挑战赛后）：
//   标题「特殊奖励」+ 三块面板「前场球员 / 中场球员 / 后场球员」，每块一张球员卡
//   （卡面左上角总评 84 / 80 / 81，卡底评级字母 C / A / B）+ 底栏「选择一个阵容强化奖励」+ RT 下一步。
//
// 事故：旧判据要求卡底有「等级提升」，这页没有 → 判 None → 共享层当 LeagueInfoPage，
// 鼠标在「下一步」上连点三千多次、卡了近两小时。
// 规则（用户 2026-11-25 定）：单个球员也先看**评分**，同评分按 前锋 > 中场 > 后场。
public class CnWeeklyLevelRewardPickTests
{
    private readonly ITestOutputHelper _out;
    public CnWeeklyLevelRewardPickTests(ITestOutputHelper output) => _out = output;

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

    private static async Task<(CwScreen Cw, OcrResult Ocr, FrameData Frame)> Run(string file)
    {
        using var det = new YoloDetector(
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx"),
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt"),
            new OnnxSessionFactory("CPU"));
        using var ocr = new OcrRecognizer();
        var frame = Load(Path.Combine(Dir, file));
        var ocrRes = await ocr.RecognizeAsync(frame);
        return (CnWeeklyBrain.Classify(det.Detect(frame), ocrRes), ocrRes, frame);
    }

    // ① 这一页必须被认成「特殊奖励」（单卡版），不能再落到 None 交给共享层。
    [Fact]
    public async Task SingleCardLevelPage_IsRecognized()
    {
        var (cw, ocr, _) = await Run("cn_level_pick_20261125.png");
        _out.WriteLine($"CwScreen={cw} IsLevelRewardPick={CnWeeklyBrain.IsLevelRewardPick(ocr)} " +
                       $"IsSquadRewardPick={CnWeeklyBrain.IsSquadRewardPick(ocr)}");
        Assert.Equal(CwScreen.LevelRewardPick, cw);
        Assert.False(CnWeeklyBrain.IsSquadRewardPick(ocr), "单卡版没有「捆绑包」，不能被六卡版判据抢走");
    }

    // ② 三块面板的球员总评都要读到（实机 84 / 80 / 81）。
    [Fact]
    public async Task CardRatings_AllRead()
    {
        var (_, ocr, _) = await Run("cn_level_pick_20261125.png");
        var panels = CnWeeklyBrain.Panels(ocr, "球员");
        foreach (var (i, p) in panels.Select((p, i) => (i, p)))
            _out.WriteLine($"面板{i} x={p.X:F3} 总评={CnWeeklyBrain.ReadCardRating(ocr, p.X)?.ToString() ?? "?"}");
        // 面板顺序：前锋 / 中场 / 后场
        Assert.Equal(84, CnWeeklyBrain.ReadCardRating(ocr, panels[0].X));
        Assert.Equal(80, CnWeeklyBrain.ReadCardRating(ocr, panels[1].X));
        Assert.Equal(81, CnWeeklyBrain.ReadCardRating(ocr, panels[2].X));
    }

    // ③ 选择规则：按总评最高选 = 前场（84），不是评级字母最高的中场（A）。
    [Fact]
    public async Task PicksHighestRating_Forward()
    {
        var (_, ocr, _) = await Run("cn_level_pick_20261125.png");
        var choice = CnWeeklyBrain.PlanLevelReward(ocr);
        Assert.NotNull(choice);
        _out.WriteLine($"选 {choice!.Slot} 值={choice.Value} 完整={choice.Complete} 明细={choice.Detail}");
        Assert.Equal(CwRewardSlot.Forward, choice.Slot);   // 84 > 81 > 80
        Assert.Equal(84, choice.Value);
        Assert.True(choice.Complete, "三块总评都读到了，应当是完整结论");
    }

    // ④ 同评分优先级：把三个数字都改成 84（合成 OCR），仍必须选前锋（下标最小）。
    [Fact]
    public void SameRating_FallsBackToForwardPriority()
    {
        var panels = new[] { 0.221, 0.499, 0.777 };
        var words = new List<OcrWord>();
        for (int i = 0; i < 3; i++)
            words.Add(new OcrWord("84", panels[i] - 0.031, 0.380, 0.026, 0.033));
        // 面板标题（Panels 定位用）
        words.Add(new OcrWord("前场球员", 0.180, 0.252, 0.082, 0.046));
        words.Add(new OcrWord("中场球员", 0.459, 0.252, 0.082, 0.046));
        words.Add(new OcrWord("后场球员", 0.737, 0.252, 0.080, 0.046));
        var ocr = new OcrResult(words, 1280, 720);

        var choice = CnWeeklyBrain.PlanLevelReward(ocr);
        Assert.NotNull(choice);
        _out.WriteLine($"同分 → 选 {choice!.Slot} 值={choice.Value} 明细={choice.Detail}");
        Assert.Equal(CwRewardSlot.Forward, choice.Slot);
    }
}
