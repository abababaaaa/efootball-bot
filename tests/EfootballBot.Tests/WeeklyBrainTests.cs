using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Bot.Scenarios.Weekly;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using Xunit;

namespace EfootballBot.Tests;

// 周活动决策大脑：界面分类规则 + 活动设置解析（合成 OCR 帧；实机截图回归见 WeeklyRealFrameTests）
public class WeeklyBrainTests
{
    private static readonly FrameData TinyFrame = new(new byte[4 * 10 * 10], 10, 10, 0);
    private static OcrWord W(string text, double cx, double cy)
        => new(text, cx - 0.05, cy - 0.015, 0.1, 0.03);

    private static YoloDetection Det(string cls, float score)
        => new(cls, 0, score, 0.4, 0.4, 0.2, 0.1);

    private static YoloDetection DetAt(string cls, float score, double cx, double cy)
        => new(cls, 0, score, cx - 0.1, cy - 0.05, 0.2, 0.1);

    private static OcrResult Ocr(params OcrWord[] words) => new(words, 1280, 720);

    // ---------------- 界面分类 ----------------

    [Fact]
    public void Classify_EventSettings_TitleAtTop()
    {
        // 标题词在顶部（y<0.2）：活动设置浮层（实机 weekly_event_settings）
        var ocr = Ocr(W("活动设置", 0.61, 0.11), W("赛事等级", 0.57, 0.20),
            W("传奇", 0.56, 0.25), W("自动控制", 0.57, 0.47));
        Assert.Equal(WkScreen.EventSettings, WeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_EventHome_NotFooledByBottomBarSettingsWord()
    {
        // 实机回归：活动主页底栏有「X 活动设置」按钮词（y≈0.93），
        // 设置判定不限 y 会把主页误判成设置浮层 → 必须仍判 EventHome
        var ocr = Ocr(W("前往比赛", 0.13, 0.24), W("总活动积分", 0.39, 0.21),
            W("离获得主要奖励还差3000点积分", 0.77, 0.60), W("活动设置", 0.31, 0.93),
            W("比赛计划", 0.58, 0.93));
        Assert.Equal(WkScreen.EventHome, WeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_EntryPopup_BeatsEventHome()
    {
        // 入场弹窗（设置专用比赛计划 + OK）盖在活动主页上：必须先于 EventHome 判定
        var ocr = Ocr(W("设置专用比赛计划", 0.50, 0.40), W("OK", 0.50, 0.60),
            W("前往比赛", 0.13, 0.24), W("总活动积分", 0.39, 0.21));
        Assert.Equal(WkScreen.EntryPopup, WeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_EventHome_WithAdvanceWord_IsEntryPopup()
    {
        // 赛后弹窗链回归（用户 2026-10-12 实机）：弹窗未清完时主页词透过暗化背景仍可读，
        // 误判 EventHome 会按 X/前往比赛全部无效——有推进词必须先判弹窗按 A 清掉
        var ocr = Ocr(W("前往比赛", 0.13, 0.24), W("总活动积分", 0.39, 0.21),
            W("下一步", 0.50, 0.60));
        Assert.Equal(WkScreen.EntryPopup, WeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_EventHome_WithConfirmButton_IsEntryPopup()
    {
        // 弹窗按钮被 YOLO 检出（OCR 漏读按钮词时）：同样先判弹窗（弹窗按钮在屏幕中部）
        var ocr = Ocr(W("前往比赛", 0.13, 0.24), W("总活动积分", 0.39, 0.21));
        var dets = new[] { DetAt("btn_confirm", 0.8f, 0.50, 0.60) };
        Assert.Equal(WkScreen.EntryPopup, WeeklyBrain.Classify(dets, ocr));
    }

    [Fact]
    public void Classify_EventHome_BottomBarConfirm_IsNotPopup()
    {
        // 实机回归（2026-10-13 晚，Titani 帧）：主页底栏「B 返回」蓝色高亮框被 YOLO 判
        // btn_confirm 0.91 @(0.12,0.93)——底栏确认框不是弹窗证据，不得把干净主页门控成
        // EntryPopup（否则 A=前往比赛直接开下一场，完成态永远不被评估）
        var ocr = Ocr(W("前往比赛", 0.13, 0.24), W("总活动积分", 0.39, 0.21),
            W("获得的主要奖励", 0.72, 0.60), W("返回", 0.10, 0.93));
        var dets = new[] { DetAt("btn_confirm", 0.91f, 0.12, 0.93) };
        Assert.Equal(WkScreen.EventHome, WeeklyBrain.Classify(dets, ocr));
    }

    [Fact]
    public void Classify_PitchFrame_WithOkWord_IsNotWeekly()
    {
        // 比赛中弹窗（无主页背景词）：不归周活动界面，交给共享分类（HasConfirm → A）
        var ocr = Ocr(W("OK", 0.50, 0.60), W("今天的进球", 0.5, 0.4));
        Assert.Equal(WkScreen.None, WeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_OkSubstringInEnglishText_DoesNotTriggerPopup()
    {
        //「OK」必须整词精确匹配：英文卡面词/球员名含 "ok" 子串不得当成弹窗按钮
        //（误触发会在纯主页上按 A = 误开赛）
        var ocr = Ocr(W("前往比赛", 0.13, 0.24), W("总活动积分", 0.39, 0.21),
            W("Rookie", 0.5, 0.5));
        Assert.Equal(WkScreen.EventHome, WeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_EventsList_ByTabWords()
    {
        var ocr = Ocr(W("对阵AI", 0.39, 0.08), W("对阵玩家", 0.60, 0.08), W("进入", 0.5, 0.79));
        Assert.Equal(WkScreen.EventsList, WeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_EventIntro_ByTourLabel()
    {
        var ocr = Ocr(W("EnglishClubs巡回活动关闭", 0.48, 0.10), W("进入", 0.26, 0.75), W("详情", 0.26, 0.85));
        Assert.Equal(WkScreen.EventIntro, WeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_EnterWithoutDetails_IsNotIntro()
    {
        // 列表页也有「进入」条：无「详情」不得判介绍页（OCR 漏读对阵AI 时宁可 None 交给共享分类）
        var ocr = Ocr(W("进入", 0.5, 0.79), W("返回", 0.10, 0.93));
        Assert.Equal(WkScreen.None, WeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_MatchHubTiles_ReturnNone_SharedClassifyHandles()
    {
        // 比赛 hub（锦标赛/活动磁贴行）不是周活动界面：交给 MyLeagueBrain 分类
        var ocr = Ocr(W("比赛", 0.10, 0.11), W("锦标赛", 0.11, 0.73), W("活动", 0.40, 0.72));
        Assert.Equal(WkScreen.None, WeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    // ---------------- 活动设置解析 ----------------

    [Fact]
    public void ParseSettings_AllReadable()
    {
        // 坐标取自实机 weekly_event_settings 帧词级数据
        var ocr = Ocr(
            W("赛事等级", 0.57, 0.20), W("传奇", 0.56, 0.25),
            W("选择的球队", 0.57, 0.33), W("GalatasaraySK", 0.60, 0.38),
            W("自动控制", 0.57, 0.47), W("开", 0.56, 0.52),
            W("弹性自动控制", 0.58, 0.60), W("关", 0.56, 0.65));
        var s = WeeklyBrain.ParseActivitySettings(ocr);
        Assert.Equal("传奇", s.Level);
        Assert.True(s.AutoControl);
        Assert.False(s.FlexibleAutoControl);
        Assert.True(s.AllReadable);
    }

    [Fact]
    public void ParseSettings_MissingValue_IsNull_NotGuessed()
    {
        // 值词漏读（如 OCR 丢「关」单字）→ null，绝不猜（保守：读不到就不动）
        var ocr = Ocr(
            W("赛事等级", 0.57, 0.20), W("传奇", 0.56, 0.25),
            W("自动控制", 0.57, 0.47), W("开", 0.56, 0.52),
            W("弹性自动控制", 0.58, 0.60));
        var s = WeeklyBrain.ParseActivitySettings(ocr);
        Assert.Null(s.FlexibleAutoControl);
        Assert.False(s.AllReadable);
    }

    [Fact]
    public void ParseSettings_FlexRowNotTreatedAsAutoRow()
    {
        // 「弹性自动控制」包含「自动控制」子串：普通行缺失时 AutoControl 必须为 null，
        // 不能拿弹性行的值充当普通行（否则会漏改自动控制）
        var ocr = Ocr(W("弹性自动控制", 0.58, 0.60), W("关", 0.56, 0.65));
        var s = WeeklyBrain.ParseActivitySettings(ocr);
        Assert.Null(s.AutoControl);
        Assert.False(s.FlexibleAutoControl);
    }

    [Fact]
    public void ParseSettings_LevelParagraphNotMistakenAsValue()
    {
        // 页面底部的说明文字含难度词（新手/传奇），但不在标签下方 0.13 窗口内，不得当等级值
        var ocr = Ocr(
            W("赛事等级", 0.57, 0.20), W("传奇", 0.56, 0.25),
            W("自动控制", 0.57, 0.47), W("开", 0.56, 0.52),
            W("弹性自动控制", 0.58, 0.60), W("关", 0.56, 0.65),
            W("设置比赛中的AI对手难度新手难度最低传奇难度最高", 0.26, 0.81));
        var s = WeeklyBrain.ParseActivitySettings(ocr);
        Assert.Equal("传奇", s.Level);
    }

    // ---------------- 完成态探针（预留判定） ----------------

    [Fact]
    public void MainRewardPending_ByRightColumnText()
    {
        var ocr = Ocr(W("离获得主要奖励还差3,000点积分", 0.77, 0.60));
        Assert.True(WeeklyBrain.MainRewardPending(ocr));
    }

    [Fact]
    public void MainRewardDone_ByCompletedText()
    {
        // 完成态右栏标题「获得的主要奖励」（2026-10-13 用户实机截图校准）
        var ocr = Ocr(W("获得的主要奖励", 0.72, 0.60), W("随机位置代币×1", 0.78, 0.67));
        Assert.True(WeeklyBrain.IsMainRewardDone(ocr, TinyFrame));
    }

    [Fact]
    public void MainRewardDone_InProgressText_NotFooledBySubstring()
    {
        // 进行中文案「离获得主要奖励还差…」不含「的」字，不得命中完成判定
        var ocr = Ocr(W("离获得主要奖励还差3,000点积分", 0.77, 0.60));
        Assert.False(WeeklyBrain.IsMainRewardDone(ocr, TinyFrame));
    }

    [Fact]
    public void GreenCheck_NoPixels_ReturnsFalse()
    {
        // 无绿像素帧（10x10 黑帧）不判勾
        Assert.False(WeeklyBrain.DetectGreenCheck(TinyFrame, 0));
        // 屏幕外槽位（第 4 卡勾区采样不到）返回 false
        Assert.False(WeeklyBrain.DetectGreenCheck(TinyFrame, 3));
    }
}
