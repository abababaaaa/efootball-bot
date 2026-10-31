using EfootballBot.Core.Bot.Scenarios.CnWeekly;
using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Vision;
using Xunit;

namespace EfootballBot.Tests;

// 国服周活动·挑战 误判通关与禁跳回归（用户 2026-10-31 反馈包 v5.1.4）：
// 卡 03「以4分或以上为目标」积分 3/4 被「已打满 2 局按通关处理」兜底误判通关，主要奖励漏拿；
// 同局被「跳过比赛✅」跳过——跳过不计积分，挑战赛积分制对局必须禁跳
public class CnWeeklyChallengeGuardTests
{
    private static OcrResult Ocr(params (string text, double x, double y)[] words)
    {
        var list = words.Select(w => new OcrWord(w.text, w.x, w.y, 0.1, 0.03)).ToList();
        return new OcrResult(list, 1, 1);
    }

    // ---------------- ChallengeScoreBehind：积分/进度未达标护栏 ----------------

    [Fact]
    public void ScoreBehind_UnmetPointGoal_Blocks()
    {
        // 卡 03 实机形态：积分 3/4（目标 4 分只拿了 3 分）
        var ocr = Ocr(("完成的条件", 0.5, 0.22), ("以4分或以上为目标", 0.5, 0.28),
            ("积分", 0.75, 0.21), ("3/4", 0.82, 0.21), ("进度", 0.75, 0.44), ("1/3", 0.82, 0.44));
        Assert.True(CnWeeklyBrain.ChallengeScoreBehind(ocr));
    }

    [Fact]
    public void ScoreBehind_GoalMet_DoesNotBlock()
    {
        // 达标态：积分 4/4——护栏不得反向申报，也不得拦正常通关判定
        var ocr = Ocr(("积分", 0.75, 0.21), ("4/4", 0.82, 0.21));
        Assert.False(CnWeeklyBrain.ChallengeScoreBehind(ocr));
    }

    [Fact]
    public void ScoreBehind_NoRatioTokens_DoesNotBlock()
    {
        // 读不出任何比值：护栏保持沉默，原有兜底链不受影响
        var ocr = Ocr(("前往比赛", 0.3, 0.3), ("建议", 0.2, 0.8));
        Assert.False(CnWeeklyBrain.ChallengeScoreBehind(ocr));
    }

    [Fact]
    public void ScoreBehind_TwoDigitRatio_Ignored()
    {
        // 两位数比值（日期 10/31 等）不取，防误拦
        var ocr = Ocr(("10/31", 0.5, 0.5));
        Assert.False(CnWeeklyBrain.ChallengeScoreBehind(ocr));
    }

    [Fact]
    public void ScoreBehind_ZeroProgress_Blocks()
    {
        var ocr = Ocr(("积分", 0.75, 0.21), ("0/1", 0.82, 0.21));
        Assert.True(CnWeeklyBrain.ChallengeScoreBehind(ocr));
    }

    // ---------------- MyLeagueBrain.Decide allowMatchSkip：挑战赛禁跳 ----------------

    [Fact]
    public void Decide_ChallengeMode_SkipBannerIgnored()
    {
        var ocr = Ocr(("跳过比赛", 0.85, 0.06));
        var action = MyLeagueBrain.Decide(MlScreen.InMatch, ocr, 5, allowMatchSkip: false);
        Assert.Equal(MlActionKind.MatchIdleTap, action.Kind); // 继续正常挂机，不开暂停菜单
    }

    [Fact]
    public void Decide_ChallengeMode_PauseMenuNeverSkips()
    {
        var win = Ocr(("跳过比赛", 0.6, 0.3), ("对局获胜", 0.6, 0.4));
        Assert.Equal(MlActionKind.GamepadBack,
            MyLeagueBrain.Decide(MlScreen.CnMatchPause, win, 1, allowMatchSkip: false).Kind);
    }

    [Fact]
    public void Decide_ChallengeMode_SkipConfirmPopupCloses()
    {
        // 禁跳下误开过一次菜单后弹「确认跳过」：B 关闭（默认焦点在关闭），绝不确认
        var popup = Ocr(("确认跳过", 0.6, 0.4), ("关闭", 0.6, 0.55));
        Assert.Equal(MlActionKind.GamepadBack,
            MyLeagueBrain.Decide(MlScreen.CnMatchPause, popup, 1, allowMatchSkip: false).Kind);
    }

    [Fact]
    public void Decide_LeagueMode_SkipUnchanged()
    {
        // 默认 allowMatchSkip=true：联赛/天梯等胜负制行为不变
        var win = Ocr(("跳过比赛", 0.6, 0.3), ("对局获胜", 0.6, 0.4));
        Assert.Equal(MlActionKind.PadConfirm,
            MyLeagueBrain.Decide(MlScreen.CnMatchPause, win, 1).Kind);
    }
}
