using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Bot.Scenarios.Weekly;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Observation;
using EfootballBot.Core.Vision;
using Xunit;

namespace EfootballBot.Tests;

public class AuditOptimizationTests
{
    [Fact]
    public void FilterWindowTitlebar_RemovesWindowBarTokens_ResultingEmpty()
    {
        var raw = new OcrResult(new List<OcrWord>
        {
            new("D", 0.008, 0.008, 0.018, 0.025),
            new("eΓootball™", 0.030, 0.008, 0.095, 0.025),
            new("X", 0.968, 0.008, 0.020, 0.025),
        }, 1282, 759);

        var filtered = OcrResult.FilterWindowTitlebar(raw);

        Assert.Empty(filtered.Words);
    }

    [Fact]
    public void FilterWindowTitlebar_PreservesRealTitleScreenAndInGameTopBar()
    {
        var titleScreen = new OcrResult(new List<OcrWord>
        {
            new("eFootball™", 0.35, 0.43, 0.30, 0.05),
            new("Press X", 0.42, 0.72, 0.16, 0.04),
        }, 1282, 759);

        var filteredTitle = OcrResult.FilterWindowTitlebar(titleScreen);
        Assert.Same(titleScreen, filteredTitle);
        Assert.Equal(2, filteredTitle.Words.Count);

        var topGameUi = new OcrResult(new List<OcrWord>
        {
            new("D", 0.008, 0.008, 0.018, 0.025),
            new("eΓotball", 0.030, 0.008, 0.090, 0.025),
            new("比赛", 0.12, 0.06, 0.06, 0.03),
            new("4,560", 0.76, 0.06, 0.06, 0.03),
        }, 1282, 759);

        var filteredGame = OcrResult.FilterWindowTitlebar(topGameUi);
        Assert.Equal(2, filteredGame.Words.Count);
        Assert.True(filteredGame.Contains("比赛"));
        Assert.True(filteredGame.Contains("4560"));
    }

    [Fact]
    public void OcrResult_SharedRowClustering_ProducesConsistentLinesMergedAndJoined()
    {
        // 刻意打乱两行内的 X / Y 顺序，验证 Lines / MergedWords / JoinedText 一致性
        var ocr = new OcrResult(new List<OcrWord>
        {
            new("比赛", 0.48, 0.503, 0.06, 0.03),
            new("往", 0.44, 0.501, 0.03, 0.03),
            new("前", 0.40, 0.500, 0.03, 0.03),
            new("回放", 0.48, 0.202, 0.06, 0.03),
            new("比赛", 0.41, 0.200, 0.06, 0.03),
        }, 1280, 720);

        Assert.Equal(2, ocr.Lines.Count);
        Assert.Equal("比赛回放", ocr.Lines[0].Text);
        Assert.Equal("前往比赛", ocr.Lines[1].Text);
        Assert.Contains(ocr.MergedWords, w => w.NormText == "比赛回放");
        Assert.Contains(ocr.MergedWords, w => w.NormText == "前往比赛");
        Assert.Equal("比赛回放前往比赛", ocr.JoinedText);
    }

    [Fact]
    public void ShouldResetBreakerOnFreshFrame_RespectsRestartHealthWindow()
    {
        var t0 = new DateTime(2026, 11, 24, 8, 0, 0);
        Assert.False(ObservationService.ShouldResetBreakerOnFreshFrame(DateTime.MinValue, t0));
        Assert.False(ObservationService.ShouldResetBreakerOnFreshFrame(t0, t0.AddSeconds(60)));
        Assert.True(ObservationService.ShouldResetBreakerOnFreshFrame(t0, t0.AddSeconds(120)));
    }

    [Fact]
    public void ResolveNextEventSlot_SkipsGreenChecksOnceWithoutDoubleShift()
    {
        // 槽位 0 带绿勾：返回 1（而非旧实现循环里右移+1 再整体右移+1 的错位 2）
        var done0 = new HashSet<int>();
        int s1 = WeeklyEventScenario.ResolveNextEventSlot(0, done0, slot => slot == 0);
        Assert.Equal(1, s1);
        Assert.Contains(0, done0);

        // 槽位 0 与 1 带绿勾：返回 2
        var done01 = new HashSet<int>();
        int s2 = WeeklyEventScenario.ResolveNextEventSlot(0, done01, slot => slot is 0 or 1);
        Assert.Equal(2, s2);
        Assert.Equal(new[] { 0, 1 }, done01.OrderBy(x => x).ToArray());

        // 中途从槽位 1 重入且槽位 1 未完成：停在 1
        var doneReenter = new HashSet<int> { 0 };
        Assert.Equal(1, WeeklyEventScenario.ResolveNextEventSlot(1, doneReenter, _ => false));

        // 全部 4 个槽位已完成：返回 4
        var doneAll = new HashSet<int> { 0, 2 };
        Assert.Equal(4, WeeklyEventScenario.ResolveNextEventSlot(0, doneAll, slot => slot is 1 or 3));
    }

    [Fact]
    public void IsInsufficientPointsDialog_DetectsShortageWithoutFalsePositiveOnNormalPurchase()
    {
        var shortage = new OcrResult(new List<OcrWord>
        {
            new("无法使用", 0.45, 0.38, 0.10, 0.04),
            new("我的联赛积分不足。", 0.40, 0.46, 0.20, 0.04),
            new("OK", 0.48, 0.62, 0.04, 0.04),
        }, 1280, 720);
        Assert.True(MyLeagueScenario.IsInsufficientPointsDialog(shortage));

        var normalPurchase = new OcrResult(new List<OcrWord>
        {
            new("使用物品", 0.45, 0.38, 0.10, 0.04),
            new("消耗我的联赛积分", 0.40, 0.46, 0.20, 0.04),
            new("9,000", 0.47, 0.54, 0.06, 0.04),
            new("取消", 0.48, 0.64, 0.05, 0.04),
        }, 1280, 720);
        Assert.False(MyLeagueScenario.IsInsufficientPointsDialog(normalPurchase));
    }

    [Fact]
    public void TryGetWgcWarpControl_RecognizesCaptureHostWhenSidecarInactive()
    {
        using var host = new CaptureHost();
        Assert.True(ObservationService.TryGetWgcWarpControl(host, out bool initialWarp, out var setWarp));
        Assert.False(initialWarp);

        setWarp(true);
        Assert.True(host.ForceWarpDevice);
        Assert.True(ObservationService.TryGetWgcWarpControl(host, out bool updatedWarp, out _));
        Assert.True(updatedWarp);
    }

    [Fact]
    public void FilterWindowTitlebar_OptionalPerfOsd_StripsOverlayAndPreservesGameUi()
    {
        // 用户未开启外置 FPS 监控：无监控词时原样零分配返回，不生成任何遮罩
        var normalUi = new OcrResult(new List<OcrWord>
        {
            new("对阵AI", 0.15, 0.05, 0.08, 0.03),
            new("4,560", 0.78, 0.05, 0.06, 0.03),
        }, 1280, 720);
        var noOsdFiltered = OcrResult.FilterWindowTitlebar(normalUi);
        Assert.Same(normalUi, noOsdFiltered);
        Assert.Empty(noOsdFiltered.OsdMaskRects);

        // 加载黑屏 + NVIDIA/Afterburner 顶部监控浮窗：剥离标题栏和监控后词数为 0，触发 3× 节流与加载宽限
        var blankWithOsd = new OcrResult(new List<OcrWord>
        {
            new("D", 0.008, 0.008, 0.018, 0.025),
            new("eΓootbal|", 0.030, 0.008, 0.095, 0.025),
            new("X", 0.968, 0.008, 0.020, 0.025),
            new("FPS 304 CPU", 0.18, 0.009, 0.14, 0.025),
            new("86 °C GPU", 0.33, 0.009, 0.10, 0.025),
            new("71°(", 0.44, 0.009, 0.05, 0.025),
        }, 1282, 759);
        var strippedBlank = OcrResult.FilterWindowTitlebar(blankWithOsd);
        Assert.Empty(strippedBlank.Words);
        Assert.NotEmpty(strippedBlank.OsdMaskRects);

        // 菜单画面 + 外置监控：仅剔除监控条，不误伤同高度的游戏词汇
        var menuWithOsd = new OcrResult(new List<OcrWord>
        {
            new("对阵AI", 0.10, 0.05, 0.08, 0.03),
            new("FPS 319 CPU", 0.45, 0.04, 0.12, 0.025),
            new("75 C GPU 74℃", 0.58, 0.04, 0.14, 0.025),
            new("前往比赛", 0.20, 0.25, 0.12, 0.04),
        }, 1282, 759);
        var strippedMenu = OcrResult.FilterWindowTitlebar(menuWithOsd);
        Assert.Equal(2, strippedMenu.Words.Count);
        Assert.True(strippedMenu.Contains("对阵AI"));
        Assert.True(strippedMenu.Contains("前往比赛"));
    }

    [Fact]
    public void HashBgra_MasksOnlyDetectedOsdRegion_UnmaskedTopStillSensitive()
    {
        FrameData Make(byte topPixel, byte bodyPixel)
        {
            var bgra = new byte[128 * 64 * 4];
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 128; x++)
                {
                    int i = (y * 128 + x) * 4;
                    byte v = (y < 6 && x >= 32 && x < 96) ? topPixel : bodyPixel;
                    bgra[i] = v; bgra[i + 1] = v; bgra[i + 2] = v; bgra[i + 3] = 255;
                }
            return new FrameData(bgra, 128, 64, 0);
        }

        var f1 = Make(10, 120);
        var f2 = Make(240, 120); // 仅 OSD 区域像素跳变（FPS 数字变化）
        var f3 = Make(10, 200);  // 正文区域变化

        // 无外置浮窗时（无遮罩），顶部像素变化仍敏感
        Assert.NotEqual(ObservationService.HashBgra(f1), ObservationService.HashBgra(f2));

        // 有外置浮窗遮罩时，屏蔽 OSD 区跳变、正文变化依然敏感
        var mask = new[] { new OcrMaskRect(0.24, 0.0, 0.76, 0.10) };
        Assert.Equal(ObservationService.HashBgra(f1, mask), ObservationService.HashBgra(f2, mask));
        Assert.NotEqual(ObservationService.HashBgra(f1, mask), ObservationService.HashBgra(f3, mask));
    }
}
