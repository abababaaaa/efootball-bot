using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

/// <summary>
/// 自写 letterbox / 降采样 与 OpenCV 参照实现的逐像素等价对照。
/// 主工程已移除 OpenCvSharp,此处仅作迁移保障:用 OpenCV 复刻旧 YoloDetector.Letterbox 管线
/// (BGRA→BGR→INTER_LINEAR 缩放→114 灰边居中→BGR2RGB),断言与新实现 max|Δ|≤1/255。
/// </summary>
public class LetterboxEquivalenceTests
{
    private readonly ITestOutputHelper _out;
    public LetterboxEquivalenceTests(ITestOutputHelper output) => _out = output;

    private static string TestDir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..");

    public static IEnumerable<object[]> Frames => new[]
    {
        new object[] { Path.Combine("TestData", "cnweekly", "cn_challenge_list_all_checked.png") },
        new object[] { Path.Combine("TestData", "weekly", "weekly_event_home.png") },
        new object[] { Path.Combine("TestData", "focus_state_2.png") },
    };

    [Theory]
    [MemberData(nameof(Frames))]
    public void Letterbox_MatchesOpenCvReference(string rel)
    {
        string file = Path.Combine(TestDir, rel);
        Assert.True(File.Exists(file), $"缺少测试帧: {file}");
        var frame = LoadBgra(file);
        const int inW = 640, inH = 640;

        var expected = OpenCvReference(frame, inW, inH, out float scale, out int padX, out int padY);
        var actual = RunNewLetterbox(frame, inW, inH, out float scale2, out int padX2, out int padY2);

        Assert.Equal(scale, scale2, 5);
        Assert.Equal(padX, padX2);
        Assert.Equal(padY, padY2);

        int maxDiff = 0, diffCount = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            int d = Math.Abs(expected[i] - actual[i]);
            if (d > maxDiff) maxDiff = d;
            if (d > 1) diffCount++;
        }
        _out.WriteLine($"{Path.GetFileName(rel)}: max|Δ|={maxDiff}, >1 的字节数={diffCount}/{expected.Length}");
        Assert.True(maxDiff <= 1, $"letterbox 与 OpenCV 参照偏差超限: max|Δ|={maxDiff}");
    }

    [Fact]
    public void DownscaleBilinear_4K长边压到1920_等比且纯色保真()
    {
        // 纯色 4K 帧 → 1920×1080,全部像素应等于原色(双线性在常数场上精确)
        const int w = 3840, h = 2160;
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            bgra[i * 4 + 0] = 30; bgra[i * 4 + 1] = 144; bgra[i * 4 + 2] = 255; bgra[i * 4 + 3] = 255;
        }
        var dst = ImageScaling.DownscaleBilinear(new FrameData(bgra, w, h, 0), 1920);
        Assert.Equal(1920, dst.Width);
        Assert.Equal(1080, dst.Height);
        for (int i = 0; i < dst.Width * dst.Height; i++)
        {
            Assert.Equal(30, dst.Bgra[i * 4 + 0]);
            Assert.Equal(144, dst.Bgra[i * 4 + 1]);
            Assert.Equal(255, dst.Bgra[i * 4 + 2]);
        }
    }

    [Fact]
    public void DownscaleBilinear_小帧原样返回_关闭时原样返回()
    {
        var small = new FrameData(new byte[1280 * 720 * 4], 1280, 720, 7);
        Assert.True(ReferenceEquals(small, ImageScaling.DownscaleBilinear(small, 1920)));
        Assert.True(ReferenceEquals(small, ImageScaling.DownscaleBilinear(small, 0)));
    }

    // ---------------- 参照实现(旧 OpenCV 管线复刻) ----------------

    private static byte[] OpenCvReference(FrameData frame, int inW, int inH,
        out float scale, out int padX, out int padY)
    {
        scale = Math.Min((float)inW / frame.Width, (float)inH / frame.Height);
        int newW = Math.Max(1, (int)Math.Round(frame.Width * scale));
        int newH = Math.Max(1, (int)Math.Round(frame.Height * scale));
        padX = (inW - newW) / 2;
        padY = (inH - newH) / 2;

        using var bgra = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.Bgra);
        using var bgr = new Mat();
        Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
        using var dst = new Mat(inH, inW, MatType.CV_8UC3, new Scalar(114, 114, 114));
        using var resized = new Mat();
        Cv2.Resize(bgr, resized, new OpenCvSharp.Size(newW, newH));
        resized.CopyTo(dst[new Rect(padX, padY, newW, newH)]);
        Cv2.CvtColor(dst, dst, ColorConversionCodes.BGR2RGB);

        var result = new byte[inH * inW * 3];
        System.Runtime.InteropServices.Marshal.Copy(dst.Data, result, 0, result.Length);
        return result;
    }

    private static byte[] RunNewLetterbox(FrameData frame, int inW, int inH,
        out float scale, out int padX, out int padY)
    {
        var rgb = new byte[inH * inW * 3];
        unsafe
        {
            fixed (byte* pSrc = frame.Bgra, pDst = rgb)
            {
                ImageScaling.ResizeBilinearBgraToRgbLetterbox(
                    pSrc, frame.Width, frame.Height, pDst, inW, inH, inW * 3, 114,
                    out _, out _, out padX, out padY);
            }
        }
        scale = Math.Min((float)inW / frame.Width, (float)inH / frame.Height);
        return rgb;
    }

    private static FrameData LoadBgra(string file)
    {
        using var mat = Cv2.ImRead(file, ImreadModes.Color);
        Assert.False(mat.Empty(), $"图片加载失败: {file}");
        using var bgra = new Mat();
        Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
        int byteCount = (int)(bgra.Total() * bgra.ElemSize());
        var data = new byte[byteCount];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, byteCount);
        return new FrameData(data, mat.Width, mat.Height, 0);
    }
}
