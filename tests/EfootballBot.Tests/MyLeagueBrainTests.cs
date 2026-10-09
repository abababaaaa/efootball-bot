using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using Xunit;

namespace EfootballBot.Tests;

public class MyLeagueBrainTests
{
    private static YoloDetection Det(string cls, float score) => new(cls, 0, score, 0.1, 0.1, 0.1, 0.1);

    private static OcrResult Ocr(params (string text, double x, double y)[] words)
    {
        var list = words.Select(w => new OcrWord(w.text, w.x, w.y, 0.10, 0.03)).ToArray();
        return new OcrResult(list, 1, 1);
    }

    // ---------------- Classify ----------------

    [Fact]
    public void Classify_MainMenu() =>
        Assert.Equal(MlScreen.MainMenu, MyLeagueBrain.Classify(new[] { Det("main_menu", 0.8f) }));

    [Fact]
    public void Classify_ItemList_RequiresTwoRowsAndBack()
    {
        var dets = new YoloDetection[]
        {
            Det("item_row", 0.9f), Det("item_row", 0.85f), Det("btn_back", 0.9f),
        };
        Assert.Equal(MlScreen.ItemList, MyLeagueBrain.Classify(dets));
    }

    [Fact]
    public void Classify_SingleRow_NotItemList()
    {
        var dets = new YoloDetection[] { Det("item_row", 0.9f), Det("btn_back", 0.9f) };
        Assert.Equal(MlScreen.LeaguePage, MyLeagueBrain.Classify(dets));
    }

    [Fact]
    public void Classify_HasConfirm_TakesPrecedenceOverBack() =>
        Assert.Equal(MlScreen.HasConfirm, MyLeagueBrain.Classify(
            new[] { Det("btn_confirm", 0.6f), Det("btn_back", 0.9f) }));

    [Fact]
    public void Classify_LowScoreIgnored() =>
        Assert.Equal(MlScreen.Unknown, MyLeagueBrain.Classify(new[] { Det("main_menu", 0.3f) }));

    [Fact]
    public void Classify_EmptyDetections_Unknown() =>
        Assert.Equal(MlScreen.Unknown, MyLeagueBrain.Classify(Array.Empty<YoloDetection>()));

    // ---------------- Decide：主菜单 ----------------

    [Fact]
    public void Decide_MainMenu_ClicksMyLeagueEntry()
    {
        var ocr = Ocr(("我的联赛", 0.30, 0.50));
        var action = MyLeagueBrain.Decide(MlScreen.MainMenu, Array.Empty<YoloDetection>(), ocr, 0);
        Assert.Equal(MlActionKind.ClickMyLeague, action.Kind);
        Assert.Equal(0.35, action.X, 5);   // 行中心 = x + w/2
        Assert.Equal(0.515, action.Y, 5);  // y + h/2
    }

    [Fact]
    public void Decide_MainMenu_NoEntry_Waits()
    {
        var action = MyLeagueBrain.Decide(MlScreen.MainMenu, Array.Empty<YoloDetection>(), OcrResult.Empty, 0);
        Assert.Equal(MlActionKind.Wait, action.Kind);
    }

    [Fact]
    public void Decide_MainMenu_Stuck_BackOff() =>
        Assert.Equal(MlActionKind.Wait,
            MyLeagueBrain.Decide(MlScreen.MainMenu, Array.Empty<YoloDetection>(),
                Ocr(("我的联赛", 0.3, 0.5)), MyLeagueBrain.StuckStreak).Kind);

    // ---------------- Decide：物品列表 ----------------

    [Fact]
    public void Decide_ItemList_ClicksBestRow()
    {
        var dets = new YoloDetection[]
        {
            new("item_row", 5, 0.80f, 0.40, 0.30, 0.74, 0.15),
            new("item_row", 5, 0.92f, 0.40, 0.46, 0.74, 0.15),
        };
        var action = MyLeagueBrain.Decide(MlScreen.ItemList, dets, OcrResult.Empty, 0);
        Assert.Equal(MlActionKind.ClickItem, action.Kind);
        Assert.Equal(0.77, action.X, 5);  // 0.40 + 0.74/2
        Assert.Equal(0.535, action.Y, 5); // 0.46 + 0.15/2（高分行）
    }

    // ---------------- Decide：确认按钮 ----------------

    [Fact]
    public void Decide_HasConfirm_ClicksButtonCenter()
    {
        var dets = new YoloDetection[] { new("btn_confirm", 2, 0.88f, 0.78, 0.90, 0.19, 0.07) };
        var action = MyLeagueBrain.Decide(MlScreen.HasConfirm, dets, OcrResult.Empty, 0);
        Assert.Equal(MlActionKind.ClickPoint, action.Kind);
        Assert.Equal(0.875, action.X, 5);
        Assert.Equal(0.935, action.Y, 5);
    }

    // ---------------- Decide：联赛页 ----------------

    [Fact]
    public void Decide_LeaguePage_ClicksNextStep()
    {
        var ocr = Ocr(("下一步", 0.80, 0.91));
        var action = MyLeagueBrain.Decide(MlScreen.LeaguePage, Array.Empty<YoloDetection>(), ocr, 0);
        Assert.Equal(MlActionKind.ClickPoint, action.Kind);
        Assert.Equal(0.85, action.X, 5);
    }

    [Fact]
    public void Decide_LeaguePage_NoButton_Waits()
    {
        var action = MyLeagueBrain.Decide(MlScreen.LeaguePage, Array.Empty<YoloDetection>(), OcrResult.Empty, 0);
        Assert.Equal(MlActionKind.Wait, action.Kind);
    }

    // ---------------- Decide：无特征界面 ----------------

    [Fact]
    public void Decide_Unknown_WaitsBeforeEscape()
    {
        var action = MyLeagueBrain.Decide(MlScreen.Unknown, Array.Empty<YoloDetection>(), OcrResult.Empty, 3);
        Assert.Equal(MlActionKind.Wait, action.Kind);
    }

    [Fact]
    public void Decide_Unknown_EscapeWithBackAtStreak6()
    {
        var action = MyLeagueBrain.Decide(MlScreen.Unknown, Array.Empty<YoloDetection>(), OcrResult.Empty, 6);
        Assert.Equal(MlActionKind.GamepadBack, action.Kind);
    }
}
