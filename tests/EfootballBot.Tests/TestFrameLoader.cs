using System.Runtime.InteropServices;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
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

/// <summary>
/// 同步跑一次 OCR（测试专用）。
///
/// 为什么集中在这里：此前 22 个测试文件里散落着 34 处
/// <c>ocr.RecognizeAsync(frame).GetAwaiter().GetResult()</c>，xUnit 分析器对此报
/// xUnit1031（阻塞式取 Task 可能死锁）。测试里其实没有 SynchronizationContext、
/// OCR 是纯 CPU 计算，阻塞是安全的——但警告长期淹没真正的问题。
/// 把阻塞收进这一个非测试方法：测试代码保持同步写法，分析器不再逐处报警。
/// </summary>
internal static class TestOcr
{
    public static OcrResult Run(OcrRecognizer ocr, FrameData frame)
    {
        // Task.Run 明确切到线程池：即使将来引入同步上下文也不会与 UI 线程互相等待
        return Task.Run(() => ocr.RecognizeAsync(frame)).GetAwaiter().GetResult();
    }
}
