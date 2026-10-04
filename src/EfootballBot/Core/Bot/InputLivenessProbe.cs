using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Bot;

/// <summary>
/// 手柄输入活性自检（纯函数）：对主页中心磁贴区做 48×27 灰度下采样签名，
/// 调用方在按方向键前后各取一次签名，MAD ≥ MoveMadMin 即游戏确实收到了输入。
/// 背景见 docs/superpowers/specs/2026-10-04-input-liveness-probe-design.md。
/// </summary>
public static class InputLivenessProbe
{
    /// <summary>中心磁贴区（客户区归一化 x/y/w/h），覆盖主页磁贴行、避开顶部头部栏。</summary>
    public static readonly NormRect CenterBand = new(0.15, 0.30, 0.70, 0.55);

    public const int SigW = 48;
    public const int SigH = 27;

    /// <summary>签名平均绝对差阈值：焦点高亮位移远超此值，静止噪声接近 0（与 ChallengeCardReader.MoveMadMin 同尺度）。</summary>
    public const double MoveMadMin = 8.0;

    /// <summary>
    /// 中心磁贴区灰度签名：每个网格单元取区内像素亮度均值（BT.601）。
    /// geo 为 GetWindowFrame 的客户区几何（帧偏移 + 客户区宽高）。
    /// </summary>
    public static byte[] Signature(FrameData f, (double OffX, double OffY, double CliW, double CliH) geo)
    {
        var sig = new byte[SigW * SigH];
        if (f.Bgra.Length == 0 || f.Width <= 0 || f.Height <= 0
            || geo.CliW <= 0 || geo.CliH <= 0) return sig;

        var fr = ChallengeCardReader.Map(f, CenterBand, geo.OffX, geo.OffY, geo.CliW, geo.CliH);
        var (px, py, pw, ph) = FrameAnalyzer.PixelRect(f, fr);
        if (pw < 2 || ph < 2) return sig;

        var b = f.Bgra;
        for (int sy = 0; sy < SigH; sy++)
        {
            int y0 = py + sy * ph / SigH;
            int y1 = Math.Max(y0 + 1, py + (sy + 1) * ph / SigH);
            for (int sx = 0; sx < SigW; sx++)
            {
                int x0 = px + sx * pw / SigW;
                int x1 = Math.Max(x0 + 1, px + (sx + 1) * pw / SigW);
                long acc = 0;
                int cnt = 0;
                for (int yy = y0; yy < y1; yy++)
                {
                    int row = yy * f.Width * 4;
                    for (int xx = x0; xx < x1; xx++)
                    {
                        int i = row + xx * 4;
                        acc += (b[i + 2] * 299L + b[i + 1] * 587L + b[i] * 114L) / 1000L;
                        cnt++;
                    }
                }
                sig[sy * SigW + sx] = cnt == 0 ? (byte)0 : (byte)(acc / cnt);
            }
        }
        return sig;
    }

    /// <summary>两个签名的平均绝对差；空签名或长度不一致返回 0（调用方已保证帧有效）。</summary>
    public static double Mad(byte[] a, byte[] b)
    {
        if (a.Length == 0 || a.Length != b.Length) return 0;
        long sum = 0;
        for (int i = 0; i < a.Length; i++) sum += Math.Abs((int)a[i] - b[i]);
        return sum / (double)a.Length;
    }
}
