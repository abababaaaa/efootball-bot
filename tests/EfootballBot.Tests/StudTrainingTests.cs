using EfootballBot.Core.Bot.Scenarios.Stud;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

/// <summary>
/// 种马球员培训（购买）实机帧回归：用户 2026-11-08 提供的 20 张 1280×720 截图。
/// 锁购买/培训两条链路的界面分类、最佳位置子页高亮行读取与排序箭头探针。
/// </summary>
public class StudTrainingTests
{
    private readonly ITestOutputHelper _out;
    public StudTrainingTests(ITestOutputHelper output) => _out = output;

    private static string TestDir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..");
    private static string StudDir => Path.Combine(TestDir, "TestData", "stud");

    private static OcrResult RunOcr(OcrRecognizer ocr, string file, out FrameData frame)
    {
        var bgra = TestFrameLoader.LoadPngBgra(file, out int w, out int h);
        frame = new FrameData(bgra, w, h, 0);
        return TestOcr.Run(ocr, frame);
    }

    [Fact]
    public void Classify_User_Purchase_And_Training_Frames()
    {
        var expected = new (string File, StudScreen Screen)[]
        {
            ("stud_01_main_menu.png", StudScreen.MainMenu),
            ("stud_02_main_menu_myteam.png", StudScreen.MainMenu),
            ("stud_03_contract_home.png", StudScreen.ContractHome),
            ("stud_04_contract_normal.png", StudScreen.ContractHome),
            ("stud_05_contract_normal_list.png", StudScreen.ContractHome),
            ("stud_06_normal_list.png", StudScreen.ContractPlayerList),
            ("stud_07_sort.png", StudScreen.SortPopup),
            ("stud_08_contract_detail.png", StudScreen.ContractDetail),
            ("stud_09_payment.png", StudScreen.PaymentConfirm),
            ("stud_10_acquired.png", StudScreen.AcquiredPopup),
            ("stud_11_post_acquire_detail.png", StudScreen.PostAcquireDetail),
            ("stud_12_main_menu_after.png", StudScreen.MainMenu),
            ("stud_13_myteam_home.png", StudScreen.MyTeamHome),
            ("stud_14_myteam_players.png", StudScreen.MyTeamHome),
            ("stud_15_squad_list.png", StudScreen.SquadList),
            ("stud_16_action.png", StudScreen.ActionMenu),
            ("stud_17_filter_top.png", StudScreen.Filter),
            ("stud_18_filter_scrolled.png", StudScreen.Filter),
            ("stud_19_position.png", StudScreen.PositionFilter),
            ("stud_20_filter_after.png", StudScreen.Filter),
            ("stud_21_training.png", StudScreen.TrainingFilter),
            ("stud_22_rating.png", StudScreen.RatingFilter),
            ("stud_23_rating_last.png", StudScreen.RatingFilter),
            ("stud_24_filter_done.png", StudScreen.Filter),
            ("stud_25_squad_after_filter.png", StudScreen.SquadList),
            ("stud_26_player_detail.png", StudScreen.TrainingPlayerDetail),
            ("stud_27_player_actions.png", StudScreen.PlayerActions),
            ("stud_28_player_actions_skill.png", StudScreen.PlayerActions),
            ("stud_29_skill_training.png", StudScreen.SkillTraining),
            ("stud_31_item_select.png", StudScreen.SkillItemSelect),
            ("stud_32_skill_confirm.png", StudScreen.SkillConfirm),
            ("stud_33_skill_result.png", StudScreen.SkillResult),
            ("stud_34_skill_training_after.png", StudScreen.SkillTraining),
        };

        using var ocr = new OcrRecognizer();
        foreach (var (file, want) in expected)
        {
            string path = Path.Combine(StudDir, file);
            Assert.True(File.Exists(path), $"缺少实机帧: {path}");
            var res = RunOcr(ocr, path, out _);
            var got = StudTrainingBrain.Classify(res);
            _out.WriteLine($"{file}: {got}");
            Assert.Equal(want, got);
        }
    }

    [Fact]
    public void Position_Subpage_Selected_Row_Is_Gk()
    {
        using var ocr = new OcrRecognizer();
        string path = Path.Combine(StudDir, "stud_19_position.png");
        var res = RunOcr(ocr, path, out var frame);
        string? row = StudTrainingBrain.SelectedRightRowText(res, frame);
        _out.WriteLine($"selected row = {row}");
        Assert.NotNull(row);
        Assert.Contains("GK", row);
    }

    [Fact]
    public void Skill_Slot_And_Result_Probes()
    {
        using var ocr = new OcrRecognizer();

        var empty = RunOcr(ocr, Path.Combine(StudDir, "stud_29_skill_training.png"), out var emptyFrame);
        Assert.True(StudTrainingBrain.HasEmptyFirstSkillSlot(emptyFrame), "技巧训练第一格应为蓝色加号");

        RunOcr(ocr, Path.Combine(StudDir, "stud_34_skill_training_after.png"), out var filledFrame);
        Assert.False(StudTrainingBrain.HasEmptyFirstSkillSlot(filledFrame), "训练后第一格已写入技巧，不应再判为空");

        var result = RunOcr(ocr, Path.Combine(StudDir, "stud_33_skill_result.png"), out _);
        string? name = StudTrainingBrain.ReadSkillResultName(result);
        _out.WriteLine($"result name = {name}");
        Assert.Equal("守门员低弹道凌空球", name ?? "");

        var list = RunOcr(ocr, Path.Combine(StudDir, "stud_25_squad_after_filter.png"), out _);
        Assert.Equal(9, StudTrainingBrain.ReadVisiblePlayerCount(list) ?? -1);
    }

    [Fact]
    public void Payment_And_Acquired_Popups_Are_Mutually_Exclusive()
    {
        var pay = Ocr(W("付款确认", 0.5, 0.3), W("使用GP签下这名球员", 0.5, 0.4), W("取消", 0.5, 0.6));
        var got = Ocr(W("获得:GedeonVerlaine", 0.5, 0.3), W("在我的球队球员查看新球员", 0.5, 0.4), W("OK", 0.5, 0.6));
        Assert.Equal(StudScreen.PaymentConfirm, StudTrainingBrain.Classify(pay));
        Assert.Equal(StudScreen.AcquiredPopup, StudTrainingBrain.Classify(got));
    }

    private static OcrWord W(string text, double cx, double cy)
        => new(text, cx - 0.08, cy - 0.015, 0.16, 0.03);

    private static OcrResult Ocr(params OcrWord[] words) => new(words, 1, 1);
}
