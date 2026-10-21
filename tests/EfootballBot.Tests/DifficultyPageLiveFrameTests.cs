using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 难度页实机帧回归（2026-10-20 反馈包两次卡死的帧）+ 确认腿计划。
// 卡死链：旧宽窗把光标蓝框读成蓝勾 → 判定在勾行/光标行间抖动 → 旧舞步基于「光标在底行
// 确认键」的错误前提裸按 A（实机列表行按 A 是空操作）→ 页面永不开赛。
// 修复模型（用户实机配方）：确认唯一路径 = 下×10 饱和落底行 → 右×1 → A；
// 蓝勾用 DetectCheckedDifficulty 窄窗（勾行 22~24 票、光标行 0 票）只记录不导航。
public class DifficultyPageLiveFrameTests
{
    private readonly ITestOutputHelper _out;
    public DifficultyPageLiveFrameTests(ITestOutputHelper output) => _out = output;

    private static string TestDir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData");

    private static FrameData LoadFrame(string name)
    {
        var data = TestFrameLoader.LoadPngBgra(Path.Combine(TestDir, name), out int w, out int h);
        return new FrameData(data, w, h, 0);
    }

    [Theory]
    [InlineData("difficulty_new_ui.png")]        // 12:10 会话帧（1280 宽）
    [InlineData("difficulty_live_20261020.png")] // 18:21 会话帧（1227 宽，蓝框横线入带挑战帧）
    public void CheckedDifficulty_NarrowWindow_ReadsLegendNotFocusedRow(string file)
    {
        var frame = LoadFrame(file);
        using var ocr = new OcrRecognizer();
        var ocrRes = TestOcr.Run(ocr, frame);

        // 两张帧蓝勾都在传奇、光标都在新手（用户备注：光标在新手上）——窄窗必须读出传奇
        Assert.Equal("传奇", MyLeagueBrain.DetectCheckedDifficulty(frame, ocrRes));

        // 存档旧宽窗行为作校准基准：18:21 帧上旧检测确实翻成新手（抖动实证）。
        // 若将来调整宽窗实现，先重跑 DifficultyNewUiCalibrationTests 对照两帧行为再改此处。
        var legacy = MyLeagueBrain.DetectSelectedDifficulty(frame, ocrRes);
        _out.WriteLine($"{file}: DetectSelectedDifficulty(旧宽窗)={legacy ?? "null"}, DetectCheckedDifficulty(窄窗)=传奇");
        if (file == "difficulty_live_20261020.png")
            Assert.Equal("新手", legacy);
    }

    [Fact]
    public void DifficultyLegPlan_MatchesUserRecipe()
    {
        // 首轮 = 纯 A（2026-10-21 实机视频：对话框光标可能直接落在「确认」上——
        // 光标位置是游戏记忆的；确认已聚焦时 A 直接开赛，列表行上 A 是空操作无副作用）
        Assert.Equal((false, 0, 0), MyLeagueBrain.DifficultyLegPlan(1));
        // 重试腿先上×9 饱和回顶行，下×10 落底行；偶数轮右×2 防吞键
        Assert.Equal((true, 10, 2), MyLeagueBrain.DifficultyLegPlan(2));
        Assert.Equal((true, 10, 1), MyLeagueBrain.DifficultyLegPlan(3));
        // 第 4/7 轮 B 逃生（关对话框重进）；第 10 轮起调用方停止挂机
        Assert.Null(MyLeagueBrain.DifficultyLegPlan(4));
        Assert.Equal((true, 10, 1), MyLeagueBrain.DifficultyLegPlan(5));
        Assert.Equal((true, 10, 2), MyLeagueBrain.DifficultyLegPlan(6));
        Assert.Null(MyLeagueBrain.DifficultyLegPlan(7));
        Assert.Equal((true, 10, 1), MyLeagueBrain.DifficultyLegPlan(9));
        Assert.Null(MyLeagueBrain.DifficultyLegPlan(10));
    }

    [Theory]
    [InlineData("difficulty_2.png")]
    [InlineData("difficulty_3.png")]
    [InlineData("difficulty_4.png")]
    public void CheckedDifficulty_ManualScreens_Informational(string file)
    {
        // 手动实截与后台停靠捕获存在几何差异（3.1.0.4 已知疑点）：此处只 dump 窄窗读数
        // 供对照，不作断言——生产判定只见实机捕获帧，以上两帧锁回归。
        var frame = LoadFrame(file);
        using var ocr = new OcrRecognizer();
        var ocrRes = TestOcr.Run(ocr, frame);
        _out.WriteLine($"{file}: DetectCheckedDifficulty={MyLeagueBrain.DetectCheckedDifficulty(frame, ocrRes) ?? "null"}, " +
                       $"DetectSelectedDifficulty={MyLeagueBrain.DetectSelectedDifficulty(frame, ocrRes) ?? "null"}");
    }
}
