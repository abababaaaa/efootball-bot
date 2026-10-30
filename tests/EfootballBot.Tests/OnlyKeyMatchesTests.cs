using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Vision;
using Xunit;

namespace EfootballBot.Tests;

// 「只打关键比赛」判定回归：国际服比赛内没有「放弃比赛」菜单项，普通比赛只能靠杀进程跳过。
// 核心约定是代价不对称——误弃一场关键比赛（丢额外积分/经验、可能影响赛程奖励）远比
// 多打一场普通比赛严重，所以任何一项关键比赛证据都必须否决跳过。
public class OnlyKeyMatchesTests
{
    private static OcrResult Ocr(params (string text, double x, double y)[] words)
        => new(words.Select(w => new OcrWord(w.text, w.x, w.y, 0.1, 0.03)).ToList(), 1, 1);

    [Fact]
    public void NormalMatch_NoKeyEvidence_IsSkippable()
    {
        // 比赛中普通帧（记分牌时钟 + 球员名，无关键比赛字样）→ 可跳过
        var ocr = Ocr(("29:04", 0.62, 0.06), ("0", 0.55, 0.12), ("3", 0.66, 0.12),
                      ("瑞典", 0.45, 0.28), ("AC米兰", 0.78, 0.28));
        Assert.True(MyLeagueScenario.IsNormalMatchForSkip(nextMatchIsKey: false, ocr));
    }

    [Fact]
    public void KeyMatchFlagFromEarlier_BlocksSkip()
    {
        // 上一场见过关键比赛弹窗/主页预告 → nextMatchIsKey=true，即使当前帧 OCR 读不到字样也不许跳
        var ocr = Ocr(("29:04", 0.62, 0.06));
        Assert.False(MyLeagueScenario.IsNormalMatchForSkip(nextMatchIsKey: true, ocr));
    }

    [Theory]
    [InlineData("即将进行的是关键比赛")]
    [InlineData("下一场是关键比赛")]
    [InlineData("关键比赛")]
    public void KeyMatchWordOnScreen_BlocksSkip(string text)
    {
        // 当前帧出现「关键比赛」字样（弹窗文案 / 受限难度页副标题）→ 否决跳过
        var ocr = Ocr((text, 0.5, 0.3), ("29:04", 0.62, 0.06));
        Assert.False(MyLeagueScenario.IsNormalMatchForSkip(nextMatchIsKey: false, ocr));
    }

    [Fact]
    public void ImportantMatchAlias_BlocksSkip()
    {
        // 「重要比赛」是关键比赛同义别名（游戏文案改版防护），同样否决跳过
        var ocr = Ocr(("下一场是重要比赛", 0.5, 0.4));
        Assert.False(MyLeagueScenario.IsNormalMatchForSkip(nextMatchIsKey: false, ocr));
    }

    [Fact]
    public void EmptyOcr_IsSkippable()
    {
        // OCR 全空（帧模糊）：没有关键比赛证据。这条依赖「时钟已走够」的前置门槛兜底，
        // 由 HandleSkipNormalMatch 的 StartedAt 判定保证此时比赛确实已经开打。
        Assert.True(MyLeagueScenario.IsNormalMatchForSkip(nextMatchIsKey: false, OcrResult.Empty));
    }

    [Fact]
    public void KeyWordSplitAcrossWords_StillBlocks()
    {
        // OCR 把「关键比赛」拆成多个词（大字美术字常见）→ 拼句匹配仍须否决跳过
        var ocr = Ocr(("关键", 0.45, 0.3), ("比赛", 0.55, 0.3));
        Assert.False(MyLeagueScenario.IsNormalMatchForSkip(nextMatchIsKey: false, ocr));
    }
}
