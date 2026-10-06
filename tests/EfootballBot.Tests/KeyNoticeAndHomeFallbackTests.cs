using EfootballBot.Core.Bot;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Graph;
using EfootballBot.Core.Probe;
using EfootballBot.Core.Vision;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>
/// 回归（ProbeEngine 版）：① 关键比赛通知弹窗（「即将进行的 Key Match」）曾被
/// 「比赛日+即将进行」喂高成 Schedule，场景层盲按 A 恰好确认默认焦点 CANCEL →
/// 永远进不了比赛；必须 override 成 Dialog，由 PressButtonAsync 移焦点到 OK 再点击。
/// ② 主大厅首帧 OCR 逐字拆开（游|戏|主|菜|单）按排版行拼接兜底 Home。
/// </summary>
public sealed class KeyNoticeAndHomeFallbackTests
{
    private static Observation Obs(params (string text, double x, double y)[] words)
        => new(new FrameData(Array.Empty<byte>(), 1280, 720, 0),
               new OcrResult(words.Select(w => new OcrWord(w.text, w.x, w.y, 0.12, 0.04)).ToList(), 1280, 720),
               GameScreen.Unknown, 0, Array.Empty<string>());

    [Fact]
    public void KeyNotice_FragmentedText_ClassifiedAsDialog()
    {
        var o = Obs(
            ("比赛日", 0.40, 0.28),
            ("即将进行", 0.36, 0.36),
            ("关键比赛", 0.36, 0.44),
            ("CANCEL", 0.30, 0.78),   // 默认焦点在 CANCEL
            ("OK", 0.52, 0.88));
        var result = new ProbeEngine(PageCatalog.Pages).Classify(o);
        Assert.Equal(GameScreen.Dialog, result.Screen);
        Assert.Equal("key-notice", result.OverrideReason);
    }

    [Fact]
    public void KeyNotice_CancelVariant_ClassifiedAsDialog()
    {
        // 标题被 OCR 识成「即将进行的key」，无「关键」字样，但有独立 OK + CANCEL
        var o = Obs(
            ("即将进行的key", 0.34, 0.34),
            ("CANCEL", 0.30, 0.78),
            ("OK", 0.52, 0.88));
        var result = new ProbeEngine(PageCatalog.Pages).Classify(o);
        Assert.Equal(GameScreen.Dialog, result.Screen);
        Assert.Equal("key-notice", result.OverrideReason);
    }

    [Fact]
    public void TrueSchedule_WithoutOk_RemainsSchedule()
    {
        // 真正的赛程表：没有 OK 按钮词，key-notice 不得误触发
        var o = Obs(
            ("比赛日", 0.40, 0.28),
            ("即将进行", 0.36, 0.36),
            ("阿森纳", 0.20, 0.55),
            ("切尔西", 0.20, 0.63));
        Assert.Equal(GameScreen.Schedule, new ProbeEngine(PageCatalog.Pages).Classify(o).Screen);
    }

    [Fact]
    public void FragmentedHomeText_ClassifiedAsHome()
    {
        var o = Obs(
            ("游", 0.08, 0.08), ("戏", 0.12, 0.08), ("主", 0.16, 0.08),
            ("菜", 0.20, 0.08), ("单", 0.24, 0.08),
            ("额", 0.08, 0.85), ("外", 0.12, 0.85));
        Assert.Equal(GameScreen.Home, new ProbeEngine(PageCatalog.Pages).Classify(o).Screen);
    }

    [Fact]
    public void FragmentedHome_WithoutExtras_RemainsUnknown()
    {
        // 只有标题碎片、没有「额外」时不兜底，避免误判
        var o = Obs(
            ("游", 0.08, 0.08), ("戏", 0.12, 0.08), ("主", 0.16, 0.08),
            ("菜", 0.20, 0.08), ("单", 0.24, 0.08));
        Assert.Equal(GameScreen.Unknown, new ProbeEngine(PageCatalog.Pages).Classify(o).Screen);
    }
}
