using EfootballBot.Core.Bot.Scenarios;
using EfootballBot.Core.Bot.Scenarios.CnMyLeague;
using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Bot.Scenarios.PeakLadder;
using EfootballBot.Core.Config;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using Xunit;

namespace EfootballBot.Tests;

// 国服我的联赛移植回归：锁定进入导航所依赖的界面分类契约（合成 OCR 帧，坐标均为归一化值）。
// 实机依据（用户 2026-10-17 截图）：国服主页「比赛」页签卡片 = 比赛活动/天梯巅峰赛/我的联赛/
// 好友对战，我的联赛与天梯巅峰赛同排右侧——进入序列 = 天梯巅峰赛导航（上×2 左×4 右 下）
// 再 右×1 → A。进联赛后的全部页面复用国际服判定（前往比赛/难度/起始页等固定 UI 词两服一致）。
public class CnMyLeagueEntryTests
{
    private static OcrWord W(string text, double cx, double cy)
        => new(text, cx - 0.05, cy - 0.015, 0.1, 0.03);

    private static OcrResult Ocr(params OcrWord[] words) => new(words, 1280, 720);

    // ---------------- 进入导航前提：主页页签分类 ----------------

    [Fact]
    public void CnHome_MatchTab_ClassifiedAsHomeMatch()
    {
        // 实机 2026-10-17 比赛「比赛」页签：顶部页签行 + 四张卡（我的联赛在右上，
        // 与天梯巅峰赛同排——进入序列中「下」落后需「右」一次）
        var ocr = Ocr(W("推荐 比赛 签约 商店", 0.5, 0.075),
            W("比赛活动", 0.2, 0.69), W("天梯巅峰赛", 0.5, 0.7),
            W("我的联赛 对抗电脑球队提升球队实力", 0.8, 0.41), W("好友对战", 0.8, 0.78));
        Assert.Equal(PlScreen.HomeMatch, PeakLadderBrain.Classify(ocr));
    }

    [Fact]
    public void CnHome_RecommendedTab_ClassifiedAsHomeRecommended()
    {
        var ocr = Ocr(W("推荐 比赛 签约 商店", 0.5, 0.075),
            W("天梯巅峰赛", 0.15, 0.78), W("活动中心", 0.34, 0.78),
            W("球队阵容", 0.5, 0.78), W("我的球队", 0.67, 0.78));
        Assert.Equal(PlScreen.HomeRecommended, PeakLadderBrain.Classify(ocr));
    }

    // ---------------- 兜底：页签行 OCR 漏读时国际服判定落 MatchHub ----------------

    [Fact]
    public void MatchTab_WithMissedTabRow_FallsToIntlMatchHub()
    {
        // 国服没有国际服式"比赛选择页"：页签行词（推荐/签约/商店）任一漏读导致两个国服
        // Brain 都不命中时，国际服 IsMatchHub 仍会凭「比赛」标题行 + 我的联赛磁贴词命中——
        // CnMyLeagueScenario 依赖此行为把 MatchHub 一律改判主页导航（否则共享决策会
        // 对主页盲按 A，落进默认焦点卡「天梯巅峰赛」而非我的联赛）。
        var ocr = Ocr(W("比赛", 0.07, 0.07),
            W("比赛活动", 0.2, 0.69), W("天梯巅峰赛", 0.5, 0.7),
            W("我的联赛", 0.8, 0.41), W("好友对战", 0.8, 0.78));
        Assert.Equal(MlScreen.MatchHub, MyLeagueBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    // ---------------- 进联赛后：共享判定两服一致 ----------------

    [Fact]
    public void LeagueHome_GoMatch_SharedDecisionUnchanged()
    {
        // 联赛主页（前往比赛）：国服同一界面 → LeaguePage + A 推进（继承流程的核心前提）
        var ocr = Ocr(W("我的联赛", 0.2, 0.07), W("前往比赛", 0.14, 0.24),
            W("赛程表", 0.14, 0.38), W("兑换积分", 0.14, 0.52), W("返回", 0.07, 0.94));
        var screen = MyLeagueBrain.Classify(Array.Empty<YoloDetection>(), ocr);
        Assert.Equal(MlScreen.LeaguePage, screen);
        Assert.Equal(MlActionKind.PadConfirm, MyLeagueBrain.Decide(screen, ocr, 0).Kind);
    }

    [Fact]
    public void CnMatchStartPage_ClassifiedForRtBranch()
    {
        // 国服比赛起始页：底栏「设置|球场|下一步」——分类同国际服（下一步 y>0.85 + 球场），
        // 确认键是 RT 而非 A（国服特有），CnMyLeagueScenario 拦截钩子按此改按 RT
        var ocr = Ocr(W("eFootball™ Stadium", 0.5, 0.3), W("球场", 0.62, 0.9),
            W("下一步", 0.85, 0.9));
        Assert.Equal(MlScreen.MatchStartPage,
            MyLeagueBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    // ---------------- 回归 2026-11-11：国服关键比赛弹窗（按钮=确定/取消，A 落在取消上）----------------

    [Fact]
    public void CnKeyMatchPopup_RecognizedOnRealFrame()
    {
        // 实机帧 bot-20261111-150430-1716.yml 1793 行（16:20:48）：弹窗盖在联赛主页上，
        // 正文「即将进行的是关键比赛」+「下一场比赛有固定的赛事等级…」，按钮「确定」y0.59 /
        // 「取消」y0.69。国际服同款弹窗是 OK（HasOkButton）→ KeyMatchDialog→上移+A；
        // 国服换成 确定/取消 后 HasOkButton 判不中，落 HasConfirm 被 PopupBrain 的
        // 「白底=焦点」判成「高光在确定→直接 A」，而实际 A 落在下方取消 → 弹窗关掉回主页，
        // 前往比赛↔弹窗死循环 60+ 轮零场次（用户 2026-11-11 反馈）。
        var ocr = Ocr(W("实况足球在线", 0.5, 0.03), W("前往比赛", 0.14, 0.24),
            W("即将进行的是关键比赛", 0.42, 0.42),
            W("下一场比赛有固定的赛事等级。你将获得额外的我的联赛积分和经验值。", 0.42, 0.50),
            W("传奇：3,640", 0.55, 0.55), W("确定", 0.50, 0.59), W("取消", 0.50, 0.69),
            W("赛事信息", 0.14, 0.66), W("返回", 0.07, 0.94));

        // 实机同帧 YOLO：btn_confirm 0.81（弹窗确认按钮）+ btn_back 0.92
        var dets = new[]
        {
            new YoloDetection("btn_confirm", 1, 0.81f, 0.44, 0.56, 0.12, 0.06),
            new YoloDetection("btn_back", 2, 0.92f, 0.03, 0.90, 0.06, 0.05),
        };

        Assert.True(CnMyLeagueScenario.IsCnKeyMatchPopup(ocr));   // 修复后的判定
        Assert.False(MyLeagueBrain.HasOkButton(ocr));             // 漏接事实：没有 OK 词
        Assert.Equal(MlScreen.HasConfirm,                         // 旧分类落 HasConfirm
            MyLeagueBrain.Classify(dets, ocr));

        // 按钮布局锁定：确定在上、取消在下 → 阶梯第 1 招「上×1+A」方向正确
        var buttons = PopupBrain.FindButtons(null, ocr);
        Assert.Equal(new[] { "确定", "取消" }, buttons.Select(b => b.Label).ToArray());
        Assert.True(buttons[0].IsConfirm);
        Assert.True(buttons[0].CenterY < buttons[1].CenterY);
    }

    [Fact]
    public void LeagueHome_NotMistakenForKeyMatchPopup()
    {
        // 联赛主页（含「确定」这类底栏词也不得误判成关键比赛弹窗）：无「关键比赛」字样
        var ocr = Ocr(W("前往比赛", 0.14, 0.24), W("赛程表", 0.14, 0.38),
            W("兑换积分", 0.14, 0.52), W("赛事信息", 0.14, 0.66),
            W("比赛记录", 0.55, 0.40), W("确定", 0.50, 0.59), W("返回", 0.07, 0.94));
        Assert.False(CnMyLeagueScenario.IsCnKeyMatchPopup(ocr));
    }

    [Fact]
    public void KeyMatchHint_WithoutButton_NotPopup()
    {
        // 主页预告只印「关键比赛」一词（无弹窗正文/按钮）：不按弹窗处置
        var ocr = Ocr(W("前往比赛", 0.14, 0.24), W("关键比赛", 0.55, 0.40),
            W("赛程表", 0.14, 0.38), W("返回", 0.07, 0.94));
        Assert.False(CnMyLeagueScenario.IsCnKeyMatchPopup(ocr));

        // 正文整句漏读时靠按钮词兜底（仍要求 关键比赛 字样）
        var jitter = Ocr(W("前往比赛", 0.14, 0.24), W("关键比赛", 0.55, 0.40),
            W("确定", 0.50, 0.59), W("取消", 0.50, 0.69));
        Assert.True(CnMyLeagueScenario.IsCnKeyMatchPopup(jitter));
    }

    // ---------------- 配置：国服独立配置段 ----------------

    [Fact]
    public void CnMyLeagueConfig_IndependentFromIntl()
    {
        var cfg = new AppConfig
        {
            MyLeague = new MyLeagueConfig { Difficulty = "传奇", StopAfterMatches = 3 },
            CnMyLeague = new MyLeagueConfig { Difficulty = "新手", StopAfterMatches = 5 },
        };
        Assert.Equal("传奇", cfg.MyLeague.Difficulty);
        Assert.Equal("新手", cfg.CnMyLeague.Difficulty);
        Assert.Equal(5, cfg.CnMyLeague.StopAfterMatches);
        Assert.Equal(0, cfg.CnMyLeague.StopAfterKeyMatches); // 未设段回落默认
    }

    // ---------------- 回归 2026-10-17：联赛主页撞 PeakLadder.ActivityHome 兜底 ----------------

    [Fact]
    public void CnLeagueHome_MatchRecordTile_NotTreatedAsLadderLeftover()
    {
        // 实机 2026-10-17：国服联赛主页磁贴叫「比赛记录」（国际服为 赛季纪录/比赛历史），
        // 与「前往比赛」同屏撞 PeakLadderBrain.ActivityHome 兜底（前往比赛+比赛记录）
        // → 曾被拦截钩子当残留页 B 退回主页死循环（进主页即退）。帧里无天梯专属词
        // （本周可参赛场次/天梯巅峰赛）时必须不算天梯页，交还国际服判定 LeaguePage
        var ocr = Ocr(W("我的联赛", 0.2, 0.07), W("前往比赛", 0.14, 0.24),
            W("比赛记录", 0.55, 0.4), W("赛程表", 0.14, 0.38), W("返回", 0.07, 0.94));
        Assert.Equal(PlScreen.ActivityHome, PeakLadderBrain.Classify(ocr)); // 碰撞事实锁定
        Assert.False(CnMyLeagueScenario.HasPeakLadderEvidence(ocr));        // 修复判定

        var leagueHome = MyLeagueBrain.Classify(Array.Empty<YoloDetection>(), ocr);
        Assert.Equal(MlScreen.LeaguePage, leagueHome); // 交还后走联赛主页推进
    }

    [Fact]
    public void LadderPages_WithExclusiveWord_StillLeftover()
    {
        // 场次行或天梯巅峰赛字样在帧上 = 真天梯页，残留页拦截照常 B 退回
        var withCount = Ocr(W("前往比赛", 0.14, 0.24), W("本周可参赛场次: 40", 0.5, 0.35));
        Assert.True(CnMyLeagueScenario.HasPeakLadderEvidence(withCount));

        var withTitle = Ocr(W("教练天梯巅峰赛", 0.3, 0.1), W("前往比赛", 0.14, 0.24),
            W("比赛记录", 0.55, 0.4));
        Assert.True(CnMyLeagueScenario.HasPeakLadderEvidence(withTitle));
        Assert.Equal(PlScreen.ActivityHome, PeakLadderBrain.Classify(withTitle));
    }
}
