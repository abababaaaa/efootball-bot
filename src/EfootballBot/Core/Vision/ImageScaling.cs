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

    /// <summary>
    /// 按归一化矩形裁切并做整数倍最近邻放大（放大后 OCR 小字更稳）。
    /// 用于「赛后活动地图」左上小图标旁的数字：全帧 OCR 会把细白字与红图标并读成
    /// 「C'」而丢掉数字，裁切放大后稳定读出「©1」。越界坐标自动钳制。
    /// </summary>
    public static FrameData CropUpscale(FrameData src, double x0, double x1, double y0, double y1, int scale)
        => CropUpscale(src, x0, x1, y0, y1, scale, out _, out _, out _, out _);

    /// <summary>
    /// 同上，并回吐裁切矩形（源图像素坐标与放大后尺寸）。行级重读需要把裁切图上的
    /// 归一化词坐标映回整帧，必须知道真实裁切原点（越界已被钳制，不能按入参自己算）。
    /// </summary>
    public static FrameData CropUpscale(FrameData src, double x0, double x1, double y0, double y1, int scale,
        out int offsetX, out int offsetY, out int cropW, out int cropH)
    {
        offsetX = offsetY = cropW = cropH = 0;
        if (src.Width <= 0 || src.Height <= 0 || src.Bgra.Length == 0 || scale < 1)
            return src;
        int sx0 = Math.Clamp((int)(x0 * src.Width), 0, src.Width - 1);
        int sx1 = Math.Clamp((int)(x1 * src.Width), sx0 + 1, src.Width);
        int sy0 = Math.Clamp((int)(y0 * src.Height), 0, src.Height - 1);
        int sy1 = Math.Clamp((int)(y1 * src.Height), sy0 + 1, src.Height);
        int cw = (sx1 - sx0) * scale, ch = (sy1 - sy0) * scale;
        var dst = new byte[cw * ch * 4];
        for (int y = 0; y < ch; y++)
        {
            int sy = sy0 + y / scale;
            for (int x = 0; x < cw; x++)
            {
                int si = (sy * src.Width + sx0 + x / scale) * 4;
                int di = (y * cw + x) * 4;
                dst[di] = src.Bgra[si];
                dst[di + 1] = src.Bgra[si + 1];
                dst[di + 2] = src.Bgra[si + 2];
                dst[di + 3] = 255;
            }
        }
        offsetX = sx0;
        offsetY = sy0;
        cropW = cw;
        cropH = ch;
        return new FrameData(dst, cw, ch, src.TimestampMs);
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
