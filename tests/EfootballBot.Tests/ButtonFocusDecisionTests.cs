using EfootballBot.Core.Action;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using SkiaSharp;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>
/// 回归：关键比赛通知弹窗「即将进行的是关键比赛」中，OK 是白底未聚焦按钮、
/// 「取消」是蓝框实际焦点。旧 ButtonGlow 把亮白（bright≈0.86）直接当聚焦，
/// 对 OK 打出 0.69 分（阈值 0.12）→ 盲按 A 恰好确认了默认焦点「取消」→ 关键比赛被取消。
/// 新规则：蓝/青彩色发光才是硬信号；亮白仅在屏幕上不存在其他彩色焦点按钮时兜底。
/// </summary>
public sealed class ButtonFocusDecisionTests
{
    // ---------------- 纯判定函数：焦点决策 ----------------

    [Fact]
    public void WhiteOk_WhileCancelHoldsBlueFocus_IsNotFocused()
    {
        // 真机截图像素：白 OK(blue=0.093,cyan=0.044,bright=0.860)，蓝取消 blue=0.331
        bool focused = ActionExecutor.IsTargetFocused(0.093, 0.044, 0.860, otherColorMax: 0.331);
        Assert.False(focused);
    }

    [Fact]
    public void WhiteButton_NoColorCompetition_BrightStillCountsAsFocus()
    {
        // 无彩色竞争时（白框高亮风格 UI），亮白兜底仍判聚焦，避免回退旧行为
        bool focused = ActionExecutor.IsTargetFocused(0.02, 0.02, 0.40, otherColorMax: 0.02);
        Assert.True(focused);
    }

    [Fact]
    public void BlueGlowingTarget_IsFocused()
    {
        Assert.True(ActionExecutor.IsTargetFocused(0.331, 0.159, 0.509, otherColorMax: 0.05));
    }

    [Fact]
    public void DarkTarget_NoFocusAnywhere_IsNotFocused()
    {
        Assert.False(ActionExecutor.IsTargetFocused(0.02, 0.02, 0.05, otherColorMax: 0.02));
    }

    [Fact]
    public void BrightTarget_WithStrongerColorElsewhere_IsNotFocused()
    {
        // 目标自身略亮，但别处有明确蓝焦点：不算聚焦，应继续移动
        Assert.False(ActionExecutor.IsTargetFocused(0.05, 0.05, 0.50, otherColorMax: 0.20));
    }

    // ---------------- 真机截图：ButtonGlows 三色分量 ----------------

    /// <summary>从 TestData PNG 解码为 BGRA FrameData（与捕获帧同格式）。</summary>
    internal static FrameData LoadPng(string path)
    {
        using var bmp = SKBitmap.Decode(path);
        var info = new SKImageInfo(bmp.Width, bmp.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var conv = new SKBitmap(info);
        Assert.True(bmp.CopyTo(conv, info.ColorType));
        return new FrameData(conv.Bytes, bmp.Width, bmp.Height, 0);
    }

    /// <summary>仿真「按一次 Up 之后」：OK 变蓝聚焦、取消变白失焦，决策必须翻转为确认 OK。</summary>
    [Fact]
    public void KeyMatchDialog_AfterMoveUp_BlueOkConfirmed()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "TestData");
        var frame = LoadPng(Path.Combine(dir, "keymatch_dialog.png"));

        // 按钮实体矩形（比 ButtonRegion 略收，避免两区重叠互相染色）
        var okBtn = new NormRect(0.355, 0.545, 0.29, 0.075);    // OK 白按钮实体
        var cancelBtn = new NormRect(0.355, 0.645, 0.29, 0.08); // 取消蓝按钮实体
        Paint(frame, okBtn, bgr: (235, 132, 26));               // 聚焦蓝 RGB(26,132,235)
        Paint(frame, cancelBtn, bgr: (245, 245, 245));          // 失焦白

        var ok = new OcrWord("OK", 0.4825, 0.5605, 0.035, 0.045);
        var cancel = new OcrWord("取消", 0.4825, 0.6625, 0.035, 0.045);
        var (okB, okC, okBr) = ActionExecutor.ButtonGlows(frame, ok);
        var (cx, cc, _) = ActionExecutor.ButtonGlows(frame, cancel);

        Assert.True(Math.Max(okB, okC) >= 0.12);   // OK 已蓝色聚焦
        Assert.True(Math.Max(cx, cc) < 0.12);      // 取消已失焦
        Assert.True(ActionExecutor.IsTargetFocused(okB, okC, okBr, Math.Max(cx, cc)));
    }

    /// <summary>把归一化矩形涂成指定 BGR 色（BGRA 帧缓冲原地修改）。</summary>
    private static void Paint(FrameData f, NormRect r, (byte b, byte g, byte rr) bgr)
    {
        int x0 = (int)(r.X * f.Width), x1 = (int)((r.X + r.W) * f.Width);
        int y0 = (int)(r.Y * f.Height), y1 = (int)((r.Y + r.H) * f.Height);
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = (y * f.Width + x) * 4;
                f.Bgra[i] = bgr.b; f.Bgra[i + 1] = bgr.g; f.Bgra[i + 2] = bgr.rr; f.Bgra[i + 3] = 255;
            }
    }

    [Fact]
    public void KeyMatchDialog_Frame_WhiteOkNotFocused_BlueCancelFocused()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "TestData");
        var frame = LoadPng(Path.Combine(dir, "keymatch_dialog.png"));

        // OCR 词框（与 PP-OCRv6 实识别同量级）；X/Y 为左上角
        var ok = new OcrWord("OK", 0.4825, 0.5605, 0.035, 0.045);
        var cancel = new OcrWord("取消", 0.4825, 0.6625, 0.035, 0.045);

        var (okB, okC, okBr) = ActionExecutor.ButtonGlows(frame, ok);
        var (cx, cc, cxBr) = ActionExecutor.ButtonGlows(frame, cancel);

        // 彩色焦点在「取消」而非 OK
        Assert.True(Math.Max(cx, cc) >= 0.12);
        Assert.True(Math.Max(cx, cc) > Math.Max(okB, okC) * 1.5);

        // 决策：OK 不得判聚焦（否则按 A 点中的是取消）；取消才是当前焦点
        Assert.False(ActionExecutor.IsTargetFocused(okB, okC, okBr, Math.Max(cx, cc)));
        Assert.True(ActionExecutor.IsTargetFocused(cx, cc, cxBr, otherColorMax: 0));
    }
}
