using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using Xunit;

namespace EfootballBot.Tests;

// 国服推进键序（用户 2026-11-21）：① RT（页面所印提示键）→ ② A → ③ 后台鼠标点击。
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
    public void StepOf_MouseStartsAtDeclaredThreshold()
        => Assert.Equal(CnAdvance.Step.Mouse, CnAdvance.StepOf(CnAdvance.MouseFromAttempt));

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
