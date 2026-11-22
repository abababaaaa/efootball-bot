using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Vision;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>主菜单底部磁贴行 OCR 定位（FindMainMenuMatchOffset）单测。</summary>
public class MainMenuTileLocatorTests
{
    private static OcrResult Ocr(params (string Text, double X, double Y)[] words)
        => new(words.Select(w => new OcrWord(w.Text, w.X, w.Y, 0.08, 0.03)).ToList(), 1280, 720);

    [Fact]
    public void 比赛在最左_返回0()
    {
        var ocr = Ocr(
            ("比赛", 0.10, 0.75), ("我的球队", 0.30, 0.75),
            ("合约", 0.50, 0.75), ("任务", 0.70, 0.75), ("每日小游戏", 0.90, 0.75));
        Assert.Equal(0, HubTileLocator.FindMainMenuMatchOffset(ocr));
    }

    [Fact]
    public void 比赛在第2格_返回1()
    {
        // 布局变化：比赛左移插入了其它卡，比赛变成第2个
        var ocr = Ocr(
            ("每日小游戏", 0.10, 0.75), ("比赛", 0.30, 0.75),
            ("我的球队", 0.50, 0.75), ("合约", 0.70, 0.75), ("任务", 0.90, 0.75));
        Assert.Equal(1, HubTileLocator.FindMainMenuMatchOffset(ocr));
    }

    [Fact]
    public void 读不全三个标签_返回null回退()
    {
        var ocr = Ocr(("比赛", 0.10, 0.75), ("我的球队", 0.30, 0.75));
        Assert.Null(HubTileLocator.FindMainMenuMatchOffset(ocr));
    }

    [Fact]
    public void 顶部任务不干扰_只看底部行()
    {
        // 顶部图标区有"任务"（y=0.12），底部行不足3个标签 → 应回退
        var ocr = Ocr(
            ("任务", 0.90, 0.12), ("比赛", 0.10, 0.75), ("我的球队", 0.30, 0.75));
        Assert.Null(HubTileLocator.FindMainMenuMatchOffset(ocr));
    }

    [Fact]
    public void 比赛记录等相似词不算磁贴标签()
    {
        // "比赛记录""前往比赛"是联赛页词，y 也高；不应被当成底部"比赛"磁贴
        var ocr = Ocr(
            ("比赛记录", 0.20, 0.30), ("前往比赛", 0.40, 0.30),
            ("比赛", 0.10, 0.75), ("我的球队", 0.30, 0.75), ("合约", 0.50, 0.75));
        Assert.Equal(0, HubTileLocator.FindMainMenuMatchOffset(ocr));
    }
}
