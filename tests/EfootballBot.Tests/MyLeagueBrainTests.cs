using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using Xunit;

namespace EfootballBot.Tests;

// 我的联赛决策大脑（全手柄版）：Classify（YOLO+OCR）与 Decide（纯手柄动作）纯函数单测
public class MyLeagueBrainTests
{
    private static YoloDetection Det(string cls, float score)
        => new(cls, 0, score, 0.4, 0.4, 0.2, 0.1);

    private static OcrResult Ocr(params (string text, double x, double y)[] words)
    {
        var list = words.Select(w => new OcrWord(w.text, w.x, w.y, 0.1, 0.03)).ToList();
        return new OcrResult(list, 1, 1);
    }

    // ---------------- Classify ----------------

    [Fact]
    public void Classify_MainMenu()
    {
        var dets = new[] { Det("main_menu", 0.9f), Det("player_card", 0.8f) };
        Assert.Equal(MyLeagueBrain.Classify(dets, OcrResult.Empty), MlScreen.MainMenu);
    }

    [Fact]
    public void Classify_ItemList_NeedsTwoRowsAndBack()
    {
        var dets = new[] { Det("item_row", 0.9f), Det("item_row", 0.8f), Det("btn_back", 0.9f) };
        Assert.Equal(MlScreen.ItemList, MyLeagueBrain.Classify(dets, OcrResult.Empty));
    }

    [Fact]
    public void Classify_SingleRow_IsNotItemList()
    {
        var dets = new[] { Det("item_row", 0.9f), Det("btn_back", 0.9f) };
        // 单行不足列表特征 → 落入 LeaguePage（仅返回按钮可见）
        Assert.Equal(MlScreen.LeaguePage, MyLeagueBrain.Classify(dets, OcrResult.Empty));
    }

    [Fact]
    public void Classify_HasConfirm_BeatsLeaguePage()
    {
        var dets = new[] { Det("btn_confirm", 0.9f), Det("btn_back", 0.8f) };
        Assert.Equal(MlScreen.HasConfirm, MyLeagueBrain.Classify(dets, OcrResult.Empty));
    }

    [Fact]
    public void Classify_LowScore_Ignored()
    {
        var dets = new[] { Det("main_menu", 0.3f), Det("btn_confirm", 0.3f) };
        Assert.Equal(MlScreen.Unknown, MyLeagueBrain.Classify(dets, OcrResult.Empty));
    }

    [Fact]
    public void Classify_MatchHub_ByOcr_WithoutExit()
    {
        // 比赛页无 YOLO 特征：OCR 含"我的联赛"且无主菜单"退出游戏"
        var ocr = Ocr(("我的联赛", 0.3, 0.2), ("赛季目标", 0.6, 0.5));
        Assert.Equal(MlScreen.MatchHub, MyLeagueBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_MainMenuOcr_WithExit_IsNotMatchHub()
    {
        // 主菜单特有"退出游戏"，即使 OCR 含"我的联赛"（banner 文字）也不算比赛页
        var ocr = Ocr(("我的联赛", 0.3, 0.5), ("退出游戏", 0.8, 0.75));
        Assert.Equal(MlScreen.Unknown, MyLeagueBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_Empty_IsUnknown()
    {
        Assert.Equal(MlScreen.Unknown, MyLeagueBrain.Classify(Array.Empty<YoloDetection>(), OcrResult.Empty));
    }

    // ---------------- Decide ----------------

    [Fact]
    public void Decide_MainMenu_EnterMatchNavigation()
    {
        var action = MyLeagueBrain.Decide(MlScreen.MainMenu, OcrResult.Empty, 0);
        Assert.Equal(MlActionKind.PadEnterMatch, action.Kind);
    }

    [Fact]
    public void Decide_MainMenu_Stuck_Waits()
    {
        var action = MyLeagueBrain.Decide(MlScreen.MainMenu, OcrResult.Empty, MyLeagueBrain.StuckStreak);
        Assert.Equal(MlActionKind.Wait, action.Kind);
    }

    [Fact]
    public void Decide_MatchHub_ConfirmsDefaultFocus()
    {
        var action = MyLeagueBrain.Decide(MlScreen.MatchHub, OcrResult.Empty, 0);
        Assert.Equal(MlActionKind.PadConfirm, action.Kind);
    }

    [Fact]
    public void Decide_ItemList_ConfirmsFocusedRow()
    {
        var action = MyLeagueBrain.Decide(MlScreen.ItemList, OcrResult.Empty, 0);
        Assert.Equal(MlActionKind.PadConfirm, action.Kind);
    }

    [Fact]
    public void Decide_HasConfirm_Confirms()
    {
        var action = MyLeagueBrain.Decide(MlScreen.HasConfirm, OcrResult.Empty, 0);
        Assert.Equal(MlActionKind.PadConfirm, action.Kind);
    }

    [Fact]
    public void Decide_LeaguePage_NextStep_Confirms()
    {
        var ocr = Ocr(("下一步", 0.80, 0.91));
        var action = MyLeagueBrain.Decide(MlScreen.LeaguePage, ocr, 0);
        Assert.Equal(MlActionKind.PadConfirm, action.Kind);
        Assert.Contains("下一步", action.Target);
    }

    [Fact]
    public void Decide_LeaguePage_NoButton_Waits()
    {
        var action = MyLeagueBrain.Decide(MlScreen.LeaguePage, OcrResult.Empty, 0);
        Assert.Equal(MlActionKind.Wait, action.Kind);
    }

    [Fact]
    public void Decide_Unknown_EarlyStreak_Waits()
    {
        var action = MyLeagueBrain.Decide(MlScreen.Unknown, OcrResult.Empty, 3);
        Assert.Equal(MlActionKind.Wait, action.Kind);
    }

    [Fact]
    public void Decide_Unknown_EscapeStreak_GamepadBack()
    {
        var action = MyLeagueBrain.Decide(MlScreen.Unknown, OcrResult.Empty, MyLeagueBrain.UnknownEscapeStreak);
        Assert.Equal(MlActionKind.GamepadBack, action.Kind);
    }
}
