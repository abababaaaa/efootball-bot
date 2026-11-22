using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 2026-11-20 实机事故回归：进球后的庆祝 CG（满屏 3D 球场、OCR 只剩零星碎片词）被 IsMatchHub
// 判成「比赛页」，场次跟踪器把这帧当成「回到菜单」直接关场——一场才走了 21 秒的「比赛」
// 被计入完成场次（用户口语：「进球后 cg，误判成了比赛结束」）。
// 两层防线：
//  ① 场景层：ScreenProbe.LooksLikePitch（中央草地绿 ≥30%）→ Classify(pitchLike: true) 先于
//     一切菜单类判定返回 InMatch；
//  ② 共享层：MatchSessionTracker 对 Menu 类关场要求本场已走满 60 秒，否则只标记
//     MenuCloseSuppressed、继续观察（真回菜单由 5 分钟无比赛特征兜底，那条路径本就不计场次）。
public class GoalCutsceneFalseEndTests
{
    private static readonly DateTime T0 = new(2026, 11, 20, 0, 34, 13, DateTimeKind.Local);
    private readonly ITestOutputHelper _out;
    public GoalCutsceneFalseEndTests(ITestOutputHelper output) => _out = output;

    private static string TestDir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData");

    private static FrameData LoadFrame(string name)
    {
        var data = TestFrameLoader.LoadPngBgra(Path.Combine(TestDir, name), out int w, out int h);
        return new FrameData(data, w, h, 0);
    }

    private static OcrResult Ocr(params (string text, double x, double y)[] words)
    {
        var list = words.Select(w => new OcrWord(w.text, w.x, w.y, 0.1, 0.03)).ToList();
        return new OcrResult(list, 1, 1);
    }

    // ---------------- ① 共享层：60 秒门的语义 ----------------

    [Fact]
    public void 进球CG_21秒被判MatchHub_不关场也不计场次()
    {
        var t = new MatchSessionTracker();
        Assert.False(t.Observe(MatchScreen.InMatch, T0));
        Assert.True(t.Active);

        // 00:34:35 实机那一帧：本场才 21 秒，被判 MatchHub
        Assert.False(t.Observe(MatchScreen.MatchHub, T0.AddSeconds(21)));
        Assert.True(t.Active);                      // 还在场上
        Assert.True(t.MenuCloseSuppressed);         // 但留下了「拦下一次误判」的痕迹，供日志
        Assert.Null(t.LastDuration);                // 没有伪造出一场用时
    }

    [Fact]
    public void 进球CG连来几帧_都拦下()
    {
        var t = new MatchSessionTracker();
        t.Observe(MatchScreen.InMatch, T0);
        Assert.False(t.Observe(MatchScreen.MatchHub, T0.AddSeconds(21)));
        Assert.False(t.Observe(MatchScreen.MatchHub, T0.AddSeconds(26)));
        Assert.False(t.Observe(MatchScreen.MainMenu, T0.AddSeconds(31)));
        Assert.True(t.Active);
    }

    [Fact]
    public void 真回菜单_用时超过一分钟_照常关场()
    {
        var t = new MatchSessionTracker();
        t.Observe(MatchScreen.InMatch, T0);
        Assert.True(t.Observe(MatchScreen.MatchHub, T0.AddMinutes(8)));
        Assert.False(t.Active);
        Assert.False(t.MenuCloseSuppressed);
        Assert.Equal(MatchEndReason.Menu, t.LastEndReason);
        Assert.True(t.LastEndTrusted);              // 见到菜单 → 可信，计场次
    }

    [Fact]
    public void 终场页_不受时长门影响()
    {
        // 终场页是强证据，即便离奇地打得很快也照常关场（门只管「回到菜单」这一类弱证据）
        var t = new MatchSessionTracker();
        t.Observe(MatchScreen.InMatch, T0);
        Assert.True(t.Observe(MatchScreen.FullTime, T0.AddSeconds(30)));
        Assert.Equal(MatchEndReason.FullTime, t.LastEndReason);
    }

    // ---------------- ② 场景层：草地绿探针 ----------------

    [Fact]
    public void 球场探针_比赛帧为真()
    {
        var f = LoadFrame("inmatch_user_1.png");
        _out.WriteLine($"inmatch_user_1 pitch={ScreenProbe.LooksLikePitch(f)}");
        Assert.True(ScreenProbe.LooksLikePitch(f));
    }

    [Fact]
    public void 球场探针_菜单与终场页为假()
    {
        // 菜单/主页/终场页都是深色 UI 面板（实机标定草地绿 ≤4%），不许被当成球场，
        // 否则 MatchHub 永远判不出来、流程卡死
        foreach (var name in new[] { "matchhub_user_1.png", "matchhub_new_ui_20261021.jpg", "fulltime_user_1.png" })
        {
            var f = LoadFrame(name);
            bool pitch = ScreenProbe.LooksLikePitch(f);
            _out.WriteLine($"{name} pitch={pitch}");
            Assert.False(pitch, name + " 不应被判成球场");
        }
    }

    [Fact]
    public void 球场探针_国服跳过横幅仍是球场()
    {
        var f = LoadFrame(Path.Combine("cnmatch", "cn_skip_banner_20261106.png"));
        Assert.True(ScreenProbe.LooksLikePitch(f));
    }

    // ---------------- ③ 分类器：pitchLike 先于菜单判定 ----------------

    [Fact]
    public void 比赛页OCR风格_带球场标志时判比赛中()
    {
        // 这次事故的 OCR 风格：顶部一行含「比赛」+ 磁贴词「活动」——本来足够骗过 IsMatchHub
        var ocr = Ocr(("比赛", 0.42, 0.18), ("活动", 0.30, 0.62), ("联赛", 0.66, 0.62));
        Assert.Equal(MatchScreen.MatchHub,
            MatchBrain.Classify(Array.Empty<Core.Vision.Yolo.YoloDetection>(), ocr));
        // 同一份 OCR，只要画面还是球场（庆祝 CG）→ 必须按比赛中处理
        Assert.Equal(MatchScreen.InMatch,
            MatchBrain.Classify(Array.Empty<Core.Vision.Yolo.YoloDetection>(), ocr, pitchLike: true));
    }

    [Fact]
    public void 球场标志不影响终场页()
    {
        // 终场页判定在守卫之前：赛后回放 CG 里出现终场词，仍按终场页关场（强证据优先）
        var ocr = Ocr(("比赛结束", 0.5, 0.30));
        Assert.Equal(MatchScreen.FullTime,
            MatchBrain.Classify(Array.Empty<Core.Vision.Yolo.YoloDetection>(), ocr, pitchLike: true));
    }

    [Fact]
    public void 实机比赛帧_带球场标志仍判比赛中()
    {
        var frame = LoadFrame("inmatch_user_1.png");
        using var ocr = new OcrRecognizer(OcrEngineKind.SimdPaddle, preferInt8: false);
        var res = TestOcr.Run(ocr, frame);
        bool pitch = ScreenProbe.LooksLikePitch(frame);
        var ml = MatchBrain.Classify(Array.Empty<Core.Vision.Yolo.YoloDetection>(), res, pitchLike: pitch);
        _out.WriteLine($"pitch={pitch} ml={ml}");
        Assert.Equal(MatchScreen.InMatch, ml);
    }
}
