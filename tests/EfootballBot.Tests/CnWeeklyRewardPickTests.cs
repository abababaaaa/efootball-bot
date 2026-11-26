using EfootballBot.Core.Bot.Scenarios.CnWeekly;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using Xunit;

namespace EfootballBot.Tests;

// 国服周活动赛后「活动内升级奖励」两页（2026-11-25 实机）：
//   ① 阵容奖励（六卡评级两两求和，选最高；同分 前锋>中场>后场）
//   ② 特殊奖励（等级 N>40 不选，选 N 最小；同数 前锋>中场>后场）
//   ③ 选完的两个领取结果弹窗（单键「确定」，靠鼠标兜底）
// 帧内容按实机截图标定：面板中心 x=0.2215/0.508/0.776，标题 y=0.271，评级字母 y=0.710；
// 评级色 A 青(16,229,247) / B 绿(14,230,35) / C 黄绿(222,231,15) / D 橙(254,194,21)。
public class CnWeeklyRewardPickTests
{
    private static OcrWord W(string text, double cx, double cy, double h = 0.03)
        => new(text, cx - 0.05, cy - h / 2, 0.1, h);

    private static OcrResult Ocr(params OcrWord[] words) => new(words, 1280, 720);

    private static FrameData Frame(int w = 1280, int h = 720)
        => new(new byte[4 * w * h], w, h, 0);

    private static void Paint(FrameData f, double x0, double x1, double y0, double y1,
        byte b, byte g, byte r)
    {
        for (int y = (int)(f.Height * y0); y < (int)(f.Height * y1); y++)
            for (int x = (int)(f.Width * x0); x < (int)(f.Width * x1); x++)
            {
                int i = (y * f.Width + x) * 4;
                f.Bgra[i] = b; f.Bgra[i + 1] = g; f.Bgra[i + 2] = r;
            }
    }

    /// <summary>在评级字母窗口里涂一块评级色（模拟卡底那枚大字）。</summary>
    private static void PaintGrade(FrameData f, double panelX, int index, char grade)
    {
        (byte B, byte G, byte R) c = grade switch
        {
            'A' => (247, 229, 16),
            'B' => (35, 230, 14),
            'C' => (15, 231, 222),
            _ => (21, 194, 254), // D
        };
        double cx = panelX + (index == 0 ? -0.059 : 0.060);
        Paint(f, cx - 0.012, cx + 0.012, 0.694, 0.726, c.B, c.G, c.R);
    }

    // ---------------- 界面判定 ----------------

    [Fact]
    public void Classify_阵容奖励页_命中SquadRewardPick()
    {
        var ocr = Ocr(
            W("阵容奖励", 0.20, 0.11),
            W("前锋捆绑包", 0.218, 0.271), W("中场捆绑包", 0.498, 0.271), W("后场捆绑包", 0.780, 0.271),
            W("选择一个阵容强化奖励", 0.500, 0.829), W("下一步", 0.878, 0.936));
        Assert.True(CnWeeklyBrain.IsSquadRewardPick(ocr));
        Assert.Equal(CwScreen.SquadRewardPick, CnWeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_阵容奖励页_标题漏读但两块捆绑包仍在()
    {
        // OCR 常把标题行读丢（黄字贴边）：两块「xx 捆绑包」同屏即认
        var ocr = Ocr(W("前锋捆绑包", 0.218, 0.271), W("后场捆绑包", 0.780, 0.271));
        Assert.True(CnWeeklyBrain.IsSquadRewardPick(ocr));
    }

    [Fact]
    public void Classify_特殊奖励页_命中LevelRewardPick()
    {
        var ocr = Ocr(
            W("特殊奖励", 0.18, 0.11),
            W("前锋球员等级提升", 0.218, 0.274), W("中场球员等级提升", 0.498, 0.274),
            W("后场球员等级提升", 0.780, 0.274),
            W("球员等级提升至26级", 0.190, 0.651), W("球员等级提升至26级", 0.470, 0.651),
            W("球员等级提升至26级", 0.740, 0.651),
            W("选择", 0.218, 0.703), W("选择一个等级强化奖励", 0.500, 0.829));
        Assert.True(CnWeeklyBrain.IsLevelRewardPick(ocr));
        Assert.Equal(CwScreen.LevelRewardPick, CnWeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocr));
    }

    [Fact]
    public void Classify_两页互不误判()
    {
        // 阵容奖励页有「选择」（底部说明）但没有「等级提升」→ 不得判成特殊奖励
        var squad = Ocr(W("阵容奖励", 0.2, 0.11), W("前锋捆绑包", 0.218, 0.271),
            W("中场捆绑包", 0.498, 0.271), W("选择一个阵容强化奖励", 0.5, 0.829));
        Assert.False(CnWeeklyBrain.IsLevelRewardPick(squad));
        // 特殊奖励页没有「捆绑包」→ 不得判成阵容奖励
        var level = Ocr(W("特殊奖励", 0.2, 0.11), W("球员等级提升至26级", 0.19, 0.651));
        Assert.False(CnWeeklyBrain.IsSquadRewardPick(level));
    }

    [Fact]
    public void Classify_领取结果弹窗_两个都认()
    {
        var newPlayers = Ocr(W("新球员加入", 0.5, 0.146), W("二位球员已经加入你的挑战阵容", 0.5, 0.767),
            W("确定", 0.5, 0.877), W("前往比赛", 0.88, 0.923));
        Assert.Equal(CwScreen.CwRewardResultPopup,
            CnWeeklyBrain.Classify(Array.Empty<YoloDetection>(), newPlayers));

        var inbox = Ocr(W("奖励已发放至收件箱", 0.5, 0.53), W("确定", 0.5, 0.794), W("前往比赛", 0.88, 0.923));
        Assert.Equal(CwScreen.CwRewardResultPopup,
            CnWeeklyBrain.Classify(Array.Empty<YoloDetection>(), inbox));
        // 只有背景活动主页、没有结果弹窗词 → 不能误判成弹窗
        Assert.False(CnWeeklyBrain.IsRewardResultPopup(Ocr(W("前往比赛", 0.88, 0.923), W("确定", 0.5, 0.877))));
    }

    // ---------------- 评级（阵容奖励） ----------------

    [Theory]
    [InlineData('S', 5)]
    [InlineData('A', 4)]
    [InlineData('B', 3)]
    [InlineData('C', 2)]
    [InlineData('D', 1)]
    public void GradeScore_映射(char grade, int expected)
        => Assert.Equal(expected, CnWeeklyBrain.GradeScore(grade));

    [Fact]
    public void PlanSquadReward_实机六卡_选中场()
    {
        // 实机 2026-11-25：前锋 C+C=4 / 中场 B+C=5 / 后场 D+A=5 → 同分取优先级高的中场
        var ocr = Ocr(
            W("前锋捆绑包", 0.218, 0.271), W("中场捆绑包", 0.498, 0.271), W("后场捆绑包", 0.780, 0.271),
            W("C", 0.162, 0.710), W("C", 0.281, 0.710),
            W("B", 0.440, 0.710), W("C", 0.559, 0.710),
            W("D", 0.718, 0.710), W("A", 0.834, 0.710));
        var plan = CnWeeklyBrain.PlanSquadReward(ocr, frame: null);
        Assert.NotNull(plan);
        Assert.Equal(CwRewardSlot.Midfield, plan!.Slot);
        Assert.Equal(5, plan.Value);
        Assert.True(plan.Complete);
        Assert.Equal(0.498, plan.ClickX, 3);
        Assert.Contains("中场", plan.Detail);
    }

    [Fact]
    public void PlanSquadReward_三组同分_前锋优先()
    {
        var ocr = Ocr(
            W("前锋捆绑包", 0.218, 0.271), W("中场捆绑包", 0.498, 0.271), W("后场捆绑包", 0.780, 0.271),
            W("B", 0.162, 0.710), W("B", 0.281, 0.710),
            W("C", 0.440, 0.710), W("A", 0.559, 0.710),
            W("D", 0.718, 0.710), W("S", 0.834, 0.710));
        var plan = CnWeeklyBrain.PlanSquadReward(ocr, frame: null);
        Assert.Equal(CwRewardSlot.Forward, plan!.Slot); // 6 / 6 / 6
    }

    [Fact]
    public void PlanSquadReward_颜色识别_不依赖单字母OCR()
    {
        var frame = Frame();
        Paint(frame, 0, 1, 0, 1, 255, 255, 255); // 整屏白底
        PaintGrade(frame, 0.2215, 0, 'C');
        PaintGrade(frame, 0.2215, 1, 'C');
        PaintGrade(frame, 0.508, 0, 'B');
        PaintGrade(frame, 0.508, 1, 'C');
        PaintGrade(frame, 0.776, 0, 'D');
        PaintGrade(frame, 0.776, 1, 'A');
        // 整帧 OCR 完全读不到字母（只有标题词）→ 必须靠颜色读出 6 个评级
        var ocr = Ocr(
            W("前锋捆绑包", 0.218, 0.271), W("中场捆绑包", 0.498, 0.271), W("后场捆绑包", 0.780, 0.271));
        var plan = CnWeeklyBrain.PlanSquadReward(ocr, frame);
        Assert.Equal(CwRewardSlot.Midfield, plan!.Slot);
        Assert.Equal(5, plan.Value);
        Assert.True(plan.Complete);
    }

    [Fact]
    public void PlanSquadReward_期望位置读不出_靠整行排序分组兜底()
    {
        // 标题词的 x 被 OCR 读歪（面板中心算错 → 期望字母位置整体偏出），但六枚单字母都在：
        // 按 x 从左到右两两分组仍能读出六卡（B+C=5 与 D+A=5 同分 → 中场）
        var ocr = Ocr(
            W("前锋捆绑包", 0.05, 0.271), W("中场捆绑包", 0.06, 0.271), W("后场捆绑包", 0.07, 0.271),
            W("C", 0.162, 0.710), W("C", 0.281, 0.710),
            W("B", 0.440, 0.710), W("C", 0.559, 0.710),
            W("D", 0.718, 0.710), W("A", 0.834, 0.710));
        var plan = CnWeeklyBrain.PlanSquadReward(ocr, frame: null);
        Assert.Equal(CwRewardSlot.Midfield, plan!.Slot);
        Assert.Equal(5, plan.Value);
        Assert.True(plan.Complete);
    }

    [Fact]
    public void PlanSquadReward_全读不出_返回null由调用方兜底()
    {
        var ocr = Ocr(W("前锋捆绑包", 0.218, 0.271), W("中场捆绑包", 0.498, 0.271));
        Assert.Null(CnWeeklyBrain.PlanSquadReward(ocr, frame: null));
    }

    // ---------------- 等级数字（特殊奖励） ----------------

    [Fact]
    public void PlanLevelReward_全26级_前锋优先()
    {
        var ocr = Ocr(
            W("前锋球员等级提升", 0.218, 0.274), W("中场球员等级提升", 0.498, 0.274),
            W("后场球员等级提升", 0.780, 0.274),
            W("球员等级提升至26级", 0.190, 0.651), W("球员等级提升至26级", 0.470, 0.651),
            W("球员等级提升至26级", 0.740, 0.651));
        var plan = CnWeeklyBrain.PlanLevelReward(ocr);
        Assert.Equal(CwRewardSlot.Forward, plan!.Slot);
        Assert.Equal(26, plan.Value);
        Assert.True(plan.Complete);
    }

    [Fact]
    public void PlanLevelReward_大于40不选_选最小的()
    {
        // 前锋 45 级（不选）→ 中场 30 / 后场 35 → 选中场
        var ocr = Ocr(
            W("前锋球员等级提升", 0.218, 0.274), W("中场球员等级提升", 0.498, 0.274),
            W("后场球员等级提升", 0.780, 0.274),
            W("球员等级提升至45级", 0.190, 0.651), W("球员等级提升至30级", 0.470, 0.651),
            W("球员等级提升至35级", 0.740, 0.651));
        var plan = CnWeeklyBrain.PlanLevelReward(ocr);
        Assert.Equal(CwRewardSlot.Midfield, plan!.Slot);
        Assert.Equal(30, plan.Value);
    }

    [Fact]
    public void PlanLevelReward_数字拆词也能读()
    {
        // OCR 把「球员等级提升至」和「26级」拆成两枚词（实机常见）：取同带内的数字词
        var ocr = Ocr(
            W("前锋球员等级提升", 0.218, 0.274), W("中场球员等级提升", 0.498, 0.274),
            W("后场球员等级提升", 0.780, 0.274),
            W("球员等级提升至", 0.150, 0.651), W("50级", 0.245, 0.651),
            W("球员等级提升至", 0.430, 0.651), W("26级", 0.525, 0.651),
            W("球员等级提升至", 0.712, 0.651), W("31级", 0.806, 0.651));
        var plan = CnWeeklyBrain.PlanLevelReward(ocr);
        Assert.Equal(CwRewardSlot.Midfield, plan!.Slot);
        Assert.Equal(26, plan.Value);
    }

    [Fact]
    public void PlanLevelReward_全超40_返回null()
    {
        var ocr = Ocr(
            W("前锋球员等级提升", 0.218, 0.274), W("中场球员等级提升", 0.498, 0.274),
            W("后场球员等级提升", 0.780, 0.274),
            W("球员等级提升至45级", 0.190, 0.651), W("球员等级提升至50级", 0.470, 0.651),
            W("球员等级提升至60级", 0.740, 0.651));
        Assert.Null(CnWeeklyBrain.PlanLevelReward(ocr));
    }

    // ---------------- 选中态（卡面变蓝） ----------------

    [Fact]
    public void IsRewardChosen_白底未选_蓝底已选()
    {
        var white = Frame();
        Paint(white, 0, 1, 0, 1, 255, 255, 255);
        Assert.False(CnWeeklyBrain.IsRewardChosen(white, 0.2215));

        var chosen = Frame();
        Paint(chosen, 0, 1, 0, 1, 255, 255, 255);
        Paint(chosen, 0.1215, 0.3215, 0.222, 0.785, 151, 0, 0); // 整块卡面深蓝 (0,0,151)
        Assert.True(CnWeeklyBrain.IsRewardChosen(chosen, 0.2215));
        // 相邻面板仍是白底 → 不应被认成已选
        Assert.False(CnWeeklyBrain.IsRewardChosen(chosen, 0.508));
    }

    [Fact]
    public void FallbackChoice_永远可用_指向前锋()
    {
        var c = CnWeeklyBrain.FallbackChoice();
        Assert.Equal(CwRewardSlot.Forward, c.Slot);
        Assert.False(c.Complete);
        Assert.Equal(0.2215, c.ClickX, 3);
        Assert.Equal(0.271, c.ClickY, 3);
    }
}
