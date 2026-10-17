using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Bot.Scenarios.Weekly;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 校准/回归：用户提供的国际服挑战活动（挑战清单/卡片页/活动设置）截图 →
// OCR + YOLO dump + WeeklyBrain 分类 + 锁图标像素统计。
// 首次运行用于看清 OCR 实际读出的词与坐标（固化挑战清单判定规则的依据）；
// 之后作为回归锁定（各帧分类不得回退）。
public class ChallengeRealFrameTests
{
    private readonly ITestOutputHelper _out;
    public ChallengeRealFrameTests(ITestOutputHelper output) => _out = output;

    private static string TestDir => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..");

    private static string WeeklyDir => Path.Combine(TestDir, "TestData", "weekly");

    private static byte[] LoadPngBgra(string file, out int w, out int h)
    {
        using var mat = Cv2.ImRead(file, ImreadModes.Color);
        Assert.False(mat.Empty(), $"图片加载失败: {file}");
        using var bgra = new Mat();
        Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
        int byteCount = (int)(bgra.Total() * bgra.ElemSize());
        var data = new byte[byteCount];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, byteCount);
        w = mat.Width; h = mat.Height;
        return data;
    }

    private static readonly string[] Frames =
    {
        "challenge_list.png",
        "challenge_intro.png",
        "challenge_card_home.png",
        "challenge_settings.png",
    };

    [Fact]
    public void Dump_Challenge_Frames()
    {
        using var det = new YoloDetector(
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx"),
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt"),
            new OnnxSessionFactory("CPU"));
        using var ocr = new OcrRecognizer();

        foreach (var name in Frames)
        {
            var path = Path.Combine(WeeklyDir, name);
            if (!File.Exists(path)) { _out.WriteLine($"==== {name}: missing, skip ===="); continue; }
            var data = LoadPngBgra(path, out int w, out int h);
            var frame = new FrameData(data, w, h, 0);
            var dets = det.Detect(frame);
            var ocrRes = ocr.RecognizeAsync(frame).GetAwaiter().GetResult();
            var screen = WeeklyBrain.Classify(dets, ocrRes,
                MyLeagueScenario.CornerHasContent(frame),
                matchContext: false,
                MyLeagueScenario.IsMostlyLightPage(frame));

            _out.WriteLine($"==== {name} -> {screen} ====");
            foreach (var d in dets.Where(d => d.Score >= MyLeagueBrain.MinScore))
                _out.WriteLine($"  YOLO {d.ClassName} {d.Score:F2} @({d.CenterX:F2},{d.CenterY:F2})");
            foreach (var l in ocrRes.Lines)
                _out.WriteLine($"  L \"{l.Text}\" @({l.CenterX:F2},{l.CenterY:F2})");
            foreach (var wd in ocrRes.Words)
                _out.WriteLine($"  W \"{wd.Text}\" @({wd.CenterX:F2},{wd.CenterY:F2}) wh=({wd.W:F3},{wd.H:F3})");

            // 锁图标候选区灰像素统计（卡2 右下 + 卡1 同位对照）
            DumpLockStats(frame, "slot0", 0.352, 0);
            DumpLockStats(frame, "slot1", 0.647, 0);
        }
    }

    /// <summary>打印卡右缘内侧若干候选矩形内「灰亮像素」（R≈G≈B、亮度 90~220）计数，
    /// 用于标定锁图标判定阈值（对角线扫描看整卡也可对照）。</summary>
    private void DumpLockStats(FrameData f, string tag, double cardRight, double dy)
    {
        var bgra = f.Bgra;
        int w = f.Width, h = f.Height;
        foreach (var (x0, x1, y0, y1) in new[]
        {
            (cardRight - 0.055, cardRight - 0.005, 0.760 + dy, 0.850 + dy),
            (cardRight - 0.070, cardRight - 0.005, 0.740 + dy, 0.870 + dy),
        })
        {
            int gray = 0, bright = 0, total = 0;
            for (int y = Math.Max(0, (int)(h * y0)); y < Math.Min(h, (int)(h * y1)); y += 2)
                for (int x = Math.Max(0, (int)(w * x0)); x < Math.Min(w, (int)(w * x1)); x += 2)
                {
                    int i = (y * w + x) * 4;
                    int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                    int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
                    int lum = (r + g + b) / 3;
                    total++;
                    if (mx - mn < 30 && lum >= 90 && lum <= 220) gray++;
                    if (lum > 220) bright++;
                }
            _out.WriteLine($"  [lock {tag}] x[{x0:F3},{x1:F3}] y[{y0:F3},{y1:F3}] gray={gray} bright={bright} total={total}");
        }
    }

    private (WkScreen Screen, OcrResult Ocr, FrameData Frame) Load(string name)
    {
        var path = Path.Combine(WeeklyDir, name);
        Assert.True(File.Exists(path), $"缺少实机帧: {path}");
        var data = LoadPngBgra(path, out int w, out int h);
        var frame = new FrameData(data, w, h, 0);
        var ocr = new OcrRecognizer();
        var ocrRes = ocr.RecognizeAsync(frame).GetAwaiter().GetResult();
        return (WeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocrRes,
            MyLeagueScenario.CornerHasContent(frame),
            matchContext: false,
            MyLeagueScenario.IsMostlyLightPage(frame)), ocrRes, frame);
    }

    [Fact]
    public void ChallengeList_ClassifiesAndScans()
    {
        // 挑战清单帧（2026-10-15 用户实机）：必须判 ChallengeList（此前落 None 会被共享
        // 分类兜成 LeaguePage 盲等）；卡 1 待挑战（无锁无勾）、卡 2 锁定（右下灰锁 44 灰像素）
        var (screen, ocr, frame) = Load("challenge_list.png");
        Assert.Equal(WkScreen.ChallengeList, screen);

        var cards = WeeklyBrain.ScanChallengeCards(ocr, frame);
        Assert.Equal(2, cards.Count);
        Assert.Equal((1, 0, false, false), (cards[0].Index, cards[0].Slot, cards[0].Locked, cards[0].Checked));
        Assert.Equal((2, 1, true, false), (cards[1].Index, cards[1].Slot, cards[1].Locked, cards[1].Checked));

        // 消失处置守卫的前提：真实清单弹窗不含「完成的条件」（那是卡详情页专属词）——
        // 否则 cur 为空时会被误路由到卡详情处理
        Assert.False(WeeklyBrain.IsChallengeCardHome(ocr));
    }

    [Fact]
    public void ChallengeVanishDecision_NeverDeclaresCompleteAfterEnteringPlayableCard()
    {
        // ★实机 2026-10-17 反馈回归（v2.0.8 假「全部完成」跳过未通关活动）：
        // 卡 02 无勾无锁按 A 进入后详情页被误判、卡号消失——游标未越过已勾卡，
        // 无论第几次消失都不得判全完成：未确证 → 重扫，反复无法确证 → 放弃拉黑
        Assert.Equal(WeeklyBrain.ChVanishDecision.Rescan, WeeklyBrain.DecideChallengeVanish(2, cursorDone: false, rescans: 0));
        Assert.Equal(WeeklyBrain.ChVanishDecision.Rescan, WeeklyBrain.DecideChallengeVanish(2, cursorDone: false, rescans: 1));
        Assert.Equal(WeeklyBrain.ChVanishDecision.Rescan, WeeklyBrain.DecideChallengeVanish(2, cursorDone: false, rescans: 2));
        Assert.Equal(WeeklyBrain.ChVanishDecision.Abort, WeeklyBrain.DecideChallengeVanish(2, cursorDone: false, rescans: 3));

        // 两帧确认：动画丢帧等待
        Assert.Equal(WeeklyBrain.ChVanishDecision.WaitFrame, WeeklyBrain.DecideChallengeVanish(0, cursorDone: true, rescans: 5));
        Assert.Equal(WeeklyBrain.ChVanishDecision.WaitFrame, WeeklyBrain.DecideChallengeVanish(1, cursorDone: false, rescans: 0));

        // 正常全通关：游标越过已勾卡后消失 → 重扫确证一次 → 第二次消失才判完成
        Assert.Equal(WeeklyBrain.ChVanishDecision.Rescan, WeeklyBrain.DecideChallengeVanish(2, cursorDone: true, rescans: 0));
        Assert.Equal(WeeklyBrain.ChVanishDecision.ConfirmTail, WeeklyBrain.DecideChallengeVanish(2, cursorDone: true, rescans: 1));
        // 已越勾的重扫消失优先于放弃（能确证完成就不报错误）
        Assert.Equal(WeeklyBrain.ChVanishDecision.ConfirmTail, WeeklyBrain.DecideChallengeVanish(2, cursorDone: true, rescans: 3));
    }

    [Fact]
    public void ChallengeCardHomeMarker_TriggersEvenWhenScreenClassifiedAsList()
    {
        // 实机 2026-10-17 反馈回归：详情页含大字数字+底栏「返回」时会被卡号兜底判成
        // ChallengeList——场景层凭「完成的条件」把它拉回卡详情处理（构造此类帧的 OCR：
        // 无当前卡号、但有详情页专属词）
        var ocr = Ocr(("完成的条件", 0.2, 0.3), ("返回", 0.5, 0.93));
        Assert.True(WeeklyBrain.IsChallengeCardHome(ocr));
    }

    private static OcrResult Ocr(params (string text, double x, double y)[] words)
    {
        var list = words.Select(w => new OcrWord(w.text, w.x, w.y, 0.1, 0.03)).ToList();
        return new OcrResult(list, 1, 1);
    }

    [Fact]
    public void ChallengeIntro_StillClassifiesAsEventIntro()
    {
        // 挑战活动介绍页（「挑战活动」标签 + 进入/详情）走既有介绍页流程
        var (screen, _, _) = Load("challenge_intro.png");
        Assert.Equal(WkScreen.EventIntro, screen);
    }

    [Fact]
    public void ChallengeCardHome_ClassifiesAsEventHome_AndDetectsProgressAndMode()
    {
        // 卡详情页与活动主页同构（前往比赛+主要奖励）→ EventHome；
        // 右栏胜场 0/1 进行中不得判通关；「完成的条件」激活挑战模式识别
        var (screen, ocr, frame) = Load("challenge_card_home.png");
        Assert.Equal(WkScreen.EventHome, screen);
        Assert.False(WeeklyBrain.ChallengeProgressDone(ocr));
        Assert.True(WeeklyBrain.IsChallengeCardHome(ocr));
        Assert.False(WeeklyBrain.IsMainRewardDone(ocr, frame));
    }

    [Fact]
    public void ChallengeSettings_ClassifiesAsEventSettings_AndParsesViaWordFallback()
    {
        // 设置浮层判 EventSettings；行级匹配被背后页面合并行破坏时词级回退必须读出开关值；
        // 无赛事等级行（挑战卡难度固定），Level 必须为 null
        var (screen, ocr, _) = Load("challenge_settings.png");
        Assert.Equal(WkScreen.EventSettings, screen);
        var s = WeeklyBrain.ParseActivitySettings(ocr);
        Assert.Null(s.Level);
        Assert.True(s.AutoControl);
        Assert.False(s.FlexibleAutoControl);
    }
}
