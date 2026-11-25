using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>
/// 我的联赛主页固定特征词判据（用户 2026-11-22 实机截图红框标注：
/// 前往比赛 / 排名 / 比赛记录 / 最近 5 场比赛 / 兑换积分 / 赛季纪录 / 赛事信息 / 建议，
/// 另有赛程表）。用户要求「这些词基本不会变，判断差不多就可以确定」——
/// 即不必把大字全读全，但也不能被别的板块页蹭到一两个通用词骗过。
///
/// 这些测试同时是 v8.0.11 三处逻辑加固的回归护栏：主页确认放得太松会漏掉进错卡，
/// 放得太紧会把正确进入 B 退出（v7.5.1 事故同款）。
/// </summary>
public class LeagueHomeFixedWordsTests
{
    private static OcrResult Ocr(params string[] words)
        => new(words.Select((t, i) => new OcrWord(t, 0.10 + i * 0.01, 0.30, 0.10, 0.04)).ToList(), 1280, 720);

    [Fact]
    public void 红框全词命中_确认主页()
    {
        var ocr = Ocr("前往比赛", "排名", "比赛记录", "最近 5 场比赛",
                      "兑换积分", "赛季纪录", "赛事信息", "建议", "赛程表");
        Assert.True(MyLeagueBrain.CountLeagueHomeWords(ocr) >= 8);
        Assert.True(MyLeagueBrain.LooksLikeLeagueHome(ocr));
        // 与共享分类一致：读到「前往比赛」时该帧被判 LeaguePage
        Assert.Equal(MatchScreen.LeaguePage, MatchBrain.Classify(new List<YoloDetection>(), ocr));
    }

    [Fact]
    public void 只读到两个词但含专有词_也算差不多()
    {
        // 用户要求：OCR 漏读大字时不要死等全词，命中两个（其中一个主页专有）就确认
        var ocr = Ocr("兑换积分", "建议");
        Assert.Equal(2, MyLeagueBrain.CountLeagueHomeWords(ocr));
        Assert.True(MyLeagueBrain.LooksLikeLeagueHome(ocr));
    }

    [Fact]
    public void 带空格的最近5场比赛_归一化后仍能命中()
    {
        var ocr = Ocr("最近 5 场比赛", "赛程表");
        Assert.Equal(2, MyLeagueBrain.CountLeagueHomeWords(ocr));
        Assert.True(MyLeagueBrain.LooksLikeLeagueHome(ocr));
    }

    [Fact]
    public void 赛季结束页_下个赛季加赛季纪录_确认主页()
    {
        var ocr = Ocr("下个赛季", "赛季记录"); // 「纪/记」两种 OCR 写法都收
        Assert.True(MyLeagueBrain.LooksLikeLeagueHome(ocr));
    }

    [Fact]
    public void 只蹭到通用词_排名加建议_不误认主页()
    {
        // 别的板块页也可能有「排名」「建议」：凑够两个但没有主页专有词 → 不确认
        var ocr = Ocr("排名", "建议");
        Assert.Equal(2, MyLeagueBrain.CountLeagueHomeWords(ocr));
        Assert.False(MyLeagueBrain.LooksLikeLeagueHome(ocr));
    }

    [Fact]
    public void 只命中一枚专有词_证据不足()
    {
        var ocr = Ocr("比赛记录");
        Assert.Equal(1, MyLeagueBrain.CountLeagueHomeWords(ocr));
        Assert.False(MyLeagueBrain.LooksLikeLeagueHome(ocr));
    }

    [Fact]
    public void 类别页与锦标赛页词_不构成主页证据()
    {
        var category = Ocr("eFootball™联赛", "对阵AI类别", "对阵玩家类别");
        Assert.Equal(0, MyLeagueBrain.CountLeagueHomeWords(category));
        Assert.False(MyLeagueBrain.LooksLikeLeagueHome(category));

        var tournament = Ocr("锦标赛", "淘汰赛", "小组赛", "自定义锦标赛");
        Assert.Equal(0, MyLeagueBrain.CountLeagueHomeWords(tournament));
        Assert.False(MyLeagueBrain.LooksLikeLeagueHome(tournament));
    }

    [Fact]
    public void 周活动页词_不构成主页证据()
    {
        var weekly = Ocr("对阵AI", "Team Effort", "阵容挑战", "巡回活动", "结束倒数");
        Assert.Equal(0, MyLeagueBrain.CountLeagueHomeWords(weekly));
        Assert.False(MyLeagueBrain.LooksLikeLeagueHome(weekly));
    }

    [Fact]
    public void 积分页只有兑换积分_不构成主页证据()
    {
        // 积分页是 isLeagueFlow 放行的正常流程页；单词命中不该被当成"确认进了主页"
        var points = Ocr("我的联赛积分", "兑换积分", "租借球员");
        Assert.Equal(1, MyLeagueBrain.CountLeagueHomeWords(points));
        Assert.False(MyLeagueBrain.LooksLikeLeagueHome(points));
    }
}
