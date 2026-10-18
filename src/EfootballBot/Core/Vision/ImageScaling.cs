using System;
using EfootballBot.Core.Capture;

namespace EfootballBot.Core.Vision;

/// <summary>
/// BGRA 帧的双线性缩放（自写实现，语义对齐 OpenCV INTER_LINEAR）。
/// OCR 输入降采样与 YOLO letterbox 共用此处的插值核。
/// </summary>
public static class ImageScaling
{
    /// <summary>
    /// 帧长边超过 <paramref name="maxLongSide"/> 时双线性降采样（等比），否则原样返回。
    /// 返回的 FrameData 尺寸即后续归一化坐标的基准——OCR 词块坐标按传入帧宽高归一化，
    /// 降采样不改变 0~1 归一化结果。
    /// </summary>
    public static FrameData DownscaleBilinear(FrameData src, int maxLongSide)
    {
        if (maxLongSide <= 0 || src.Width <= 0 || src.Height <= 0)
            return src;
        int longSide = Math.Max(src.Width, src.Height);
        if (longSide <= maxLongSide)
            return src;

        double scale = (double)maxLongSide / longSide;
        int dstW = Math.Max(1, (int)Math.Round(src.Width * scale));
        int dstH = Math.Max(1, (int)Math.Round(src.Height * scale));
        var dst = new byte[dstW * dstH * 4];

        unsafe
        {
            fixed (byte* pSrc = src.Bgra, pDst = dst)
            {
                ResizeBilinearBgra(pSrc, src.Width, src.Height, pDst, dstW, dstH);
            }
        }
        return new FrameData(dst, dstW, dstH, src.TimestampMs);
    }

    /// <summary>BGRA → BGRA 双线性缩放。坐标映射与边界钳制复刻 OpenCV INTER_LINEAR：
    /// src = (dst + 0.5) * invScale - 0.5，越界坐标钳到边缘（等价 BORDER_REPLICATE）。</summary>
    internal static unsafe void ResizeBilinearBgra(byte* pSrc, int srcW, int srcH, byte* pDst, int dstW, int dstH)
    {
        double invX = (double)srcW / dstW;
        double invY = (double)srcH / dstH;
        float maxX = srcW - 1, maxY = srcH - 1;

        for (int y = 0; y < dstH; y++)
        {
            double sy = (y + 0.5) * invY - 0.5;
            sy = Math.Clamp(sy, 0.0, (double)maxY);
            int y0 = (int)sy;
            int y1 = Math.Min(y0 + 1, srcH - 1);
            float fy = (float)(sy - y0);

            byte* dstRow = pDst + (long)y * dstW * 4;
            for (int x = 0; x < dstW; x++)
            {
                double sx = (x + 0.5) * invX - 0.5;
                sx = Math.Clamp(sx, 0.0, (double)maxX);
                int x0 = (int)sx;
                int x1 = Math.Min(x0 + 1, srcW - 1);
                float fx = (float)(sx - x0);

                byte* p00 = pSrc + ((long)y0 * srcW + x0) * 4;
                byte* p01 = pSrc + ((long)y0 * srcW + x1) * 4;
                byte* p10 = pSrc + ((long)y1 * srcW + x0) * 4;
                byte* p11 = pSrc + ((long)y1 * srcW + x1) * 4;

                // 4 邻域加权，Round 半 away from zero 与 OpenCV 一致（CV_ROUND = rint）
                float w00 = (1 - fx) * (1 - fy), w01 = fx * (1 - fy), w10 = (1 - fx) * fy, w11 = fx * fy;
                dstRow[x * 4 + 0] = (byte)Math.Round(w00 * p00[0] + w01 * p01[0] + w10 * p10[0] + w11 * p11[0]);
                dstRow[x * 4 + 1] = (byte)Math.Round(w00 * p00[1] + w01 * p01[1] + w10 * p10[1] + w11 * p11[1]);
                dstRow[x * 4 + 2] = (byte)Math.Round(w00 * p00[2] + w01 * p01[2] + w10 * p10[2] + w11 * p11[2]);
                dstRow[x * 4 + 3] = (byte)Math.Round(w00 * p00[3] + w01 * p01[3] + w10 * p10[3] + w11 * p11[3]);
            }
        }
    }

    /// <summary>
    /// BGRA 源 → RGB 目标双线性缩放 + 灰边 letterbox（YOLO 预处理专用，一步到位）。
    /// 目标缓冲先整块填 <paramref name="padValue"/> 灰，再把等比缩放结果居中写入；
    /// <paramref name="dstStride"/> 为目标行字节数（= dstW*3，参数化便于复用缓冲）。
    /// </summary>
    internal static unsafe void ResizeBilinearBgraToRgbLetterbox(
        byte* pSrc, int srcW, int srcH,
        byte* pDst, int dstW, int dstH, int dstStride, byte padValue,
        out int newW, out int newH, out int padX, out int padY)
    {
        // 先铺灰边（复刻 Cv2.SetTo(114)），再缩放居中拷入
        for (int y = 0; y < dstH; y++)
        {
            byte* row = pDst + (long)y * dstStride;
            for (int x = 0; x < dstW * 3; x++)
                row[x] = padValue;
        }

        double scale = Math.Min((double)dstW / srcW, (double)dstH / srcH);
        newW = Math.Max(1, (int)Math.Round(srcW * scale));
        newH = Math.Max(1, (int)Math.Round(srcH * scale));
        padX = (dstW - newW) / 2;
        padY = (dstH - newH) / 2;

        double invX = (double)srcW / newW;
        double invY = (double)srcH / newH;
        float maxX = srcW - 1, maxY = srcH - 1;

        for (int y = 0; y < newH; y++)
        {
            double sy = (y + 0.5) * invY - 0.5;
            sy = Math.Clamp(sy, 0.0, (double)maxY);
            int y0 = (int)sy;
            int y1 = Math.Min(y0 + 1, srcH - 1);
            float fy = (float)(sy - y0);

            byte* dstRow = pDst + (long)(y + padY) * dstStride + (long)padX * 3;
            for (int x = 0; x < newW; x++)
            {
                double sx = (x + 0.5) * invX - 0.5;
                sx = Math.Clamp(sx, 0.0, (double)maxX);
                int x0 = (int)sx;
                int x1 = Math.Min(x0 + 1, srcW - 1);
                float fx = (float)(sx - x0);

                // BGRA → RGB：R=[i+2]、G=[i+1]、B=[i]（原 OpenCV 路径 BGRA→BGR→RGB 的净效果）
                byte* p00 = pSrc + ((long)y0 * srcW + x0) * 4;
                byte* p01 = pSrc + ((long)y0 * srcW + x1) * 4;
                byte* p10 = pSrc + ((long)y1 * srcW + x0) * 4;
                byte* p11 = pSrc + ((long)y1 * srcW + x1) * 4;

                float w00 = (1 - fx) * (1 - fy), w01 = fx * (1 - fy), w10 = (1 - fx) * fy, w11 = fx * fy;
                dstRow[x * 3 + 0] = (byte)Math.Round(w00 * p00[2] + w01 * p01[2] + w10 * p10[2] + w11 * p11[2]);
                dstRow[x * 3 + 1] = (byte)Math.Round(w00 * p00[1] + w01 * p01[1] + w10 * p10[1] + w11 * p11[1]);
                dstRow[x * 3 + 2] = (byte)Math.Round(w00 * p00[0] + w01 * p01[0] + w10 * p10[0] + w11 * p11[0]);
            }
        }
    }
}
