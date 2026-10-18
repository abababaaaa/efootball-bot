using System.Runtime.InteropServices;
using SkiaSharp;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>测试共用 PNG/JPG → BGRA 帧加载（SkiaSharp 路线，替代旧 OpenCvSharp Cv2.ImRead）。
/// 与旧路线像素语义一致：不透明游戏截图 → BGRA8888、Alpha=255、行连续。</summary>
internal static class TestFrameLoader
{
    public static byte[] LoadPngBgra(string file, out int w, out int h)
    {
        using var bmp = SKBitmap.Decode(file);
        Assert.False(bmp is null || bmp.IsEmpty, $"图片加载失败: {file}");
        w = bmp.Width;
        h = bmp.Height;
        using var converted = new SKBitmap(bmp.Width, bmp.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        Assert.True(bmp.CopyTo(converted, SKColorType.Bgra8888), $"像素格式转换失败: {file}");
        var data = new byte[w * h * 4];
        Marshal.Copy(converted.GetPixels(), data, 0, data.Length);
        return data;
    }
}
