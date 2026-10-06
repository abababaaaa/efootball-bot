using EfootballBot.Core.Bot;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Flow;
using EfootballBot.Core.Vision;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>
/// 「选择赛事等级」蓝勾扫描回归：真机故障（2026-10-06）为 OCR 漏识整行难度名
/// （「传奇」行只剩积分「0L820」），旧按词匹配找不到行 → 永远报「无勾」误改选。
/// 修复后凑齐 7 行按 y 顺序对号入座，对勾 ROI 用行首固定 x 区。
/// </summary>
public sealed class DifficultySelectorTests
{
    private const int W = 1280, H = 720;

    private static byte[] BlackFrame() => new byte[W * H * 4];

    /// <summary>在归一化矩形内填蓝勾色（RGB(37,125,232)，与真机实测一致）。</summary>
    private static void PaintBlueCheck(byte[] bgra, double x0, double y0, double w, double h)
    {
        int px0 = (int)(x0 * W), py0 = (int)(y0 * H);
        int px1 = (int)((x0 + w) * W), py1 = (int)((y0 + h) * H);
        for (int y = py0; y < py1; y++)
            for (int x = px0; x < px1; x++)
            {
                int i = (y * W + x) * 4;
                bgra[i] = 232; bgra[i + 1] = 125; bgra[i + 2] = 37;
            }
    }

    private static Observation Observe(byte[] bgra, params (string text, double x, double y)[] words)
    {
        var ocr = new OcrResult(
            words.Select(w => new OcrWord(w.text, w.x, w.y, 0.10, 0.035)).ToList(), W, H);
        return new Observation(new FrameData(bgra, W, H, 1), ocr,
            GameScreen.DifficultySelect, 5.0, Array.Empty<string>());
    }

    // 7 行难度行 y 中心（真机诊断行距 ~0.063）
    private static readonly double[] RowYs = { 0.36, 0.43, 0.49, 0.55, 0.62, 0.68, 0.74 };

    [Fact]
    public void CheckedLegend_NameSwallowedByOcr_StillFoundByPosition()
    {
        // 「传奇」行名被吞只剩积分数字；其余行也只给积分（模拟大面积漏识）
        var bgra = BlackFrame();
        PaintBlueCheck(bgra, 0.222, RowYs[6] - 0.018, 0.026, 0.036); // ✓ 在传奇行首
        var obs = Observe(bgra,
            ("业余", 0.24, RowYs[1] - 0.017), ("1,260", 0.68, RowYs[1] - 0.017),
            ("标准", 0.24, RowYs[2] - 0.017), ("1,400", 0.68, RowYs[2] - 0.017),
            ("0120", 0.68, RowYs[0] - 0.017),          // 新手：名被吞
            ("0150", 0.68, RowYs[3] - 0.017),          // 职业：名被吞
            ("顶尖球员", 0.24, RowYs[4] - 0.017), ("1,610", 0.68, RowYs[4] - 0.017),
            ("01680", 0.68, RowYs[5] - 0.017),         // 超级球星：名被吞
            ("0L820", 0.68, RowYs[6] - 0.017));        // 传奇：名被吞

        var (idx, diag, rowLevels) = DifficultySelector.ScanCheckedRow(obs);
        Assert.Equal(6, idx); // 传奇
        Assert.Contains("0L820", diag);
        Assert.Equal(7, rowLevels.Count);
    }

    [Fact]
    public void NoCheck_ReturnsMinusOne()
    {
        var obs = Observe(BlackFrame(),
            ("业余", 0.24, RowYs[1] - 0.017), ("1,260", 0.68, RowYs[1] - 0.017));
        var (idx, _, _) = DifficultySelector.ScanCheckedRow(obs);
        Assert.Equal(-1, idx);
    }

    [Fact]
    public void KeyMatchVariant_TwoRows_LegendFound()
    {
        // 关键比赛变体：仅 2 行（超级球星/传奇），y 位置与 7 行版不同，勾在传奇
        var bgra = BlackFrame();
        PaintBlueCheck(bgra, 0.222, 0.397 - 0.018, 0.026, 0.036);
        var obs = Observe(bgra,
            ("超级球星", 0.24, 0.33 - 0.017), ("3,360", 0.68, 0.33 - 0.017),
            ("传奇", 0.24, 0.397 - 0.017), ("3,640", 0.68, 0.397 - 0.017));

        var (idx, _, rowLevels) = DifficultySelector.ScanCheckedRow(obs);
        Assert.Equal(6, idx); // 传奇
        Assert.Equal(new[] { 5, 6 }, rowLevels);
    }

    [Fact]
    public void FewerRows_FallsBackToNameMatch()
    {
        // 只 OCR 到 2 行（凑不齐 7 行）：回退名称匹配，勾仍按行首固定 x 检测
        var bgra = BlackFrame();
        PaintBlueCheck(bgra, 0.222, RowYs[6] - 0.018, 0.026, 0.036);
        var obs = Observe(bgra,
            ("传奇", 0.24, RowYs[6] - 0.017), ("1,820", 0.68, RowYs[6] - 0.017),
            ("业余", 0.24, RowYs[1] - 0.017), ("1,260", 0.68, RowYs[1] - 0.017));

        var (idx, _, _) = DifficultySelector.ScanCheckedRow(obs);
        Assert.Equal(6, idx);
    }

    [Fact]
    public void SubtitleRowWithMisreadDigit_NotCountedAsDifficultyRow()
    {
        // 真机故障（2026-10-06 截图）：副标题「可赚取的我的联赛积分」中"赛"被 OCR
        // 识成"0"→「可赚取的我的联0积分」，含数字通过筛选凑出 8 行 → 位置映射失效
        var bgra = BlackFrame();
        PaintBlueCheck(bgra, 0.222, RowYs[6] - 0.018, 0.026, 0.036); // ✓ 在传奇行
        var obs = Observe(bgra,
            ("可赚取的我的联0积分", 0.40, 0.33 - 0.017), // 副标题（CenterY=0.33）
            ("0120", 0.68, RowYs[0] - 0.017),          // 新手：名被吞
            ("业余", 0.24, RowYs[1] - 0.017), ("1,260", 0.68, RowYs[1] - 0.017),
            ("标准", 0.24, RowYs[2] - 0.017), ("1,400", 0.68, RowYs[2] - 0.017),
            ("0150", 0.68, RowYs[3] - 0.017),          // 职业：名被吞
            ("顶尖球员", 0.24, RowYs[4] - 0.017), ("1,610", 0.68, RowYs[4] - 0.017),
            ("01680", 0.68, RowYs[5] - 0.017),         // 超级球星：名被吞
            ("0L820", 0.68, RowYs[6] - 0.017));        // 传奇：名被吞

        var (idx, _, rowLevels) = DifficultySelector.ScanCheckedRow(obs);

        Assert.Equal(6, idx); // 蓝勾仍正确落在传奇
        Assert.Equal(7, rowLevels.Count);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6 }, rowLevels);
    }
}
