using EfootballBot.Core.Bot.Scenarios.CnWeekly;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using Xunit;

namespace EfootballBot.Tests;

// 国服周活动决策大脑：界面分类规则（合成 OCR 帧；实机截图回归见 CnWeeklyRealFrameTests）
public class CnWeeklyBrainTests
{
    private static OcrWord W(string text, double cx, double cy)
        => new(text, cx - 0.05, cy - 0.015, 0.1, 0.03);

    private static OcrResult Ocr(params OcrWord[] words) => new(words, 1280, 720);

    // ---------------- 各界面正判 ----------------

    [Fact]
    public void Classify_HomeRecommended_TabBarAndTiles()
    {
        // 实机 cn_home_recommended：顶部页签行 推荐|比赛|签约|商店 + 磁贴 活动中心/我的球队
        var ocr = Ocr(W("推荐 比赛 签约 商店", 0.5, 0.075),
            W("天梯巅峰赛", 0.15, 0.78), W("活动中心", 0.34, 0.78),
            W("球队阵容", 0.5, 0.78), W("我的球队", 0.67, 0.78), W("设置", 0.85, 0.78));
        Assert.Equal(CwScreen.HomeRecommended, CnWeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_HomeMatch_TabBarAndMatchCards()
    {
        // 实机 cn_home_match：比赛页签内容卡 比赛活动/我的联赛/好友对战
        var ocr = Ocr(W("推荐 比赛 签约 商店", 0.5, 0.075),
            W("比赛活动", 0.2, 0.69), W("天梯巅峰赛", 0.5, 0.7),
            W("我的联赛", 0.74, 0.41), W("好友对战", 0.74, 0.78));
        Assert.Equal(CwScreen.HomeMatch, CnWeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_EventsList_Tabs()
    {
        var ocr = Ocr(W("对阵电脑", 0.39, 0.077), W("对阵玩家", 0.63, 0.077),
            W("实况足球 冠军赛", 0.2, 0.27), W("进入", 0.2, 0.79), W("返回", 0.1, 0.93));
        Assert.Equal(CwScreen.EventsList, CnWeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_EventIntro_EnterAndDetails()
    {
        // 介绍页：进入+详情 按钮同屏（列表卡只有「进入」，不冲突）
        var ocr = Ocr(W("实况足球 冠军赛", 0.21, 0.12), W("剩余时间:2天11小时", 0.17, 0.19),
            W("进入", 0.26, 0.69), W("详情", 0.26, 0.78), W("关闭", 0.91, 0.075));
        Assert.Equal(CwScreen.EventIntro, CnWeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_Bracket_ResetBar()
    {
        // 对阵表：底栏 X 重置 + RT 下一步（活动名不作判定点）
        var ocr = Ocr(W("冠军杯 实况足球 冠军赛", 0.19, 0.05),
            W("小组赛1", 0.14, 0.18), W("决赛", 0.86, 0.18),
            W("比赛尚未开始", 0.5, 0.42),
            W("返回", 0.12, 0.93), W("重置", 0.5, 0.93), W("下一步", 0.9, 0.93));
        Assert.Equal(CwScreen.Bracket, CnWeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_EventHome_CoachEntry()
    {
        // 活动主页：RT 教练入场（底栏 y≈0.93）+ 左栏磁贴 + 右栏下场比赛
        var ocr = Ocr(W("球队阵容", 0.18, 0.21), W("比赛历史", 0.18, 0.47), W("建议", 0.18, 0.72),
            W("下场比赛:小组赛第1场", 0.64, 0.28), W("下个对手", 0.45, 0.53),
            W("返回", 0.12, 0.93), W("积分情况", 0.5, 0.93), W("教练入场", 0.9, 0.93));
        Assert.Equal(CwScreen.EventHome, CnWeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_EventHome_FallbackWithoutCoachEntry()
    {
        // 教练入场 OCR 漏读：左栏 比赛历史+建议 与右栏 下场比赛 同屏兜底
        var ocr = Ocr(W("球队阵容", 0.18, 0.21), W("比赛历史", 0.18, 0.47), W("建议", 0.18, 0.72),
            W("下场比赛:小组赛第1场", 0.64, 0.28), W("下个对手", 0.45, 0.53));
        Assert.Equal(CwScreen.EventHome, CnWeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_Squad_LineupList()
    {
        // 实机 cn_squad：左上 球队阵容列表 + 标题 球员加分 + 底栏 RS 自动配置球员 + B 完成
        var ocr = Ocr(W("球队阵容列表", 0.11, 0.07), W("球员加分: 7.7", 0.29, 0.07),
            W("完成", 0.92, 0.075), W("自动配置球员", 0.89, 0.93), W("战术", 0.11, 0.35));
        Assert.Equal(CwScreen.Squad, CnWeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_SquadMenu_BeatsSquad()
    {
        // RS 菜单盖在阵容页上：阵容页词透过暗化背景仍可读，必须先判菜单
        var ocr = Ocr(W("球队阵容列表", 0.11, 0.07), W("球员加分: 7.7", 0.29, 0.07),
            W("无变更", 0.86, 0.54), W("重视能力值", 0.86, 0.61),
            W("重视能力值 / 心有灵犀", 0.86, 0.68), W("活动加成", 0.86, 0.74),
            W("基础球队优先", 0.86, 0.82));
        Assert.Equal(CwScreen.SquadMenu, CnWeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    // ---------------- 门控与坑 ----------------

    [Fact]
    public void Classify_PopupOverEventHome_GatedToSharedFlow()
    {
        // 赛后弹窗链（用户国际服 2026-10-12 实机教训同款）：主页词透过暗化背景仍可读，
        // 有中部推进词必须交共享流程按 A 清弹窗，不能判回 EventHome 反复按 RT
        var ocr = Ocr(W("球队阵容", 0.18, 0.21), W("比赛历史", 0.18, 0.47),
            W("下场比赛:小组赛第1场", 0.64, 0.28), W("教练入场", 0.9, 0.93),
            W("下一步", 0.5, 0.55));
        Assert.Equal(CwScreen.None, CnWeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_Bracket_NotGatedByBottomBarNextWord()
    {
        // 对阵表底栏「RT 下一步」在 y≈0.93：推进词限 y<0.82，干净对阵表不得被门控成弹窗
        var ocr = Ocr(W("冠军杯 实况足球 冠军赛", 0.19, 0.05),
            W("小组赛1", 0.14, 0.18), W("比赛尚未开始", 0.5, 0.42),
            W("返回", 0.12, 0.93), W("重置", 0.5, 0.93), W("下一步", 0.9, 0.93));
        Assert.Equal(CwScreen.Bracket, CnWeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_EventNameNeverAnAnchor()
    {
        // 可变内容禁令：活动名「实况足球 冠军赛」「决战32强」单独出现不构成任何界面
        var ocr = Ocr(W("实况足球 冠军赛", 0.5, 0.3), W("决战32强 传奇由此诞生", 0.5, 0.5));
        Assert.Equal(CwScreen.None, CnWeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_TopTabsRequireTopRegion()
    {
        // 页签词只在顶部（y<0.15）才算主页：正文里偶然出现 推荐/签约 字样不构成主页
        var ocr = Ocr(W("推荐", 0.5, 0.5), W("签约", 0.6, 0.6), W("商店", 0.7, 0.7),
            W("活动中心", 0.3, 0.3));
        Assert.Equal(CwScreen.None, CnWeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_SquadMenuAloneNotEnough()
    {
        // 只有「活动加成」无「无变更」：不构成 RS 菜单（防单词误判）
        var ocr = Ocr(W("活动加成", 0.5, 0.5));
        Assert.Equal(CwScreen.None, CnWeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }
}
