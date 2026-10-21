using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 难度选择页蓝勾校准（实机帧 difficulty_new_ui.png 12:10 / difficulty_live_20261020.png 18:21）：
// 旧窗口（词左右 ±0.06）把「光标蓝框左边线」和「蓝勾」混在一个窗里——光标所在行（新手）
// 蓝框计数 150+ 碾过勾行（传奇 ≈24），同一会话内判定在两者间抖动，舞步死循环。
// 本测试 dump 各难度词在若干候选窗内的蓝像素计数，校准出「只含勾、不含框边线」的窗口。
public class DifficultyNewUiCalibrationTests
{
    private readonly ITestOutputHelper _out;
    public DifficultyNewUiCalibrationTests(ITestOutputHelper output) => _out = output;

    private static readonly string[] Frames =
    {
        "difficulty_new_ui.png",
        "difficulty_live_20261020.png",
    };

    private static string FramePath(string name) => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestData", name);

    [Fact]
    public void Calibrate_BlueCheck_Window()
    {
        foreach (var name in Frames)
        {
            var data = TestFrameLoader.LoadPngBgra(FramePath(name), out int w, out int h);
            var frame = new FrameData(data, w, h, 0);
            using var ocr = new OcrRecognizer();
            var ocrRes = TestOcr.Run(ocr, frame);

            _out.WriteLine($"==== {name} {w}x{h}, 词数 {ocrRes.Words.Count} ====");
            foreach (var word in ocrRes.Words)
                _out.WriteLine($"  W \"{word.Text}\" @({word.CenterX:F3},{word.CenterY:F3}) wh=({word.W:F3},{word.H:F3})");

            foreach (var (tag, a, b, yh) in new[]
                     {
                         ("旧窗[-0.06,0]y.03", -0.06, 0.0, 0.03),
                         ("勾窄窗[-0.034,-0.004]y.016", -0.034, -0.004, 0.016),
                         ("勾宽窗[-0.034,0]y.016", -0.034, 0.0, 0.016),
                         ("框带[-0.058,-0.040]y.016", -0.058, -0.040, 0.016),
                     })
            {
                _out.WriteLine($"-- 窗 {tag} --");
                foreach (var diff in MyLeagueBrain.Difficulties)
                {
                    var word = ocrRes.Words.FirstOrDefault(x => x.Text.Replace(" ", "").Contains(diff));
                    if (word is null) { _out.WriteLine($"   {diff}: 词未检出"); continue; }
                    int blue = CountBlueWindow(frame, word, a, b, yh);
                    _out.WriteLine($"   {diff}: 蓝={blue}（词@x{word.CenterX:F3},y{word.CenterY:F3} w{word.W:F3}）");
                }
            }

            var selected = MyLeagueBrain.DetectSelectedDifficulty(frame, ocrRes);
            _out.WriteLine($"DetectSelectedDifficulty（当前实现）= {selected ?? "null"}");
        }
    }

    /// <summary>蓝像素计数：词 Y ±yh 带、词左缘起 [a,b]（相对词左缘，负=向左）单窗。</summary>
    private static int CountBlueWindow(FrameData f, OcrWord word, double a, double b, double yh)
    {
        var (w, h, bgra) = (f.Width, f.Height, f.Bgra);
        int count = 0;
        int y0 = Math.Max(0, (int)((word.CenterY - yh) * h));
        int y1 = Math.Min(h, (int)((word.CenterY + yh) * h));
        double xa = word.CenterX - word.W / 2 + a;
        double xb = word.CenterX - word.W / 2 + b;
        for (int y = y0; y < y1; y += 2)
            for (int x = Math.Max(0, (int)(xa * w)); x < Math.Min(w, (int)(xb * w)); x += 2)
            {
                int i = (y * w + x) * 4;
                if (bgra[i] > 120 && bgra[i] > bgra[i + 1] + 40 && bgra[i] > bgra[i + 2] + 40) count++;
            }
        return count;
    }
}
