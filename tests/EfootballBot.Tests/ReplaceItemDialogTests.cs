using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

/// <summary>
/// 「要覆盖目前的物品吗？」替换确认弹窗（用户 2026-11-23 实机）：
/// 焦点默认在下方蓝色「取消」，按钮词常只读出「取消」一枚 —— 旧逻辑按「单按钮弹窗直接 A」
/// 处理，A 正好点掉取消，购买白做。锁定三条：
/// ① 标题含「覆盖」→ 分类必须是 PurchaseDialog（不是 ItemList/HasConfirm）；
/// ② 弹窗决策必须是「上×1 → A」，与按钮词读全读不全无关；
/// ③ 普通单按钮弹窗（没有「覆盖」标题）仍走直接 A，不被这条规则带偏。
/// </summary>
public class ReplaceItemDialogTests
{
    private readonly ITestOutputHelper _out;
    public ReplaceItemDialogTests(ITestOutputHelper output) => _out = output;

    private static FrameData Frame(int w = 1280, int h = 720)
    {
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = 40; bgra[i + 1] = 40; bgra[i + 2] = 40; bgra[i + 3] = 255;
        }
        return new FrameData(bgra, w, h, 0);
    }

    private static OcrResult Ocr(params (string Text, double Cx, double Cy, double W, double H)[] words)
        => new(words.Select(x => new OcrWord(x.Text, x.Cx - x.W / 2, x.Cy - x.H / 2, x.W, x.H)).ToList(), 1280, 720);

    /// <summary>实机形态：标题「要覆盖目前的物品吗？」+ 上方「覆盖」+ 下方「取消」（焦点）。</summary>
    private static OcrResult ReplaceDialogOcr(bool withConfirmButton)
    {
        var words = new List<(string, double, double, double, double)>
        {
            ("要覆盖目前的物品吗", 0.50, 0.46, 0.28, 0.045),
            ("取消", 0.50, 0.66, 0.05, 0.036),
        };
        if (withConfirmButton) words.Insert(1, ("覆盖", 0.50, 0.58, 0.05, 0.036));
        return Ocr(words.ToArray());
    }

    [Fact]
    public void 覆盖弹窗_分类为购买弹窗()
    {
        var ocr = ReplaceDialogOcr(withConfirmButton: true);
        var screen = MatchBrain.Classify(new List<YoloDetection>(), ocr);
        _out.WriteLine($"screen={screen}");
        Assert.Equal(MatchScreen.PurchaseDialog, screen);
    }

    [Fact]
    public void 覆盖弹窗_按钮只读出取消_仍上移再A()
    {
        // 实机事故就是这个形态：OCR 只读到「取消」一枚，没有「覆盖」按钮词
        var ocr = ReplaceDialogOcr(withConfirmButton: false);
        var frame = Frame();
        var buttons = PopupBrain.FindButtons(frame, ocr);
        _out.WriteLine("buttons: " + PopupBrain.Describe(buttons));
        Assert.Single(buttons); // 只有取消：旧逻辑在这里直接 A
        Assert.True(PopupBrain.IsReplaceDialog(ocr));

        var action = PopupBrain.Decide(frame, ocr, out var note);
        _out.WriteLine("decide: " + note);
        Assert.Equal(PopupAction.UpThenConfirm, action);
    }

    [Fact]
    public void 覆盖弹窗_按钮读全且焦点在取消_上移再A()
    {
        var ocr = ReplaceDialogOcr(withConfirmButton: true);
        // 上方「覆盖」暗（未聚焦），下方「取消」亮（焦点）
        var bgra = new byte[1280 * 720 * 4];
        for (int i = 0; i < bgra.Length; i += 4) { bgra[i] = 40; bgra[i + 1] = 40; bgra[i + 2] = 40; bgra[i + 3] = 255; }
        void Paint(double x0, double y0, double x1, double y1, byte l)
        {
            for (int y = (int)(y0 * 720); y < (int)(y1 * 720); y++)
                for (int x = (int)(x0 * 1280); x < (int)(x1 * 1280); x++)
                {
                    int i = (y * 1280 + x) * 4;
                    bgra[i] = l; bgra[i + 1] = l; bgra[i + 2] = l;
                }
        }
        Paint(0.42, 0.55, 0.58, 0.615, 40);    // 覆盖：暗
        Paint(0.42, 0.63, 0.58, 0.695, 255);   // 取消：亮（焦点）
        var frame = new FrameData(bgra, 1280, 720, 0);

        var buttons = PopupBrain.FindButtons(frame, ocr);
        _out.WriteLine("buttons: " + PopupBrain.Describe(buttons));
        var action = PopupBrain.Decide(frame, ocr, out var note);
        _out.WriteLine("decide: " + note);
        Assert.Equal(PopupAction.UpThenConfirm, action);
    }

    [Fact]
    public void 普通单按钮弹窗_不受覆盖规则影响()
    {
        var frame = Frame();
        var ocr = Ocr(("物品已送到收件箱", 0.50, 0.55, 0.30, 0.05), ("OK", 0.50, 0.79, 0.03, 0.04));
        Assert.False(PopupBrain.IsReplaceDialog(ocr));
        Assert.Equal(PopupAction.PressConfirm, PopupBrain.Decide(frame, ocr, out _));
    }

    [Fact]
    public void 覆盖字在弹窗带外_不触发替换规则()
    {
        // 页眉/角落的「覆盖」不是替换弹窗标题（y=0.10 / x=0.95 都在弹窗带外）
        var frame = Frame();
        var ocr = Ocr(
            ("覆盖范围：全部球员", 0.50, 0.10, 0.30, 0.03),
            ("确定", 0.50, 0.60, 0.05, 0.036));
        Assert.False(PopupBrain.IsReplaceDialog(ocr));
        Assert.Equal(PopupAction.PressConfirm, PopupBrain.Decide(frame, ocr, out _));
    }

    [Fact]
    public void 带内说明文字含覆盖且有取消_仍不误判()
    {
        // 共享层收紧回归：别的模块页面上「覆盖范围：全部球员」+ 一枚「取消」按钮，
        // 不能因为出现「覆盖」二字就被当成替换弹窗白按一次上+A
        var frame = Frame();
        var ocr = Ocr(
            ("覆盖范围：全部球员", 0.50, 0.46, 0.30, 0.04),
            ("取消", 0.50, 0.66, 0.05, 0.036));
        Assert.True(ocr.ContainsJoined("取消"));
        Assert.False(PopupBrain.IsReplaceDialog(ocr));
        Assert.Equal(PopupAction.PressConfirm, PopupBrain.Decide(frame, ocr, out _)); // 单按钮 → 直接 A
    }

    [Fact]
    public void 标题整行被合并读长_分类仍按购买弹窗兜底()
    {
        // 标题+按钮被 OCR 并成一行（超过 PopupBrain 的 TitleMaxChars）时，
        // Classify 的全文拼接判据仍然命中，购买弹窗不会退化成物品列表
        var ocr = Ocr(
            ("要覆盖目前的物品吗覆盖取消", 0.50, 0.46, 0.40, 0.045),
            ("取消", 0.50, 0.66, 0.05, 0.036));
        Assert.False(PopupBrain.IsReplaceDialog(ocr)); // 超长整行不认
        Assert.Equal(MatchScreen.PurchaseDialog,
            MatchBrain.Classify(new List<YoloDetection>(), ocr));
    }

    [Fact]
    public void 购买弹窗久按无效_按B脱困不死循环()
    {
        // 上移+A 连续 30 轮仍停在同一弹窗 → B 关掉重来（否则永远重按）
        var action = MatchBrain.Decide(MatchScreen.PurchaseDialog, OcrResult.Empty,
            MatchBrain.ConfirmEscapeStreak);
        _out.WriteLine($"{action.Kind} / {action.Target}");
        Assert.Equal(MatchActionKind.GamepadBack, action.Kind);
        // 未到阈值仍按「上移→A」
        Assert.Equal(MatchActionKind.PadUpConfirm,
            MatchBrain.Decide(MatchScreen.PurchaseDialog, OcrResult.Empty, 8).Kind);
    }

    [Fact]
    public void 比赛页磁贴行计数_单枚锦标赛不算磁贴行()
    {
        // 锦标赛子页：只有一枚「锦标赛」标题
        var single = Ocr(("锦标赛", 0.30, 0.60, 0.10, 0.04));
        Assert.Equal(1, HubTileLocator.VisibleTitleCount(single));
        // 比赛页磁贴行：三枚卡标题
        var hub = Ocr(
            ("锦标赛", 0.14, 0.62, 0.10, 0.04),
            ("活动", 0.40, 0.62, 0.08, 0.04),
            ("我的联赛", 0.78, 0.62, 0.12, 0.04));
        Assert.Equal(3, HubTileLocator.VisibleTitleCount(hub));
    }
}
