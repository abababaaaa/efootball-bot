using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using Xunit;

namespace EfootballBot.Tests;

// 国服推进键序（用户 2026-11-25 实机定稿）：① RT（页面所印提示键）→ ② 手柄 A → ③ 后台鼠标点按钮。
// 「进入 / 下一步 / 确认」都走这一条；比赛内只发 RT（鼠标会落到球场）。国际服仍是 A 为主。
// 纯函数部分（键序阶梯、按钮锚定）在这里覆盖；三个国服模块（天梯巅峰赛/周活动/我的联赛）
// 都调同一份实现，任一处改键序都先在这里红。
public class CnAdvanceTests
{
    private static OcrWord W(string text, double cx, double cy)
        => new(text, cx - 0.05, cy - 0.015, 0.1, 0.03);

    private static OcrResult Ocr(params OcrWord[] words) => new(words, 1280, 720);

    private static YoloDetection Box(string cls, double score, double cx, double cy)
        => new(cls, 0, (float)score, cx - 0.05, cy - 0.03, 0.10, 0.06);

    // ---------------- 键序 ----------------

    [Theory]
    [InlineData(1, CnAdvance.Step.Rt)]
    [InlineData(0, CnAdvance.Step.Rt)]
    [InlineData(2, CnAdvance.Step.Confirm)]
    [InlineData(3, CnAdvance.Step.Mouse)]
    [InlineData(4, CnAdvance.Step.Mouse)]
    [InlineData(99, CnAdvance.Step.Mouse)]
    public void StepOf_RtThenAThenMouse(int attempt, CnAdvance.Step expected)
        => Assert.Equal(expected, CnAdvance.StepOf(attempt));

    [Fact]
    public void StepOf_SecondRoundUsesA()
    {
        // 用户 2026-11-25 实机：终场「球员评价」页按 RT 画面不动、非管理员鼠标又被拦，
        // 连点 17 轮原地不动 → 第 2 轮必须补手柄 A
        Assert.Equal(CnAdvance.Step.Confirm, CnAdvance.StepOf(CnAdvance.ConfirmAtAttempt));
        Assert.Equal(CnAdvance.Step.Confirm, CnAdvance.StepOf(2));
    }

    [Fact]
    public void StepOf_MouseStartsAtDeclaredThreshold()
    {
        Assert.Equal(CnAdvance.Step.Mouse, CnAdvance.StepOf(CnAdvance.MouseFromAttempt));
        // A 那一档必须排在鼠标之前，否则「RT 不动」的页面还是只有鼠标一条路
        Assert.True(CnAdvance.ConfirmAtAttempt < CnAdvance.MouseFromAttempt);
    }

    // ---------------- 单键确认页的阶梯（与赛外推进同一条） ----------------

    [Theory]
    [InlineData(0, CnAdvance.Step.Rt)]
    [InlineData(1, CnAdvance.Step.Rt)]
    [InlineData(2, CnAdvance.Step.Confirm)]
    [InlineData(3, CnAdvance.Step.Mouse)]
    [InlineData(4, CnAdvance.Step.Mouse)]
    [InlineData(9, CnAdvance.Step.Mouse)]
    public void ConfirmStepOf_RtThenAThenMouse(int attempt, CnAdvance.Step expected)
        => Assert.Equal(expected, CnAdvance.ConfirmStepOf(attempt));

    [Fact]
    public void ConfirmStepOf_KillsTheDeadEndAtRoundThree()
    {
        // 退阶必须真的落到鼠标：国服网页层页面（段位积分页/结算页同族）手柄全不吃，
        // 只靠 RT/A 反复猜会永远停在原地，第 3 轮必须有第三种输入
        Assert.Equal(CnAdvance.Step.Mouse, CnAdvance.ConfirmStepOf(3));
        Assert.Equal(CnAdvance.Step.Mouse, CnAdvance.StepOf(3));
    }

    // ---------------- 比赛中弹窗：RT 优先，但不点鼠标 ----------------
    // 用户 2026-11-25：比赛中弹窗也优先 RT。比赛里鼠标点击会落到球场上，所以只用 RT/A 交替。

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public void InMatchStepOf_AlwaysRt(int attempt)
        => Assert.Equal(CnAdvance.Step.Rt, CnAdvance.InMatchStepOf(attempt));

    [Fact]
    public void InMatchStepOf_NeverClicksInsideMatch()
    {
        // 比赛内绝不出鼠标：点球场可能被游戏当成指令，弹窗只需要一个确认键
        for (int attempt = 1; attempt <= 40; attempt++)
            Assert.NotEqual(CnAdvance.Step.Mouse, CnAdvance.InMatchStepOf(attempt));
    }

    // ---------------- 按钮锚定 ----------------

    [Fact]
    public void FindButton_PrefersBottomRightAdvanceWord()
    {
        // 段位积分页：底栏按钮印「RT 下一步」，同屏另有「确认」「前往比赛」等词
        var ocr = Ocr(W("RT 下一步", 0.90, 0.925), W("确认", 0.62, 0.80), W("前往比赛", 0.14, 0.25));
        var hit = CnAdvance.FindButton(ocr, null, "下一步");
        Assert.NotNull(hit);
        Assert.True(hit!.Value.X > 0.8 && hit.Value.Y > 0.9);
    }

    [Fact]
    public void FindButton_IgnoresTopHalfWords()
    {
        // 下半屏没有推进词时不得把顶栏/卡面词当按钮（否则会点到「前往比赛」重复开赛）
        var ocr = Ocr(W("前往比赛", 0.14, 0.25), W("本周可参赛场次: 40", 0.2, 0.28));
        Assert.Null(CnAdvance.FindButton(ocr, null));
    }

    [Fact]
    public void FindButton_FallsBackToBottomRightConfirmBox()
    {
        // OCR 整帧漏读按钮字（段位积分页常见）：靠右下 btn_confirm 框定位
        var dets = new[]
        {
            Box("btn_confirm", 0.62, 0.50, 0.50), // 中部：不是右下推进键
            Box("btn_confirm", 0.93, 0.88, 0.90), // 右下：命中
        };
        var hit = CnAdvance.FindButton(Ocr(), dets);
        Assert.NotNull(hit);
        Assert.True(hit!.Value.X > 0.8 && hit.Value.Y > 0.8);
    }

    [Fact]
    public void FindButton_NothingFound_ReturnsNull()
    {
        Assert.Null(CnAdvance.FindButton(Ocr(), Array.Empty<YoloDetection>()));
        Assert.Null(CnAdvance.FindButton(Ocr(), null));
    }

    [Fact]
    public void FindButton_DefaultWordsIncludeNextStepAndConfirm()
    {
        foreach (string word in new[] { "下一步", "确认", "确定", "OK" })
        {
            var hit = CnAdvance.FindButton(Ocr(W(word, 0.88, 0.92)), null);
            Assert.NotNull(hit);
        }
    }

    // ---------------- OCR 合并长行不许抢真实按钮（2026-11-25 实机事故） ----------------

    [Fact]
    public void FindButton_合并长行不许抢真实按钮()
    {
        // 实机帧（国服周活动「奖励已发放至收件箱」弹窗，用户 2026-11-25 截图）：弹窗自己的
        // 「确定」与右侧页面的「奖励累计领取2/10次」被 OCR 合成一条「确定奖励累计领取210次」，
        // 这条合并行的中心 x=0.716 已经在蓝色按钮（x≈0.33~0.68）**外面**。旧实现按
        // 「同高取最靠右」选中合并行 → 后台鼠标每一轮都点在按钮右侧空白面板上，弹窗 961 轮没关掉。
        var ocr = Ocr(
            W("确定", 0.503, 0.794),
            W("确定奖励累计领取210次", 0.716, 0.795),
            W("奖励已发放至收件箱", 0.500, 0.530));
        var hit = CnAdvance.FindButton(ocr, null, "确定", "确认", "確定", "OK");
        Assert.NotNull(hit);
        // 必须落在按钮里（x 0.33~0.68 / y 0.74~0.85），而不是合并行中心
        Assert.InRange(hit!.Value.X, 0.33, 0.68);
        Assert.InRange(hit.Value.Y, 0.74, 0.85);
    }

    [Fact]
    public void FindButton_只有合并长行时仍给落点()
    {
        // 整帧只读出合并长行（没有独立按钮词）：中心虽不可信，也不许退回「找不到」
        var ocr = Ocr(W("点击确定关闭提示", 0.500, 0.800));
        var hit = CnAdvance.FindButton(ocr, null, "确定");
        Assert.NotNull(hit);
    }

    // ---------------- 弹窗落点档位轮转 ----------------

    [Fact]
    public void PopupVariantOf_六档轮转_每轮换一个手段()
    {
        var seen = new List<CnAdvance.PopupVariant>();
        for (int a = CnAdvance.MouseFromAttempt; a < CnAdvance.MouseFromAttempt + CnAdvance.PopupVariantCount; a++)
            seen.Add(CnAdvance.PopupVariantOf(a));
        Assert.Equal(CnAdvance.PopupVariantCount, seen.Distinct().Count());
        // 第一档必须是「按 OCR 落的真实按钮」（落点最准的先上）
        Assert.Equal(CnAdvance.PopupVariant.OcrHit, CnAdvance.PopupVariantOf(CnAdvance.MouseFromAttempt));
        // 一轮走完回到第一档，绝不固定在同一个坐标上连点
        Assert.Equal(CnAdvance.PopupVariant.OcrHit,
            CnAdvance.PopupVariantOf(CnAdvance.MouseFromAttempt + CnAdvance.PopupVariantCount));
    }

    [Fact]
    public void PopupVariantOf_前两轮不介入_留给RT与A()
    {
        // 1/2 轮交给 AdvanceAsync 的 RT / A，档位函数不应把这两轮判成鼠标档
        Assert.Equal(CnAdvance.PopupVariant.OcrHit, CnAdvance.PopupVariantOf(1));
        Assert.Equal(CnAdvance.Step.Rt, CnAdvance.StepOf(1));
        Assert.Equal(CnAdvance.Step.Confirm, CnAdvance.StepOf(2));
        Assert.Equal(CnAdvance.Step.Mouse, CnAdvance.StepOf(CnAdvance.MouseFromAttempt));
    }

    [Fact]
    public void PopupVariantOf_含手柄重试与B兜底()
    {
        // 长时间关不掉的弹窗必须还有「再按一次 RT」和「B 试关」两条路，不能只剩鼠标一种
        bool retryRt = false, back = false;
        for (int a = 1; a <= 60; a++)
        {
            var v = CnAdvance.PopupVariantOf(a);
            retryRt |= v == CnAdvance.PopupVariant.RetryRt;
            back |= v == CnAdvance.PopupVariant.Back;
        }
        Assert.True(retryRt && back);
    }

    [Fact]
    public void FindRightBottomConfirm_OnlyBottomRightBox()
    {
        var dets = new[]
        {
            Box("btn_confirm", 0.65, 0.50, 0.50), // 中部
            Box("btn_confirm", 0.40, 0.30, 0.88), // 左下
            Box("btn_confirm", 0.93, 0.88, 0.88), // 右下：命中
            Box("btn_back", 0.97, 0.95, 0.12),    // 类别不对
        };
        var hit = CnAdvance.FindRightBottomConfirm(dets);
        Assert.NotNull(hit);
        Assert.True(hit!.CenterX > 0.8 && hit.CenterY > 0.8);
        // 置信度门槛
        Assert.Null(CnAdvance.FindRightBottomConfirm(dets, minScore: 0.95f));
    }
}
