using EfootballBot.Core.Bot.Scenarios.Weekly;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

/// <summary>
/// 国际服周活动介绍页（进入 / 详情 纵排按钮）聚焦探针 + 推进决策（2026-11-09 用户实机反馈：
/// 「到这介绍页总会多一步向下的错误操作，默认光标在进入直接点下一步即可」）。
/// 纯函数部分锁死规则；实机帧部分校准两个按钮的底色亮度（聚焦白底 vs 未聚焦深色半透明）。
/// </summary>
public class WeeklyIntroFocusTests
{
    private readonly ITestOutputHelper _out;
    public WeeklyIntroFocusTests(ITestOutputHelper output) => _out = output;

    private static string WeeklyDir => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestData", "weekly");

    [Fact]
    public void DecideIntroEntry_FocusMatrix()
    {
        // 焦点在「进入」：直接 A，一个方向键都不按（用户 2026-11-09 规则）
        Assert.Equal(WeeklyBrain.WkIntroAction.PressEnter,
            WeeklyBrain.DecideIntroEntry(WeeklyBrain.WkIntroFocus.Enter, 0));
        Assert.Equal(WeeklyBrain.WkIntroAction.PressEnter,
            WeeklyBrain.DecideIntroEntry(WeeklyBrain.WkIntroFocus.Enter,
                WeeklyBrain.IntroEnterTriesBeforeNudge - 1));

        // 焦点在「详情」：必须先上移归位，直接 A 只会打开说明面板
        Assert.Equal(WeeklyBrain.WkIntroAction.UpThenEnter,
            WeeklyBrain.DecideIntroEntry(WeeklyBrain.WkIntroFocus.Detail, 0));
        Assert.Equal(WeeklyBrain.WkIntroAction.UpThenEnter,
            WeeklyBrain.DecideIntroEntry(WeeklyBrain.WkIntroFocus.Detail, 3));

        // 探针读不出：先按用户默认直接 A，连续无效后才改上移归位（绝不死磕同一个动作）
        Assert.Equal(WeeklyBrain.WkIntroAction.PressEnter,
            WeeklyBrain.DecideIntroEntry(WeeklyBrain.WkIntroFocus.Unknown, 0));
        Assert.Equal(WeeklyBrain.WkIntroAction.PressEnter,
            WeeklyBrain.DecideIntroEntry(WeeklyBrain.WkIntroFocus.Unknown,
                WeeklyBrain.IntroEnterTriesBeforeNudge - 1));
        Assert.Equal(WeeklyBrain.WkIntroAction.UpThenEnter,
            WeeklyBrain.DecideIntroEntry(WeeklyBrain.WkIntroFocus.Unknown,
                WeeklyBrain.IntroEnterTriesBeforeNudge));

        // 自愈兜底：探针哪怕误报「进入」，连续多次没推进也必须改上移归位
        Assert.Equal(WeeklyBrain.WkIntroAction.UpThenEnter,
            WeeklyBrain.DecideIntroEntry(WeeklyBrain.WkIntroFocus.Enter,
                WeeklyBrain.IntroEnterTriesBeforeNudge));
    }

    [Fact]
    public void IntroDetailPanel_NeedsCloseHeaderWithoutEnterButton()
    {
        // 说明面板：整幅浅色文档 + 右上「关闭」，没有「进入」按钮
        var panel = new OcrResult(new[]
        {
            new OcrWord("关闭", 0.90, 0.03, 0.06, 0.04),
            new OcrWord("eFootball™ Championship 2027 eJ.LEAGUE2027は明治安田J1リーグ", 0.30, 0.20, 0.40, 0.05),
            new OcrWord("決勝は、1月31日(日)に東京でオフライン開催される予定です。", 0.30, 0.26, 0.40, 0.05),
            new OcrWord("大会の詳細は公式サイトをご確認ください。", 0.30, 0.32, 0.40, 0.05),
            new OcrWord("内容", 0.30, 0.38, 0.05, 0.05),
            new OcrWord("配布期間", 0.30, 0.44, 0.05, 0.05),
            new OcrWord("公式サイトヘオ", 0.60, 0.50, 0.10, 0.05),
            new OcrWord("終了", 0.90, 0.94, 0.05, 0.04),
        }, 1280, 720);
        Assert.True(WeeklyBrain.IsIntroDetailPanel(panel));

        // 介绍页本体：同屏有「进入」，不能被当成说明面板（否则会误按 B 退出）
        var intro = new OcrResult(new[]
        {
            new OcrWord("巡回活动", 0.06, 0.06, 0.06, 0.04),
            new OcrWord("关闭", 0.90, 0.03, 0.06, 0.04),
            new OcrWord("National Teams", 0.10, 0.10, 0.20, 0.05),
            new OcrWord("结束倒数：6天10小时", 0.10, 0.16, 0.14, 0.04),
            new OcrWord("进入", 0.27, 0.69, 0.05, 0.04),
            new OcrWord("详情", 0.27, 0.79, 0.05, 0.04),
            new OcrWord("奖励", 0.06, 0.43, 0.04, 0.04),
            new OcrWord("4★ 指定球员合约×1", 0.10, 0.48, 0.18, 0.04),
        }, 1280, 720);
        Assert.False(WeeklyBrain.IsIntroDetailPanel(intro));
    }

    /// <summary>
    /// 实机帧锁定：三个历史介绍页帧的光标都在「进入」（旧逻辑直接 A 就能进）；
    /// 2026-11-09 国际服 National Teams 帧的光标停在「详情」（白底高亮）——
    /// 这正是用户看到「多一步向下」的那一帧：直接 A 只会打开说明面板。
    /// </summary>
    [Fact]
    public void IntroFrames_FocusAsserted()
    {
        var expected = new (string Name, WeeklyBrain.WkIntroFocus Want)[]
        {
            ("weekly_event_intro.png", WeeklyBrain.WkIntroFocus.Enter),
            ("weekly_intro_vsai_body.png", WeeklyBrain.WkIntroFocus.Enter),
            ("challenge_intro.png", WeeklyBrain.WkIntroFocus.Enter),
            ("weekly_intro_national_teams_1109.png", WeeklyBrain.WkIntroFocus.Detail),
        };

        using var ocr = new OcrRecognizer();
        foreach (var (name, want) in expected)
        {
            var path = Path.Combine(WeeklyDir, name);
            if (!File.Exists(path)) { _out.WriteLine($"==== {name}: missing, skip ===="); continue; }
            var data = TestFrameLoader.LoadPngBgra(path, out int w, out int h);
            var frame = new FrameData(data, w, h, 0);
            var res = TestOcr.Run(ocr, frame);
            var focus = WeeklyBrain.DetectFocusedIntroButton(frame, res);
            _out.WriteLine($"{name}: focus={focus} (want {want})  " +
                           $"luma(进入)={BandLuma(frame, res, "进入")} luma(详情)={BandLuma(frame, res, "详情")}");
            Assert.True(focus == want, $"{name}: got {focus}, want {want}");
        }
    }

    /// <summary>
    /// 2026-11-09 帧仍要判成介绍页（不能被正文/页签名抢判成列表页或主页），
    /// 否则场景层连 PressIntroEnterAsync 都到不了。
    /// </summary>
    [Fact]
    public void NationalTeamsFrame_ClassifiedAsEventIntro()
    {
        var path = Path.Combine(WeeklyDir, "weekly_intro_national_teams_1109.png");
        if (!File.Exists(path)) return;
        var data = TestFrameLoader.LoadPngBgra(path, out int w, out int h);
        var frame = new FrameData(data, w, h, 0);
        using var ocr = new OcrRecognizer();
        var res = TestOcr.Run(ocr, frame);
        var screen = WeeklyBrain.Classify(Array.Empty<EfootballBot.Core.Vision.Yolo.YoloDetection>(), res);
        _out.WriteLine($"weekly_intro_national_teams_1109: {screen}");
        Assert.Equal(WkScreen.EventIntro, screen);
        // 该帧不是「详情」说明面板（同屏有「进入」）——不能被快速 B 通道误吃
        Assert.False(WeeklyBrain.IsIntroDetailPanel(res));
    }

    /// <summary>测试侧复算按钮底色中位亮度（与 WeeklyBrain 内部探针同一几何，仅供日志对照）。</summary>
    private static int BandLuma(FrameData frame, OcrResult res, string keyword)
    {
        var word = res.Words
            .Where(x => x.CenterY > 0.55 && x.CenterX < 0.55 && x.NormText.Contains(keyword))
            .OrderByDescending(x => x.H)
            .FirstOrDefault();
        if (word is null) return -1;
        double padY = word.H * 0.5;
        return RegionProbe.MedianLuma(frame,
            Math.Clamp(word.X - 0.05, 0, 1), Math.Clamp(word.X + word.W + 0.05, 0, 1),
            Math.Clamp(word.Y - padY, 0, 1), Math.Clamp(word.Y + word.H + padY, 0, 1));
    }
}
