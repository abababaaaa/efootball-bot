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

// 校准/回归：2026-10-30 用户截图 + GitHub issue #1（单场周活动「选择赛事等级」页）。
// 首次运行 = 看清 OCR 词与坐标、聚焦行亮度特征（标定探针阈值）；
// 之后作为回归锁定（分类不得回退）。
public class WkDifficultyRealFrameTests
{
    private readonly ITestOutputHelper _out;
    public WkDifficultyRealFrameTests(ITestOutputHelper output) => _out = output;

    private static string TestDir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..");
    private static string WeeklyDir => Path.Combine(TestDir, "TestData", "weekly");

    private static readonly string[] Frames =
    {
        "wk_difficulty_popup.png",
        "wk_difficulty_list.png",
        "wk_difficulty_focus.png",
        "issue1_difficulty_a.png",
        "issue1_difficulty_b.png",
        "wk_difficulty_confirm.png",
        "wk_difficulty_confirm2.png",
    };

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

    [Fact]
    public void Dump_WkDifficulty_Frames()
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
            var wk = WeeklyBrain.Classify(dets, ocrRes,
                MyLeagueScenario.CornerHasContent(frame), matchContext: false,
                MyLeagueScenario.IsMostlyLightPage(frame));
            var ml = MyLeagueBrain.Classify(dets, ocrRes,
                MyLeagueScenario.CornerHasContent(frame), false,
                MyLeagueScenario.IsMostlyLightPage(frame));

            _out.WriteLine($"==== {name} ({w}x{h}) -> wk={wk} ml={ml} ====");
            foreach (var d in dets.Where(d => d.Score >= MyLeagueBrain.MinScore))
                _out.WriteLine($"  YOLO {d.ClassName} {d.Score:F2} @({d.CenterX:F2},{d.CenterY:F2})");
            foreach (var wd in ocrRes.Words)
                _out.WriteLine($"  W \"{wd.Text}\" @({wd.CenterX:F2},{wd.CenterY:F2}) wh=({wd.W:F3},{wd.H:F3})");
            foreach (var l in ocrRes.Lines)
                _out.WriteLine($"  L \"{l.Text}\" @({l.CenterX:F2},{l.CenterY:F2})");

            // 难度行亮度/描边探针：对每个难度词所在行带（y±0.035，词左 -0.28~右 +0.06）
            // 统计亮像素（lum>200）与蓝像素，标定「聚焦行=白底高亮」判定阈值。
            var bgra = frame.Bgra;
            foreach (var diff in MyLeagueBrain.Difficulties)
            {
                var word = ocrRes.Words.FirstOrDefault(x => x.Text.Replace(" ", "").Contains(diff));
                if (word is null) { _out.WriteLine($"  [probe {diff}] word missing"); continue; }
                double x0 = Math.Max(0, word.CenterX - 0.28), x1 = Math.Min(1, word.CenterX + 0.06);
                int bright = 0, blue = 0, total = 0;
                for (int y = Math.Max(0, (int)((word.CenterY - 0.035) * h)); y < Math.Min(h, (int)((word.CenterY + 0.035) * h)); y += 2)
                    for (int x = (int)(x0 * w); x < (int)(x1 * w); x += 2)
                    {
                        int i = (y * w + x) * 4;
                        int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                        int lum = (r + g + b) / 3;
                        total++;
                        if (lum > 200) bright++;
                        if (b > 120 && b > g + 40 && b > r + 40) blue++;
                    }
                _out.WriteLine($"  [probe {diff}] y={word.CenterY:F3} bright={bright} blue={blue} total={total}");
            }
        }
    }

    [Fact]
    public void EventDifficulty_ClassifiesAndFocusProbe()
    {
        // 回归锁定（2026-10-30 实机三帧 + issue #1）：
        // ① 三帧都必须判 EventDifficulty——旧逻辑 wk=None/ml=DifficultyPage 无人接管原地干等；
        // ② 说明弹窗帧焦点探针必须 null（背景压暗全 0，绝不误报聚焦行）；
        // ③ 未滚动帧聚焦=新手（默认光标）、滚动一行的帧聚焦=传奇（白底高亮 1283/1285 vs 其余≤81）；
        // ④ issue #1 手机照（GUI 覆盖层盖住列表）标题仍可读：整页判定可落空，由共享分类
        //    兜 DifficultyPage、场景层双信号接管——此处只锁定弹窗 OK 可检出。
        using var ocr0 = new OcrRecognizer();
        foreach (var name in Frames)
        {
            var path = Path.Combine(WeeklyDir, name);
            if (!File.Exists(path)) continue;
            var data = LoadPngBgra(path, out int w, out int h);
            var frame = new FrameData(data, w, h, 0);
            var ocrRes = TestOcr.Run(ocr0, frame);
            var wk = WeeklyBrain.Classify(Array.Empty<YoloDetection>(), ocrRes,
                MyLeagueScenario.CornerHasContent(frame), matchContext: false,
                MyLeagueScenario.IsMostlyLightPage(frame));

            if (name == "wk_difficulty_popup.png")
            {
                Assert.Equal(WkScreen.EventDifficulty, wk);
                Assert.Null(WeeklyBrain.DetectFocusedDifficulty(frame, ocrRes));
                var ok = ocrRes.FindButton("OK");
                Assert.NotNull(ok);
                Assert.True(ok!.CenterY < 0.85, "OK 必须在屏中部（底栏 B 返回不算）");
            }
            else if (name == "wk_difficulty_list.png")
            {
                Assert.Equal(WkScreen.EventDifficulty, wk);
                Assert.Equal("新手", WeeklyBrain.DetectFocusedDifficulty(frame, ocrRes));
            }
            else if (name == "wk_difficulty_focus.png")
            {
                Assert.Equal(WkScreen.EventDifficulty, wk);
                Assert.Equal("传奇", WeeklyBrain.DetectFocusedDifficulty(frame, ocrRes));
                Assert.False(WeeklyBrain.IsDifficultyConfirmPopup(ocrRes), "纯列表帧不得误判确认弹窗");
            }
            else if (name.StartsWith("wk_difficulty_confirm"))
            {
                // 选行后的最终确认弹窗（是否要选择传奇？+ 进入/取消，默认焦点在取消）：
                // 背后页面词仍可读 → 仍判 EventDifficulty（由场景层 ①' 分支 ↑+A 处理）
                Assert.Equal(WkScreen.EventDifficulty, wk);
                Assert.True(WeeklyBrain.IsDifficultyConfirmPopup(ocrRes), "确认弹窗必须命中（否则会被当列表按下）");
                Assert.Null(WeeklyBrain.DetectFocusedDifficulty(frame, ocrRes));
            }
        }
    }
}
