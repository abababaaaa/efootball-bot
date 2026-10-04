using EfootballBot.Core.Bot;
using EfootballBot.Core.Capture;
using Xunit;

namespace EfootballBot.Tests;

public class InputLivenessProbeTests
{
    // 320×180、客户区充满整帧（OffX/OffY=0）的几何，便于按整数坐标推演
    private static readonly (double OffX, double OffY, double CliW, double CliH) Full = (0, 0, 320, 180);

    private static FrameData SolidFrame(int w, int h, byte gray)
        => BlockFrame(w, h, gray, gray, 0, 0, 0, 0);

    private static FrameData BlockFrame(int w, int h, byte bg, byte blockGray, int x0, int y0, int x1, int y1)
    {
        var bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            byte v = (x >= x0 && x < x1 && y >= y0 && y < y1) ? blockGray : bg;
            int i = (y * w + x) * 4;
            bgra[i] = v;
            bgra[i + 1] = v;
            bgra[i + 2] = v;
            bgra[i + 3] = 255;
        }
        return new FrameData(bgra, w, h, 0);
    }

    private static FrameData JitterFrame(int w, int h, byte gray)
    {
        var bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int d = ((x + y) & 1) == 0 ? 3 : -3;
            byte v = (byte)Math.Clamp(gray + d, 0, 255);
            int i = (y * w + x) * 4;
            bgra[i] = v;
            bgra[i + 1] = v;
            bgra[i + 2] = v;
            bgra[i + 3] = 255;
        }
        return new FrameData(bgra, w, h, 0);
    }

    [Fact]
    public void Mad_IdenticalSignatures_IsZero()
    {
        var f = SolidFrame(320, 180, 100);

        var a = InputLivenessProbe.Signature(f, Full);
        var b = InputLivenessProbe.Signature(f, Full);

        Assert.Equal(0.0, InputLivenessProbe.Mad(a, b));
    }

    [Fact]
    public void Mad_HighlightBlockMoves_ExceedsThreshold()
    {
        // 中心磁贴区为客户区 x0.15~0.85(帧 48~272)、y0.30~0.85(帧 54~153)。
        // 亮块（焦点高亮）在区内左右平移 100px，两次覆盖的网格互不重叠。
        var a = InputLivenessProbe.Signature(
            BlockFrame(320, 180, 20, 255, 60, 80, 120, 120), Full);
        var b = InputLivenessProbe.Signature(
            BlockFrame(320, 180, 20, 255, 160, 80, 220, 120), Full);

        double mad = InputLivenessProbe.Mad(a, b);
        Assert.True(mad >= InputLivenessProbe.MoveMadMin, $"MAD={mad}");
    }

    [Fact]
    public void Mad_BackgroundMicroJitter_BelowThreshold()
    {
        var a = InputLivenessProbe.Signature(SolidFrame(320, 180, 100), Full);
        var b = InputLivenessProbe.Signature(JitterFrame(320, 180, 100), Full);

        double mad = InputLivenessProbe.Mad(a, b);
        Assert.True(mad < InputLivenessProbe.MoveMadMin, $"MAD={mad}");
    }
}
