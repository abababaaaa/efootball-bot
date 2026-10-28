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

// 校准/回归：用户提供的国际服周活动 8 张截图 → OCR + YOLO dump + WeeklyBrain 分类。
// 首次运行用于看清 OCR 实际读出的词与坐标（固化分类规则的依据）；
// 之后作为回归锁定（各帧分类不得回退）。
public class WeeklyRealFrameTests
{
    private readonly ITestOutputHelper _out;
    public WeeklyRealFrameTests(ITestOutputHelper output) => _out = output;

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
        "weekly_home_default.png",
        "weekly_home_focused.png",
        "weekly_hub.png",
        "weekly_events_list.png",
        "weekly_event_intro.png",
        "weekly_event_home_popup.png",
        "weekly_event_home.png",
        "weekly_event_settings.png",
        "weekly_match_history.png",
        "weekly_advice_panel.png",
    };

    [Fact]
    public void Dump_Weekly_Frames()
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
            var ocrRes = TestOcr.Run(ocr, frame);
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
        }
    }

    [Fact]
    public void Classify_Weekly_Frames_Asserted()
    {
        using var det = new YoloDetector(
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx"),
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt"),
            new OnnxSessionFactory("CPU"));
        using var ocr = new OcrRecognizer();

        var expected = new (string Name, WkScreen Screen)[]
        {
            ("weekly_events_list.png", WkScreen.EventsList),
            ("weekly_event_intro.png", WkScreen.EventIntro),
            ("weekly_event_home_popup.png", WkScreen.EntryPopup),
            ("weekly_event_home.png", WkScreen.EventHome),
            ("weekly_event_settings.png", WkScreen.EventSettings),
            // 2026-10-26 反馈包实机帧（国际服万圣节活动）：活动主页焦点停在「比赛历史」
            // 面板上，A 点开历史页后旧逻辑被 YOLO 误检 btn_confirm 判成 HasConfirm，
            // 盲按 A 三十轮才 B 逃生 → 回主页再点开，永远进不了比赛
            ("weekly_match_history.png", WkScreen.MatchHistory),
            // 2026-10-28 反馈包实机帧（v5.1.0 国际服周赛）：活动主页左列「建议」被 A 点开，
            // 建议面板盖在主页上但主页词仍可读 → 旧逻辑判 EventHome，A/X 全打空、卡死
            ("weekly_advice_panel.png", WkScreen.AdvicePanel),
        };

        foreach (var (name, want) in expected)
        {
            var path = Path.Combine(WeeklyDir, name);
            if (!File.Exists(path)) continue;
            var data = LoadPngBgra(path, out int w, out int h);
            var frame = new FrameData(data, w, h, 0);
            var dets = det.Detect(frame);
            var ocrRes = TestOcr.Run(ocr, frame);
            var screen = WeeklyBrain.Classify(dets, ocrRes,
                MyLeagueScenario.CornerHasContent(frame),
                matchContext: false,
                MyLeagueScenario.IsMostlyLightPage(frame));
            _out.WriteLine($"{name}: {screen} (want {want})");
            Assert.True(screen == want, $"{name}: got {screen}, want {want}");
        }
    }

    [Fact]
    public void DoneState_Frames_Asserted()
    {
        // 2026-10-13 用户实机校准帧：完成态两处探针（主要奖励勾 + 列表卡绿勾）。
        // 主页帧必须带真实 YOLO 检测——底栏「B 返回」蓝框会检出 btn_confirm 0.91，
        // 弹窗门控不得把干净主页吃成 EntryPopup（实机 bug：完成态从未被评估）
        using var ocr = new OcrRecognizer();
        using var det = new YoloDetector(
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx"),
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt"),
            new OnnxSessionFactory("CPU"));

        // 帧1：主要奖励已完成的主页（English Clubs 实机帧）
        var p1 = Path.Combine(WeeklyDir, "weekly_reward_done.png");
        if (File.Exists(p1))
        {
            var data = LoadPngBgra(p1, out int w, out int h);
            var frame = new FrameData(data, w, h, 0);
            var dets = det.Detect(frame);
            var ocrRes = TestOcr.Run(ocr, frame);
            var screen = WeeklyBrain.Classify(dets, ocrRes);
            _out.WriteLine($"weekly_reward_done: {screen}, mainRewardDone={WeeklyBrain.IsMainRewardDone(ocrRes, frame)}");
            Assert.Equal(WkScreen.EventHome, screen);
            Assert.True(WeeklyBrain.IsMainRewardDone(ocrRes, frame));
        }

        // 帧1b：主要奖励已完成的主页（Titani 实机帧，底栏蓝框 btn_confirm 0.91）
        var p1b = Path.Combine(WeeklyDir, "weekly_home_titani_done.png");
        if (File.Exists(p1b))
        {
            var data = LoadPngBgra(p1b, out int w, out int h);
            var frame = new FrameData(data, w, h, 0);
            var dets = det.Detect(frame);
            var ocrRes = TestOcr.Run(ocr, frame);
            var screen = WeeklyBrain.Classify(dets, ocrRes);
            _out.WriteLine($"weekly_home_titani_done: {screen}, mainRewardDone={WeeklyBrain.IsMainRewardDone(ocrRes, frame)}");
            Assert.Equal(WkScreen.EventHome, screen);
            Assert.True(WeeklyBrain.IsMainRewardDone(ocrRes, frame));
        }

        // 帧2：列表页卡1 绿勾（已完成）→ 判 EventsList，勾探针只命中卡1
        var p2 = Path.Combine(WeeklyDir, "weekly_list_greencheck.png");
        if (File.Exists(p2))
        {
            var data = LoadPngBgra(p2, out int w, out int h);
            var frame = new FrameData(data, w, h, 0);
            var dets = det.Detect(frame);
            var ocrRes = TestOcr.Run(ocr, frame);
            var screen = WeeklyBrain.Classify(dets, ocrRes);
            _out.WriteLine($"weekly_list_greencheck: {screen}, check0={WeeklyBrain.DetectGreenCheck(frame, 0)}, " +
                           $"check1={WeeklyBrain.DetectGreenCheck(frame, 1)}, check2={WeeklyBrain.DetectGreenCheck(frame, 2)}");
            Assert.Equal(WkScreen.EventsList, screen);
            Assert.True(WeeklyBrain.DetectGreenCheck(frame, 0));
            Assert.False(WeeklyBrain.DetectGreenCheck(frame, 1));
            Assert.False(WeeklyBrain.DetectGreenCheck(frame, 2));
        }

        // 帧3：进行中的活动主页（既有回归帧）→ 完成探针不得误命中
        var p3 = Path.Combine(WeeklyDir, "weekly_event_home.png");
        if (File.Exists(p3))
        {
            var data = LoadPngBgra(p3, out int w, out int h);
            var frame = new FrameData(data, w, h, 0);
            var dets = det.Detect(frame);
            var ocrRes = TestOcr.Run(ocr, frame);
            var screen = WeeklyBrain.Classify(dets, ocrRes);
            _out.WriteLine($"weekly_event_home: {screen}, mainRewardDone={WeeklyBrain.IsMainRewardDone(ocrRes, frame)}");
            Assert.Equal(WkScreen.EventHome, screen);
            Assert.False(WeeklyBrain.IsMainRewardDone(ocrRes, frame));
        }
    }

    [Fact]
    public void Dump_DoneState_Frames()
    {
        using var ocr = new OcrRecognizer();
        using var det = new YoloDetector(
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx"),
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt"),
            new OnnxSessionFactory("CPU"));

        // 用户实机回归帧（2026-10-13 晚）：Titani del calcio 主页主要奖励已完成，
        // 实机未被识别（整场日志再无 EventHome）——dump 完整判定依据
        var p0 = Path.Combine(WeeklyDir, "weekly_home_titani_done.png");
        if (File.Exists(p0))
        {
            var data = LoadPngBgra(p0, out int w, out int h);
            var frame = new FrameData(data, w, h, 0);
            var dets = det.Detect(frame);
            var ocrRes = TestOcr.Run(ocr, frame);
            var screen = WeeklyBrain.Classify(dets, ocrRes);
            _out.WriteLine($"==== weekly_home_titani_done.png -> {screen} ====");
            foreach (var d in dets.Where(d => d.Score >= MyLeagueBrain.MinScore))
                _out.WriteLine($"  YOLO {d.ClassName} {d.Score:F2} @({d.CenterX:F2},{d.CenterY:F2})");
            _out.WriteLine($"  ContainsJoined 下一步={ocrRes.ContainsJoined("下一步")} 确认={ocrRes.ContainsJoined("确认")} " +
                           $"OK按钮={ocrRes.FindButton("OK") is not null}");
            foreach (var l in ocrRes.Lines)
                _out.WriteLine($"  L \"{l.Text}\" @({l.CenterX:F2},{l.CenterY:F2})");
            _out.WriteLine($"  mainRewardDone={WeeklyBrain.IsMainRewardDone(ocrRes, frame)} " +
                           $"勾区绿像素={CountGreen(frame, 0.920, 0.965, 0.635, 0.715)}");
        }

        using var ocr2 = new OcrRecognizer();

        // 帧1：主要奖励已获得的活动主页（右栏「获得的主要奖励」+ 绿勾）
        var p1 = Path.Combine(WeeklyDir, "weekly_reward_done.png");
        if (File.Exists(p1))
        {
            var data = LoadPngBgra(p1, out int w, out int h);
            var frame = new FrameData(data, w, h, 0);
            var ocrRes = TestOcr.Run(ocr, frame);
            _out.WriteLine("==== weekly_reward_done.png ====");
            foreach (var l in ocrRes.Lines)
                _out.WriteLine($"  L \"{l.Text}\" @({l.CenterX:F2},{l.CenterY:F2})");
            _out.WriteLine($"  绿勾区(0.92-0.965,0.635-0.715) 绿像素={CountGreen(frame, 0.92, 0.965, 0.635, 0.715)}");
            ScanGreenClusters(frame, 0.60, 1.0, 0.55, 0.75);
        }

        // 帧2：列表页第 1 卡绿勾（已完成活动）
        var p2 = Path.Combine(WeeklyDir, "weekly_list_greencheck.png");
        if (File.Exists(p2))
        {
            var data = LoadPngBgra(p2, out int w, out int h);
            var frame = new FrameData(data, w, h, 0);
            var ocrRes = TestOcr.Run(ocr, frame);
            _out.WriteLine("==== weekly_list_greencheck.png ====");
            foreach (var l in ocrRes.Lines)
                _out.WriteLine($"  L \"{l.Text}\" @({l.CenterX:F2},{l.CenterY:F2})");
            double[] rights = { 0.352, 0.643, 0.946 };
            for (int slot = 0; slot < 3; slot++)
                _out.WriteLine($"  卡{slot + 1}勾区({rights[slot] - 0.065:F3}-{rights[slot] - 0.005:F3},0.15-0.22) 绿像素={CountGreen(frame, rights[slot] - 0.065, rights[slot] - 0.005, 0.150, 0.220)}");
            ScanGreenClusters(frame, 0.0, 1.0, 0.12, 0.25);
        }
    }

    internal static int CountGreen(FrameData f, double x0, double x1, double y0, double y1)
    {
        var bgra = f.Bgra;
        int w = f.Width, h = f.Height;
        if (w < 100 || h < 100 || bgra.Length < w * h * 4) return 0;
        int count = 0;
        for (int y = Math.Max(0, (int)(h * y0)); y < Math.Min(h, (int)(h * y1)); y += 2)
            for (int x = Math.Max(0, (int)(w * x0)); x < Math.Min(w, (int)(w * x1)); x += 2)
            {
                int i = (y * w + x) * 4;
                if (bgra[i + 1] > 100 && bgra[i + 1] > bgra[i + 2] + 40 && bgra[i + 1] > bgra[i] + 40) count++;
            }
        return count;
    }

    /// <summary>扫描区域内绿色像素的分布（1/40 屏粒度），用于确认勾的实际包围盒。</summary>
    private void ScanGreenClusters(FrameData f, double x0, double x1, double y0, double y1)
    {
        var bgra = f.Bgra;
        int w = f.Width, h = f.Height;
        var cells = new Dictionary<(int, int), int>();
        for (int y = Math.Max(0, (int)(h * y0)); y < Math.Min(h, (int)(h * y1)); y += 2)
            for (int x = Math.Max(0, (int)(w * x0)); x < Math.Min(w, (int)(w * x1)); x += 2)
            {
                int i = (y * w + x) * 4;
                if (bgra[i + 1] > 100 && bgra[i + 1] > bgra[i + 2] + 40 && bgra[i + 1] > bgra[i] + 40)
                {
                    var key = ((int)(x / (double)w * 40), (int)(y / (double)h * 40));
                    cells[key] = cells.TryGetValue(key, out int c) ? c + 1 : 1;
                }
            }
        foreach (var ((cx, cy), c) in cells.Where(kvp => kvp.Value >= 5).OrderBy(kvp => kvp.Key.Item2))
            _out.WriteLine($"  绿簇 cell=({cx / 40.0:F2}-{(cx + 1) / 40.0:F2},{cy / 40.0:F2}-{(cy + 1) / 40.0:F2}) count={c}");
    }
}
