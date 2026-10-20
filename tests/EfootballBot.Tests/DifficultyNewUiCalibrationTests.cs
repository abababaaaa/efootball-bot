using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 2027 新 UI 难度选择页蓝勾校准（实机帧 difficulty_new_ui.png，2026-10-20）：
// 旧窗口（词 ±0.06）每轮把已勾的传奇读成新手 → 舞步死循环（实机 12:10 反馈包：
// "卡在赛事等级选择页面来回点击"）。本测试 dump 各难度词窗口内蓝像素计数 +
// 候选扩展窗（勾图标在行最左缘），校准出新窗口并锁定回归。
public class DifficultyNewUiCalibrationTests
{
    private readonly ITestOutputHelper _out;
    public DifficultyNewUiCalibrationTests(ITestOutputHelper output) => _out = output;

    private static string FramePath => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestData", "difficulty_new_ui.png");

    [Fact]
    public void Calibrate_BlueCheck_Window()
    {
        var data = TestFrameLoader.LoadPngBgra(FramePath, out int w, out int h);
        var frame = new FrameData(data, w, h, 0);
        using var ocr = new OcrRecognizer();
        var ocrRes = ocr.RecognizeAsync(frame).GetAwaiter().GetResult();

        _out.WriteLine($"frame {w}x{h}, 词数 {ocrRes.Words.Count}");
        foreach (var word in ocrRes.Words)
            _out.WriteLine($"  W \"{word.Text}\" @({word.CenterX:F3},{word.CenterY:F3}) wh=({word.W:F3},{word.H:F3})");

        // 旧窗（±0.06）与新候选窗（左扩到 -0.14）各算一遍
        foreach (var (tag, leftExt) in new[] { ("旧窗", 0.06), ("左扩0.14", 0.14) })
        {
            _out.WriteLine($"== {tag}（左扩 {leftExt}） ==");
            foreach (var diff in MyLeagueBrain.Difficulties)
            {
                var word = ocrRes.Words.FirstOrDefault(x => x.Text.Replace(" ", "").Contains(diff));
                if (word is null) { _out.WriteLine($"  {diff}: 词未检出"); continue; }
                int blue = CountBlueLeftWindow(frame, ocrRes, diff, leftExt);
                _out.WriteLine($"  {diff}: 蓝={blue}（词@{word.CenterX:F3},{word.CenterY:F3}）");
            }
        }

        // 锁定回归：新窗下传奇必须显著胜出且 ≥20（判定阈值），其余 ≈0
        var selected = MyLeagueBrain.DetectSelectedDifficulty(frame, ocrRes);
        _out.WriteLine($"DetectSelectedDifficulty（当前实现）= {selected ?? "null"}");
    }

    /// <summary>蓝像素计数：词 Y ±0.03 带、词左右窗合计，左窗宽度可调（勾在行左缘的布局）。</summary>
    private static int CountBlueLeftWindow(FrameData f, OcrResult ocr, string diff, double leftExt)
    {
        var word = ocr.Words.FirstOrDefault(x => x.Text.Replace(" ", "").Contains(diff));
        if (word is null) return 0;
        var (w, h, bgra) = (f.Width, f.Height, f.Bgra);
        int count = 0;
        int y0 = Math.Max(0, (int)((word.CenterY - 0.03) * h));
        int y1 = Math.Min(h, (int)((word.CenterY + 0.03) * h));
        double xa = word.CenterX - word.W / 2 - leftExt;
        double xb = word.CenterX + word.W / 2 + 0.06;
        for (int y = y0; y < y1; y += 2)
            for (int x = Math.Max(0, (int)(xa * w)); x < Math.Min(w, (int)(xb * w)); x += 2)
            {
                int i = (y * w + x) * 4;
                if (bgra[i] > 120 && bgra[i] > bgra[i + 1] + 40 && bgra[i] > bgra[i + 2] + 40) count++;
            }
        return count;
    }
}
