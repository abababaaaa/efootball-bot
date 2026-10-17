using EfootballBot.Core.Bot.Scenarios.CnWeekly;
using EfootballBot.Core.Capture;
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

    /// <summary>大字卡号词（国服挑战清单 3 卡布局编号 H≈0.09，普通文字 H≈0.05）。</summary>
    private static OcrWord BigW(string text, double cx, double cy, double h = 0.09)
        => new(text, cx - 0.04, cy - h / 2, 0.08, h);

    /// <summary>黑底测试帧（200×120，探针区域裁剪安全）。</summary>
    private static FrameData BlackFrame()
        => new(new byte[4 * 200 * 120], 200, 120, 0);

    private static void PaintRect(FrameData f, double x0, double x1, double y0, double y1, byte b, byte g, byte r)
    {
        for (int y = (int)(f.Height * y0); y < (int)(f.Height * y1); y++)
            for (int x = (int)(f.Width * x0); x < (int)(f.Width * x1); x++)
            {
                int i = (y * f.Width + x) * 4;
                f.Bgra[i] = b; f.Bgra[i + 1] = g; f.Bgra[i + 2] = r;
            }
    }

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

    // ---------------- 巡回赛型活动（2026-10-17 实机新增） ----------------

    [Fact]
    public void Classify_TourHome_GoMatchPlusSettingsBar()
    {
        // 巡回赛主页：主按钮 前往比赛 + 底栏 X 活动设置 + 比赛历史/建议（无 教练入场/下场比赛）
        var ocr = Ocr(W("球星训练 巡回赛", 0.3, 0.09),
            W("前往比赛", 0.18, 0.24), W("总活动积分", 0.5, 0.21),
            W("比赛历史", 0.18, 0.47), W("建议", 0.18, 0.79),
            W("返回", 0.14, 0.93), W("活动设置", 0.42, 0.93));
        Assert.Equal(CwScreen.EventHomeTour, CnWeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_EventSettings_BeatsTourHome()
    {
        // 设置面板盖在主页右半：主页词透暗背景仍可读，必须先判面板（实机 cn_tour_event_settings）
        var ocr = Ocr(W("前往比赛", 0.18, 0.24), W("比赛历史", 0.18, 0.47),
            W("返回", 0.14, 0.93), W("活动设置", 0.42, 0.93),
            W("活动设置", 0.75, 0.09), W("选择的球队", 0.75, 0.23), W("AC米兰", 0.75, 0.26),
            W("教练模式", 0.75, 0.36), W("开", 0.76, 0.42),
            W("教练模式内控制", 0.75, 0.46), W("完全托管", 0.76, 0.52),
            W("完成", 0.75, 0.93));
        Assert.Equal(CwScreen.EventSettings, CnWeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_PopupOverTourHome_GatedToSharedFlow()
    {
        // 弹窗盖在巡回赛主页上：主页词透背景仍可读，有中部推进词交共享流程按 A，不得判回主页
        var ocr = Ocr(W("前往比赛", 0.18, 0.24), W("活动设置", 0.42, 0.93),
            W("比赛历史", 0.18, 0.47),
            W("下一步", 0.5, 0.55));
        Assert.Equal(CwScreen.None, CnWeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_SettingsPanelAloneNotEnough()
    {
        // 只有「教练模式」无面板佐证词：不构成设置面板（防其他页面偶然出现教练模式字样）
        var ocr = Ocr(W("教练模式", 0.5, 0.5));
        Assert.Equal(CwScreen.None, CnWeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void TourSettingsReady_RequiresCoachOnAndFullAuto()
    {
        // 实机排版：标签行 + 值 pill 行分开，值为单字
        var ready = Ocr(W("活动设置", 0.75, 0.09), W("选择的球队", 0.75, 0.23),
            W("教练模式", 0.75, 0.36), W("开", 0.76, 0.42),
            W("教练模式内控制", 0.75, 0.46), W("完全托管", 0.76, 0.52));
        Assert.True(CnWeeklyBrain.TourSettingsReady(ready));

        // 教练模式=关：缺「开」词 → 未就绪
        var coachOff = Ocr(W("教练模式", 0.75, 0.36), W("关", 0.76, 0.42),
            W("教练模式内控制", 0.75, 0.46), W("完全托管", 0.76, 0.52));
        Assert.False(CnWeeklyBrain.TourSettingsReady(coachOff));

        // 内控制≠完全托管 → 未就绪
        var noFullAuto = Ocr(W("教练模式", 0.75, 0.36), W("开", 0.76, 0.42),
            W("教练模式内控制", 0.75, 0.46), W("部分托管", 0.76, 0.52));
        Assert.False(CnWeeklyBrain.TourSettingsReady(noFullAuto));

        // OCR 把标签与值并成一行（「教练模式开」）也认得出；含内控制的行不误报
        var merged = Ocr(W("教练模式开", 0.75, 0.39), W("教练模式内控制完全托管", 0.75, 0.49));
        Assert.True(CnWeeklyBrain.TourSettingsReady(merged));
    }

    // ---------------- 挑战清单选卡页（2026-10-17 实机新增） ----------------

    [Fact]
    public void Classify_ChallengeList_Title()
    {
        // 实机 cn_challenge_list：标题 挑战清单 + 编号卡 + 底栏 B 返回
        var ocr = Ocr(W("挑战清单", 0.13, 0.11),
            W("以1分或以上为目标", 0.2, 0.4), W("比赛次数上限:3", 0.15, 0.57),
            W("业余", 0.13, 0.59), W("返回", 0.1, 0.93));
        Assert.Equal(CwScreen.ChallengeList, CnWeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_ChallengeList_FallbackCardNumbers()
    {
        // 标题漏读兜底：≥2 枚大字卡号 + 底栏返回（普通文字 H≈0.05 不构成卡号）
        var ocr = Ocr(BigW("01", 0.115, 0.28), BigW("02", 0.425, 0.28), BigW("03", 0.72, 0.28),
            W("以1分或以上为目标", 0.2, 0.4), W("业余", 0.13, 0.59), W("返回", 0.1, 0.93));
        Assert.Equal(CwScreen.ChallengeList, CnWeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_ChallengeList_NumbersAloneNotEnough()
    {
        // 只有一枚大字数字 + 返回：不构成清单（防其他页大号数字误判）
        var ocr = Ocr(BigW("01", 0.115, 0.28), W("返回", 0.1, 0.93));
        Assert.Equal(CwScreen.None, CnWeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_SmallProgressTextNotCardNumber()
    {
        // 普通小字里的两位数字（H≈0.05，如比分/进度）不得当卡号特征
        var ocr = Ocr(W("01", 0.115, 0.28), W("02", 0.425, 0.28), W("返回", 0.1, 0.93));
        Assert.Equal(CwScreen.None, CnWeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_PopupOverChallengeList_GatedToSharedFlow()
    {
        // 弹窗盖在清单上：清单词透暗背景仍可读，有中部推进词交共享流程按 A 清弹窗
        var ocr = Ocr(W("挑战清单", 0.13, 0.11), W("以1分或以上为目标", 0.2, 0.4),
            W("返回", 0.1, 0.93), W("下一步", 0.5, 0.55));
        Assert.Equal(CwScreen.None, CnWeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void ScanChallengeCards_DynamicThreeSlots_LockAndCheck()
    {
        // 实机 3 卡布局：卡 1 右上绿勾（已通关）、卡 2 右下灰锁、卡 3 待挑战。
        // 卡右缘由下一张卡号推导（不绑定固定卡位坐标）
        var frame = BlackFrame();
        PaintRect(frame, 0.575, 0.645, 0.750, 0.860, 128, 128, 128); // 卡 2 右下灰锁（卡 2 探针区内）
        PaintRect(frame, 0.275, 0.360, 0.160, 0.295, 0, 200, 0);     // 卡 1 右上绿勾（卡 1 探针区内）
        var ocr = Ocr(BigW("01", 0.115, 0.28), BigW("02", 0.425, 0.28), BigW("03", 0.72, 0.28));
        var cards = CnWeeklyBrain.ScanChallengeCards(ocr, frame);

        Assert.Equal(3, cards.Count);
        Assert.Equal((1, 0, false, true), (cards[0].Index, cards[0].Slot, cards[0].Locked, cards[0].Checked));
        Assert.Equal((2, 1, true, false), (cards[1].Index, cards[1].Slot, cards[1].Locked, cards[1].Checked));
        Assert.Equal((3, 2, false, false), (cards[2].Index, cards[2].Slot, cards[2].Locked, cards[2].Checked));
    }

    [Fact]
    public void ScanChallengeCards_FiveCardLayout_NoFixedSlotCount()
    {
        // 5 卡布局（用户明示卡数不固定）：小间距卡号全部识别，次序按 x 排列；
        // 黑底上不得误报锁/勾
        var frame = BlackFrame();
        var ocr = Ocr(
            BigW("01", 0.09, 0.28), BigW("02", 0.28, 0.28), BigW("03", 0.47, 0.28),
            BigW("04", 0.66, 0.28), BigW("05", 0.85, 0.28));
        var cards = CnWeeklyBrain.ScanChallengeCards(ocr, frame);

        Assert.Equal(5, cards.Count);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(i + 1, cards[i].Index);
            Assert.Equal(i, cards[i].Slot);
            Assert.False(cards[i].Locked);
            Assert.False(cards[i].Checked);
        }
    }

    [Fact]
    public void ScanChallengeCards_NumberNormAndDedup()
    {
        // "O1"（0/O 混淆）归一成卡 1；同卡号重复词去重；两位小字不当作卡号
        var frame = BlackFrame();
        var ocr = Ocr(BigW("O1", 0.115, 0.28), BigW("01", 0.117, 0.285), W("02", 0.425, 0.29));
        var cards = CnWeeklyBrain.ScanChallengeCards(ocr, frame);

        Assert.Single(cards);
        Assert.Equal(1, cards[0].Index);
        Assert.Equal(0, cards[0].Slot);
    }

    // ---------------- 选择比赛难度页（2026-10-17 实机新增） ----------------

    [Fact]
    public void Classify_TourDifficulty_Title()
    {
        // 实机 cn_tour_difficulty：标题 选择比赛难度 + 难度卡行 + 底栏 B 返回/RT 确定
        var ocr = Ocr(W("选择比赛难度", 0.18, 0.11),
            W("新手", 0.10, 0.31), W("业余", 0.32, 0.31), W("标准", 0.54, 0.30),
            W("职业", 0.76, 0.31), W("顶尖球", 0.97, 0.31),
            W("返回", 0.09, 0.93), W("确定", 0.94, 0.93));
        Assert.Equal(CwScreen.Difficulty, CnWeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_TourDifficulty_FallbackCardRow()
    {
        // 标题漏读兜底：同行 ≥3 个难度名 + 确定词（国服卡名 顶尖球星 前缀归一到 顶尖球员）
        var ocr = Ocr(W("新手", 0.10, 0.31), W("业余", 0.32, 0.31), W("标准", 0.54, 0.30),
            W("顶尖球星", 0.97, 0.31), W("返回", 0.09, 0.93), W("确定", 0.94, 0.93));
        Assert.Equal(CwScreen.Difficulty, CnWeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_SingleDifficultyWordNotCardRow()
    {
        // 单个难度词 + 确定：不构成难度卡行（防球员评价等页面偶然出现难度字样误判）
        var ocr = Ocr(W("标准", 0.5, 0.31), W("确定", 0.94, 0.93));
        Assert.Equal(CwScreen.None, CnWeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void DetectSelectedCnDifficulty_BrightBodyCard()
    {
        // 光标所在卡白底高亮：卡体采样区刷白者=选中；全暗 = 未检出（调用方直接确认不导航）
        var frame = BlackFrame();
        PaintRect(frame, 0.265, 0.375, 0.475, 0.565, 230, 230, 230); // 业余卡体整块刷白（盖满采样区）
        var ocr = Ocr(W("新手", 0.10, 0.31), W("业余", 0.32, 0.31), W("标准", 0.54, 0.30));
        Assert.Equal("业余", CnWeeklyBrain.DetectSelectedCnDifficulty(frame, ocr));

        Assert.Null(CnWeeklyBrain.DetectSelectedCnDifficulty(BlackFrame(),
            Ocr(W("新手", 0.10, 0.31), W("业余", 0.32, 0.31), W("标准", 0.54, 0.30))));
    }

    // ---------------- i 信息弹窗（主要奖励 x/x） ----------------

    [Fact]
    public void Classify_RewardPopup_MainRewardPlusDescription()
    {
        // 实机 cn_tour_reward_popup：主要奖励 头 + 活动说明（主页透底的 获得的主要奖励
        // 无 活动说明，不冲突）
        var ocr = Ocr(W("球星训练 巡回赛", 0.3, 0.09), W("主要奖励", 0.11, 0.20),
            W("金币", 0.15, 0.36), W("60/120", 0.17, 0.42),
            W("高光定向券", 0.55, 0.36), W("1/2", 0.55, 0.42),
            W("活动时间", 0.10, 0.57), W("活动说明", 0.10, 0.70), W("关闭", 0.93, 0.07));
        Assert.Equal(CwScreen.RewardPopup, CnWeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void ParseMainRewardProgress_AllDoneAndDateExclusion()
    {
        // 全满：2/2 + 120/120 → AllDone；活动日期 2026/10/16（三段）不得被当进度词；
        // 只有 主要奖励 头读不到任何 N/M 词 → 未读出（调用方按未拿满继续刷）
        var allDone = Ocr(W("主要奖励", 0.11, 0.20),
            W("金币", 0.15, 0.36), W("120/120", 0.17, 0.42),
            W("高光定向券", 0.55, 0.36), W("2/2", 0.55, 0.42),
            W("活动时间", 0.10, 0.57), W("2026/10/16 11:00", 0.18, 0.62));
        var (readable, done) = CnWeeklyBrain.ParseMainRewardProgress(allDone);
        Assert.True(readable);
        Assert.True(done);

        var none = Ocr(W("主要奖励", 0.11, 0.20), W("活动时间", 0.10, 0.57), W("2026/10/16", 0.18, 0.62));
        var (readable2, done2) = CnWeeklyBrain.ParseMainRewardProgress(none);
        Assert.False(readable2);
        Assert.False(done2);

        // 无 主要奖励 头（如主页右栏只有 获得的主要奖励）：不解析
        var noPopup = Ocr(W("获得的主要奖励", 0.74, 0.44), W("1/2", 0.55, 0.42));
        var (readable3, _) = CnWeeklyBrain.ParseMainRewardProgress(noPopup);
        Assert.False(readable3);
    }
}
