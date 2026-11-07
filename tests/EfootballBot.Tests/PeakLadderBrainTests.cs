using EfootballBot.Core.Bot.Scenarios.PeakLadder;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using Xunit;

namespace EfootballBot.Tests;

// 国服天梯巅峰赛决策大脑：界面分类 + 场次解析 + 鼠标点击锚定（合成 OCR 帧，坐标均为归一化值）
public class PeakLadderBrainTests
{
    private static OcrWord W(string text, double cx, double cy)
        => new(text, cx - 0.05, cy - 0.015, 0.1, 0.03);

    private static OcrResult Ocr(params OcrWord[] words) => new(words, 1280, 720);

    private static YoloDetection Box(string cls, double score, double cx, double cy)
        => new(cls, 0, (float)score, cx - 0.05, cy - 0.03, 0.10, 0.06);

    // ---------------- 各界面正判 ----------------

    [Fact]
    public void Classify_HomeMatch_TabBarAndCards()
    {
        // 实机比赛页签：顶部页签行 + 比赛活动/天梯巅峰赛/我的联赛/好友对战 卡片
        var ocr = Ocr(W("推荐 比赛 签约 商店", 0.5, 0.075),
            W("比赛活动", 0.2, 0.69), W("天梯巅峰赛", 0.5, 0.7),
            W("我的联赛", 0.74, 0.41), W("好友对战", 0.74, 0.78));
        Assert.Equal(PlScreen.HomeMatch, PeakLadderBrain.Classify(ocr));
    }

    [Fact]
    public void Classify_HomeRecommended_Tiles()
    {
        var ocr = Ocr(W("推荐 比赛 签约 商店", 0.5, 0.075),
            W("天梯巅峰赛", 0.15, 0.78), W("活动中心", 0.34, 0.78),
            W("我的球队", 0.67, 0.78));
        Assert.Equal(PlScreen.HomeRecommended, PeakLadderBrain.Classify(ocr));
    }

    [Fact]
    public void Classify_ModeSelect_HeaderAndCards()
    {
        // 实机模式选择页：页头 剩余时间:18天 + 天梯赛×2 + 天梯巅峰赛×2（段位名/分数可变不作判定点）
        var ocr = Ocr(W("2026 第5赛季 剩余时间:18天", 0.2, 0.07),
            W("天梯赛-手动", 0.17, 0.31), W("天梯赛-教练", 0.63, 0.31),
            W("天梯巅峰赛-手动", 0.17, 0.63), W("天梯巅峰赛-教练", 0.62, 0.64),
            W("返回", 0.12, 0.94));
        Assert.Equal(PlScreen.ModeSelect, PeakLadderBrain.Classify(ocr));
    }

    [Fact]
    public void Classify_ActivityHome_PlayableCount()
    {
        // 实机活动主页：前往比赛+本周可参赛场次+比赛记录+排名
        var ocr = Ocr(W("教练天梯巅峰赛", 0.2, 0.09), W("活动剩余时间:17天", 0.2, 0.13),
            W("前往比赛", 0.14, 0.25), W("本周可参赛场次: 40", 0.2, 0.28),
            W("比赛记录", 0.2, 0.5), W("排名", 0.2, 0.79), W("返回", 0.12, 0.94));
        Assert.Equal(PlScreen.ActivityHome, PeakLadderBrain.Classify(ocr));
    }

    [Fact]
    public void Classify_ActivityHome_FallbackWithoutCountLine()
    {
        // 本周可参赛场次行 OCR 漏读时：前往比赛+比赛记录 兜底
        var ocr = Ocr(W("教练天梯巅峰赛", 0.2, 0.09),
            W("前往比赛", 0.14, 0.25), W("比赛记录", 0.2, 0.5), W("排名", 0.2, 0.79));
        Assert.Equal(PlScreen.ActivityHome, PeakLadderBrain.Classify(ocr));
    }

    [Fact]
    public void Classify_ModeDialog_Title()
    {
        // 实机参赛模式弹窗：请选择参赛模式 + 单场比赛/连续比赛/取消（段位/分数在背景不作判定点）
        var ocr = Ocr(W("请选择参赛模式", 0.5, 0.2),
            W("即刻进入教练天梯巅峰赛", 0.5, 0.34), W("当前剩余参赛场次: 40", 0.5, 0.39),
            W("单场比赛", 0.5, 0.53), W("连续比赛", 0.5, 0.63), W("取消", 0.5, 0.72));
        Assert.Equal(PlScreen.ModeDialog, PeakLadderBrain.Classify(ocr));
    }

    [Fact]
    public void Classify_ModeDialog_TwoOptions()
    {
        // 标题漏读：单场+连续两选项同屏兜底
        var ocr = Ocr(W("单场比赛", 0.5, 0.53), W("连续比赛", 0.5, 0.63), W("取消", 0.5, 0.72));
        Assert.Equal(PlScreen.ModeDialog, PeakLadderBrain.Classify(ocr));
    }

    // ---------------- 互斥（关键：同类词跨页出现） ----------------

    [Fact]
    public void Classify_DialogWinsOverActivityHome()
    {
        // 弹窗盖在活动主页上：背景的 本周可参赛场次/前往比赛 透出也不得判回主页
        var ocr = Ocr(W("请选择参赛模式", 0.5, 0.2),
            W("当前剩余参赛场次: 40", 0.5, 0.39),
            W("单场比赛", 0.5, 0.53), W("连续比赛", 0.5, 0.63), W("取消", 0.5, 0.72),
            W("前往比赛", 0.14, 0.25), W("本周可参赛场次: 40", 0.2, 0.28));
        Assert.Equal(PlScreen.ModeDialog, PeakLadderBrain.Classify(ocr));
    }

    [Fact]
    public void Classify_ActivityHomeWinsOverModeSelect()
    {
        // 活动主页标题含 教练天梯巅峰赛+活动剩余时间（两个 ModeSelect 特征词都在），
        // 但 本周可参赛场次 在前 → 必须判活动主页
        var ocr = Ocr(W("教练天梯巅峰赛", 0.2, 0.09), W("活动剩余时间:17天", 0.2, 0.13),
            W("前往比赛", 0.14, 0.25), W("本周可参赛场次: 40", 0.2, 0.28),
            W("比赛记录", 0.2, 0.5), W("排名", 0.2, 0.79));
        Assert.Equal(PlScreen.ActivityHome, PeakLadderBrain.Classify(ocr));
    }

    [Fact]
    public void Classify_HomeRecommended_NotHomeMatch()
    {
        // 推荐页签没有比赛板块卡片词（好友对战/我的联盟/比赛活动），不得判比赛页签
        var ocr = Ocr(W("推荐 比赛 签约 商店", 0.5, 0.075),
            W("活动中心", 0.34, 0.78), W("我的球队", 0.67, 0.78));
        Assert.Equal(PlScreen.HomeRecommended, PeakLadderBrain.Classify(ocr));
    }

    [Fact]
    public void Classify_None_ForUnrelatedScreens()
    {
        var ocr = Ocr(W("eFootball", 0.5, 0.5), W("16", 0.3, 0.6));
        Assert.Equal(PlScreen.None, PeakLadderBrain.Classify(ocr));
    }

    // ---------------- 场次解析 ----------------

    [Fact]
    public void ParsePlayableCount_ActivityHome_40()
    {
        var ocr = Ocr(W("前往比赛", 0.14, 0.25), W("本周可参赛场次: 40", 0.2, 0.28));
        Assert.Equal(40, PeakLadderBrain.ParsePlayableCount(ocr));
    }

    [Fact]
    public void ParsePlayableCount_Zero()
    {
        var ocr = Ocr(W("本周可参赛场次: 0", 0.2, 0.28));
        Assert.Equal(0, PeakLadderBrain.ParsePlayableCount(ocr));
    }

    [Fact]
    public void ParsePlayableCount_SingleDigit()
    {
        var ocr = Ocr(W("本周可参赛场次: 3", 0.2, 0.28));
        Assert.Equal(3, PeakLadderBrain.ParsePlayableCount(ocr));
    }

    [Fact]
    public void ParsePlayableCount_DialogFallback()
    {
        // 活动主页行漏读时用弹窗的 当前剩余参赛场次 行
        var ocr = Ocr(W("当前剩余参赛场次: 39", 0.5, 0.39));
        Assert.Equal(39, PeakLadderBrain.ParsePlayableCount(ocr));
    }

    [Fact]
    public void ParsePlayableCount_Missing_ReturnsNull()
    {
        var ocr = Ocr(W("前往比赛", 0.14, 0.25), W("比赛记录", 0.2, 0.5));
        Assert.Null(PeakLadderBrain.ParsePlayableCount(ocr));
    }

    [Fact]
    public void ParsePlayableCount_SplitDigits()
    {
        // OCR 常把行拆成两词：标签词与数字词分立（Lines 聚合后同一行）
        var label = new OcrWord("本周可参赛场次:", 0.10, 0.265, 0.15, 0.03);
        var num = new OcrWord("40", 0.27, 0.265, 0.03, 0.03);
        var ocr = new OcrResult(new[] { label, num }, 1280, 720);
        Assert.Equal(40, PeakLadderBrain.ParsePlayableCount(ocr));
    }

    [Fact]
    public void ParsePlayableCount_DialogOverHome_MergedReaderLine()
    {
        // 弹窗盖在主页上时合并行：本周可参赛场次0 + 弹窗词混入同一行 → 仍读出 0
        // （实机 2026-10-14 13:37：40 场打完后该行被误读为 99 导致循环点前往比赛）
        var ocr = Ocr(W("本周可参赛场次0请选择参赛模式克坎通纳最佳球员", 0.5, 0.28),
            W("请选择参赛模式", 0.4, 0.30), W("单场比赛", 0.5, 0.53), W("取消", 0.5, 0.72));
        Assert.Equal(0, PeakLadderBrain.ParsePlayableCount(ocr));
    }

    // ---------------- 天梯网页层结算页（2026-11-07 反馈包「右下角的下一步点不到」） ----------------

    [Fact]
    public void Classify_RankProgress_SilverPage()
    {
        // 实机 2026-11-07 12:53 卡死帧：白银联赛 / 天梯积分 1500 / 1600（升级）/ 右下「下一步」
        var ocr = Ocr(W("白银联赛", 0.5, 0.65), W("天梯积分：1500", 0.5, 0.74),
            W("1500", 0.32, 0.80), W("1600（▲升级)", 0.62, 0.80), W("下一步", 0.88, 0.925));
        Assert.Equal(PlScreen.RankProgress, PeakLadderBrain.Classify(ocr));
    }

    [Fact]
    public void Classify_RankProgress_WithoutButtonText_UsesBottomConfirmBox()
    {
        // 同一页 OCR 整帧漏读「下一步」（实机 12:53:47 只剩 白银联赛/天梯积分）：
        // 靠右下角的 btn_confirm 框仍能判出这一页，否则又会落回共享流程空按 A
        var ocr = Ocr(W("白银联赛", 0.5, 0.65), W("天梯积分：1500", 0.5, 0.74));
        var dets = new[] { Box("btn_confirm", 0.92, 0.88, 0.88), Box("btn_back", 0.90, 0.08, 0.08) };
        Assert.Equal(PlScreen.RankProgress, PeakLadderBrain.Classify(ocr, dets));

        // 没有这枚右下框就宁可判 None 交共享流程——绝不在任意「天梯积分」帧上乱点
        Assert.Equal(PlScreen.None, PeakLadderBrain.Classify(ocr));
    }

    [Fact]
    public void Classify_ActivityHome_TierPointsIsNotRankProgress()
    {
        // 活动主页同样印着「天梯积分：1200」「1300（▲升级）」：必须仍判活动主页
        var ocr = Ocr(W("教练天梯巅峰赛", 0.2, 0.09), W("前往比赛", 0.14, 0.25),
            W("本周可参赛场次: 40", 0.2, 0.28), W("比赛记录", 0.2, 0.5),
            W("天梯积分：1200", 0.5, 0.76), W("1300（▲升级)", 0.62, 0.80));
        Assert.Equal(PlScreen.ActivityHome, PeakLadderBrain.Classify(ocr));
    }

    [Fact]
    public void Classify_MatchSummary_PopupOverActivityHome()
    {
        // 实机 2026-11-07 12:15：赛后小结弹窗盖在活动主页上（背景 前往比赛/本周可参赛场次 都读得到）
        var ocr = Ocr(W("教练天梯巅峰赛", 0.2, 0.09), W("前往比赛", 0.14, 0.25),
            W("本周可参赛场次", 0.2, 0.28), W("最佳球员", 0.3, 0.45),
            W("本次段位分+240", 0.5, 0.60), W("确认", 0.62, 0.80), W("返回", 0.1, 0.94));
        Assert.Equal(PlScreen.MatchSummary, PeakLadderBrain.Classify(ocr));
    }

    [Fact]
    public void Classify_MatchSummary_FallbackWords()
    {
        // 「本次段位分」漏读时：最佳球员 + 连续比赛已完成 同屏兜底
        var ocr = Ocr(W("最佳球员", 0.3, 0.45), W("连续比赛已完成", 0.5, 0.55), W("确认", 0.62, 0.80));
        Assert.Equal(PlScreen.MatchSummary, PeakLadderBrain.Classify(ocr));
    }

    [Fact]
    public void FindAdvanceWord_PrefersBottomMostButton()
    {
        var ocr = Ocr(W("下一步", 0.88, 0.925), W("确认", 0.62, 0.80), W("前往比赛", 0.14, 0.25));
        var hit = PeakLadderBrain.FindAdvanceWord(ocr, "下一步");
        Assert.NotNull(hit);
        Assert.True(hit!.CenterX > 0.8 && hit.CenterY > 0.9);
        // 上半屏的卡面词不得被当成按钮（防误点「前往比赛」重新开赛）
        Assert.Null(PeakLadderBrain.FindAdvanceWord(ocr, "前往比赛"));
    }

    [Fact]
    public void FindBottomConfirm_OnlyBottomRightConfirmBox()
    {
        var dets = new[]
        {
            Box("btn_confirm", 0.65, 0.50, 0.50), // 画面中部：不是右下按钮
            Box("btn_confirm", 0.40, 0.30, 0.88), // 左下：排除
            Box("btn_confirm", 0.93, 0.88, 0.88), // 右下：命中
            Box("btn_back", 0.97, 0.95, 0.12),    // 返回键类别不对
        };
        var hit = PeakLadderBrain.FindBottomConfirm(dets);
        Assert.NotNull(hit);
        Assert.True(hit!.CenterX > 0.8 && hit.CenterY > 0.8);
        Assert.Null(PeakLadderBrain.FindBottomConfirm(Array.Empty<YoloDetection>()));
    }

    [Fact]
    public void FindOptionWord_DialogSingleMatch()
    {
        var ocr = Ocr(W("请选择参赛模式", 0.5, 0.2), W("单场比赛", 0.5, 0.53),
            W("连续比赛", 0.5, 0.63), W("取消", 0.5, 0.72));
        var hit = PeakLadderBrain.FindOptionWord(ocr, "单场比赛");
        Assert.NotNull(hit);
        Assert.InRange(hit!.CenterY, 0.5, 0.6);
    }

    // ---------------- 鼠标点击锚定 ----------------

    [Fact]
    public void FindCoachCardWord_PicksRightCard()
    {
        // 左「-手动」卡 cx<0.5 必须排除；右「-教练」卡命中
        var ocr = Ocr(W("天梯巅峰赛-手动", 0.17, 0.63), W("天梯巅峰赛-教练", 0.62, 0.64));
        var hit = PeakLadderBrain.FindCoachCardWord(ocr);
        Assert.NotNull(hit);
        Assert.True(hit!.CenterX > 0.5);
    }

    [Fact]
    public void FindCoachCardWord_MergedSpanningLine_Excluded()
    {
        // 整行合并词横跨左右两卡（cx≈0.42<0.5）：排除，不误点手动卡
        var ocr = Ocr(W("天梯巅峰赛手动天梯巅峰赛教练", 0.42, 0.62));
        Assert.Null(PeakLadderBrain.FindCoachCardWord(ocr));
    }

    [Fact]
    public void FindCoachCardWord_IgnoresTopTitle()
    {
        // 页头「2026 第5赛季」区域的天梯赛词（y<0.45）不参与
        var ocr = Ocr(W("天梯巅峰赛-手动", 0.17, 0.31), W("天梯巅峰赛-教练", 0.63, 0.31));
        Assert.Null(PeakLadderBrain.FindCoachCardWord(ocr));
    }

    [Fact]
    public void FindGoMatchWord_FindsLeftCard()
    {
        var ocr = Ocr(W("前往比赛", 0.14, 0.25), W("本周可参赛场次: 40", 0.2, 0.28));
        var hit = PeakLadderBrain.FindGoMatchWord(ocr);
        Assert.NotNull(hit);
        Assert.True(hit!.CenterY < 0.45);
    }
}
