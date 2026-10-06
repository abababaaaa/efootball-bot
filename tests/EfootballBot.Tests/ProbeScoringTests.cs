using EfootballBot.Core.Bot;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Graph;
using EfootballBot.Core.Probe;
using EfootballBot.Core.Vision;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>
/// 回归：OcrProbe 旧实现在 MergedWords（原始词 + 合并行）上计分，同一排版行内的关键词
/// 会被算两次（原始词一次、包含它的合并行一次）。真机活动中心帧里「教练模式比赛」因此
/// 得 6 分越过 ChallengeHome 阈值 4，主页「阵容召集」得 4 分越过 MyLeagueHome 阈值 2.5。
/// 规则：每个关键词在每条排版行至多计一次分。
/// </summary>
public sealed class ProbeScoringTests
{
    private static Observation Obs(params OcrWord[] words)
        => new(new FrameData(Array.Empty<byte>(), 1280, 720, 0),
               new OcrResult(words.ToList(), 1280, 720),
               GameScreen.Unknown, 0, Array.Empty<string>());

    [Fact]
    public void OcrProbe_KeywordInSameLine_ScoresOnce()
    {
        // 同一行两个词（y 微抖），其中一个含「教练模式」
        var o = Obs(
            new OcrWord("实时竞技比赛", 0.388, 0.588, 0.064, 0.029),
            new OcrWord("教练模式比赛", 0.571, 0.590, 0.062, 0.026));

        var probe = new OcrProbe { Text = "教练模式", Weight = 3 };
        Assert.Equal(3, probe.Score(o));
    }

    [Fact]
    public void OcrProbe_KeywordOnTwoLines_ScoresTwice()
    {
        // 真的出现在两条不同排版行（标题 + 正文）时仍各计一次
        var o = Obs(
            new OcrWord("教练模式", 0.10, 0.20, 0.10, 0.04),
            new OcrWord("教练模式比赛", 0.57, 0.60, 0.10, 0.03));

        var probe = new OcrProbe { Text = "教练模式", Weight = 3 };
        Assert.Equal(6, probe.Score(o));
    }

    [Fact]
    public void EventHubFrame_ChallengeTileTag_DoesNotBecomeChallengeHome()
    {
        // 真机 dump_130834_508 活动中心：「教练模式比赛」只是磁贴标签
        var o = Obs(
            new OcrWord("实况足球系列活动", 0.176, 0.174, 0.265, 0.074),
            new OcrWord("活动中心", 0.069, 0.207, 0.076, 0.046),
            new OcrWord("活动比赛", 0.183, 0.356, 0.060, 0.036),
            new OcrWord("天梯赛教练", 0.569, 0.557, 0.075, 0.036),
            new OcrWord("实时竞技比赛", 0.388, 0.588, 0.064, 0.029),
            new OcrWord("教练模式比赛", 0.571, 0.590, 0.062, 0.026),
            new OcrWord("登录活动", 0.069, 0.464, 0.074, 0.043));

        var result = new ProbeEngine(PageCatalog.Pages).Classify(o);
        Assert.NotEqual(GameScreen.ChallengeHome, result.Screen);
    }

    [Fact]
    public void HomeFrame_SquadTile_DoesNotBecomeMyLeagueHome()
    {
        // 真机主页含「阵容召集」活动磁贴，单个「阵容」弱词不得越过联赛主页阈值
        var o = Obs(
            new OcrWord("活动中心", 0.07, 0.21, 0.10, 0.04),
            new OcrWord("阵容召集", 0.75, 0.75, 0.08, 0.03));

        var result = new ProbeEngine(PageCatalog.Pages).Classify(o);
        Assert.NotEqual(GameScreen.MyLeagueHome, result.Screen);
    }

    [Fact]
    public void KeyMatchNotice_ForcedDialog_EvenWhenScheduleWordsCoexist()
    {
        // 「即将进行的是关键比赛」弹窗：标题+OK+取消，背景透出比赛日/赛程表，
        // 必须强制 Dialog（与旧分类器 IsKeyNotice 早返回等价）
        var o = Obs(
            new OcrWord("即将进行的是关键比赛", 0.35, 0.28, 0.30, 0.06),
            new OcrWord("比赛日17", 0.16, 0.51, 0.06, 0.03),
            new OcrWord("赛程表", 0.08, 0.37, 0.06, 0.03),
            new OcrWord("OK", 0.49, 0.57, 0.03, 0.04),
            new OcrWord("取消", 0.48, 0.67, 0.04, 0.04));

        var result = new ProbeEngine(PageCatalog.Pages).Classify(o);
        Assert.Equal(GameScreen.Dialog, result.Screen);
        Assert.Equal("key-notice", result.OverrideReason);
    }

    [Fact]
    public void ActivityDetail_OnlyHelpTab_Classifies()
    {
        // dump_130836：活动详情页全页仅底部「活动说明」强标签 + 返回/关闭
        var o = Obs(
            new OcrWord("投资反馈", 0.10, 0.10, 0.10, 0.04),
            new OcrWord("活动说明", 0.45, 0.93, 0.08, 0.03),
            new OcrWord("返回", 0.03, 0.93, 0.04, 0.03));
        Assert.Equal(GameScreen.ActivityDetail, new ProbeEngine(PageCatalog.Pages).Classify(o).Screen);
    }

    [Fact]
    public void EventCardList_EnterButtons_ClassifiesEventInfo_NotEventHub()
    {
        // dump_131240：带「进入」按钮的活动卡片列表（旧分类器 EventInfo），
        // 虽然同时含对阵电脑/巡回赛等模式词，也不能判活动中心
        var o = Obs(
            new OcrWord("对阵电脑", 0.10, 0.10, 0.08, 0.03),
            new OcrWord("巡回赛", 0.25, 0.10, 0.06, 0.03),
            new OcrWord("对阵玩家", 0.35, 0.10, 0.08, 0.03),
            new OcrWord("挑战赛", 0.50, 0.10, 0.06, 0.03),
            new OcrWord("意大利联赛巡回赛", 0.10, 0.35, 0.20, 0.04),
            new OcrWord("剩余时间:3天21小时", 0.10, 0.42, 0.15, 0.02),
            new OcrWord("进入", 0.22, 0.60, 0.05, 0.04),
            new OcrWord("欢度十一五日挑战赛", 0.40, 0.35, 0.20, 0.04),
            new OcrWord("剩余时间：5天21小时", 0.40, 0.42, 0.15, 0.02),
            new OcrWord("进入", 0.52, 0.60, 0.05, 0.04),
            new OcrWord("返回", 0.03, 0.93, 0.04, 0.03));
        Assert.Equal(GameScreen.EventInfo, new ProbeEngine(PageCatalog.Pages).Classify(o).Screen);
    }

    [Fact]
    public void MatchHub_SparseOcr_Classifies()
    {
        // dump_131238：OCR 只抓到天梯巅峰赛/好友对战 + 导航词
        var o = Obs(
            new OcrWord("天梯巅峰赛", 0.20, 0.30, 0.12, 0.05),
            new OcrWord("2026 S5赛季", 0.20, 0.40, 0.10, 0.03),
            new OcrWord("好友对战", 0.20, 0.55, 0.10, 0.04),
            new OcrWord("商店", 0.90, 0.05, 0.04, 0.02),
            new OcrWord("签约", 0.84, 0.05, 0.04, 0.02));
        Assert.Equal(GameScreen.MatchHub, new ProbeEngine(PageCatalog.Pages).Classify(o).Screen);
    }

    [Fact]
    public void Home_NavOnlyOcr_Classifies()
    {
        // dump_130850：主页 OCR 只抓到顶部导航 比赛/推荐/签约/商店
        var o = Obs(
            new OcrWord("比赛", 0.10, 0.05, 0.04, 0.02),
            new OcrWord("推荐", 0.16, 0.05, 0.04, 0.02),
            new OcrWord("签约", 0.22, 0.05, 0.04, 0.02),
            new OcrWord("商店", 0.28, 0.05, 0.04, 0.02));
        Assert.Equal(GameScreen.Home, new ProbeEngine(PageCatalog.Pages).Classify(o).Screen);
    }

    [Fact]
    public void MatchHub_WithGoToMatchAndSuggestionTiles_NotChallengeHome()
    {
        // 真机 19:34 日志：比赛菜单磁贴预览含「前往比赛」×2 行 +「建议」=4.5 曾越过
        // ChallengeHome 门槛 4（旧分类器 MatchHub 21.0 高置信）。泛词不得判 ChallengeHome
        var o = Obs(
            new OcrWord("快速比赛", 0.10, 0.30, 0.10, 0.04),
            new OcrWord("我的联赛", 0.30, 0.30, 0.10, 0.04),
            new OcrWord("好友对战", 0.50, 0.30, 0.10, 0.04),
            new OcrWord("前往比赛", 0.30, 0.36, 0.08, 0.03),
            new OcrWord("天梯巅峰赛", 0.10, 0.50, 0.10, 0.04),
            new OcrWord("好友对战", 0.50, 0.50, 0.10, 0.04),
            new OcrWord("前往比赛", 0.10, 0.56, 0.08, 0.03),
            new OcrWord("建议", 0.70, 0.50, 0.05, 0.03));
        Assert.NotEqual(GameScreen.ChallengeHome, new ProbeEngine(PageCatalog.Pages).Classify(o).Screen);
    }

    [Fact]
    public void Home_WithDailyMinigameTile_NotDailyRewards()
    {
        // 真机 19:34 日志：主页「每日小游戏」磁贴+弱词「每日」=3.5 曾判 DailyRewards（P30 压过 Home P10）
        var o = Obs(
            new OcrWord("活动中心", 0.10, 0.20, 0.08, 0.03),
            new OcrWord("我的球队", 0.30, 0.20, 0.08, 0.03),
            new OcrWord("推荐", 0.50, 0.20, 0.05, 0.03),
            new OcrWord("每日小游戏", 0.10, 0.60, 0.10, 0.03),
            new OcrWord("签约", 0.85, 0.05, 0.04, 0.02));
        Assert.Equal(GameScreen.Home, new ProbeEngine(PageCatalog.Pages).Classify(o).Screen);
    }

    [Fact]
    public void HalfTime_SingleWeakWord_DoesNotFire_NeedsFullPhrase()
    {
        // 主页新闻横幅里可能出现「中场」二字；必须是「中场休息」或多信号才行
        var weak = Obs(new OcrWord("中场", 0.20, 0.50, 0.05, 0.03));
        Assert.NotEqual(GameScreen.HalfTime, new ProbeEngine(PageCatalog.Pages).Classify(weak).Screen);

        var strong = Obs(new OcrWord("中场休息", 0.30, 0.40, 0.20, 0.06));
        Assert.Equal(GameScreen.HalfTime, new ProbeEngine(PageCatalog.Pages).Classify(strong).Screen);
    }
}
