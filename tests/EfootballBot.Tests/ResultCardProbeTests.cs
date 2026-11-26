using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 国服赛后「全场结束 / 球员评价」页左侧那块白色大卡（蓝色箭头 + 「下一步」）的鼠标落点。
//
// 现场（用户 2026-11-25 23:52，国服周活动）：打完一场停在这页，日志连刷
// 「[国服周活动] 全场结束→下一步：第150轮·鼠标(国服右下兜底坐标 0.870,0.920)」——
// 这一页根本没有右下按钮（右下角是「RS ● ● ●」翻页指示），点了 150+ 轮、约 8 分钟原地不动，
// 最后只能手动停挂机。同一页在 2026-11-25 上午已经卡过一次（v7.5.3 那笔「17 轮」）。
//
// 根因不在「有没有鼠标兜底」（兜底一直在），而在**落点**：这张卡的位置随页面版本变
// （版式 A 卡高 0.41、版式 B 卡高 0.86，箭头 y 分别是 0.215 / 0.428），写死任何单一坐标都会落空。
// 本用例锁三件事：
//   ① 两版实机帧上，落点必须在左侧白卡里（贴住「下一步」/箭头），绝不回到右下角；
//   ② OCR 整帧漏读按钮字时，像素探针仍能在左 1/3 找到白卡并点卡内箭头位置；
//   ③ 非白卡页（白底文档页、白横条、无任何线索）不许被它抢走落点。
public class ResultCardProbeTests
{
    private readonly ITestOutputHelper _out;
    public ResultCardProbeTests(ITestOutputHelper output) => _out = output;

    private static OcrWord W(string text, double cx, double cy)
        => new(text, cx - 0.05, cy - 0.015, 0.1, 0.03);

    private static OcrResult Ocr(params OcrWord[] words) => new(words, 1280, 720);

    private static YoloDetection Box(string cls, double score, double cx, double cy)
        => new(cls, 0, (float)score, cx - 0.05, cy - 0.03, 0.10, 0.06);

    /// <summary>造一帧深色背景 + 若干近白矩形（模拟「深色结算页 + 左侧白卡」）。</summary>
    private static FrameData Frame(int w, int h,
        params (double X0, double Y0, double X1, double Y1)[] whites)
        => FrameOn(w, h, dark: true, whites);

    private static FrameData FrameOn(int w, int h, bool dark,
        params (double X0, double Y0, double X1, double Y1)[] whites)
    {
        var bgra = new byte[w * h * 4];
        byte baseL = dark ? (byte)24 : (byte)246;
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = baseL;
            bgra[i + 1] = baseL;
            bgra[i + 2] = (byte)(dark ? baseL + 4 : baseL);
            bgra[i + 3] = 255;
        }
        foreach (var (x0, y0, x1, y1) in whites)
            for (int y = (int)(h * y0); y < (int)(h * y1); y++)
                for (int x = (int)(w * x0); x < (int)(w * x1); x++)
                {
                    int i = (y * w + x) * 4;
                    bgra[i] = 250;
                    bgra[i + 1] = 250;
                    bgra[i + 2] = 253;
                }
        return new FrameData(bgra, w, h, 0);
    }

    // ---------------- ① OCR 读到卡面文字：点字 ----------------

    [Fact]
    public void Locate_OCR读到卡面下一步_点在字上()
    {
        // 版式 B 实机：白卡内「下一步」三个字中心 (0.132, 0.500)；同屏还有「球员评价」「全场」作陪
        var ocr = Ocr(
            W("全场", 0.615, 0.072),
            W("球员评价", 0.616, 0.290),
            W("下一步", 0.132, 0.500));
        var hit = ResultCardProbe.Locate(ocr, null, attempt: 3);
        Assert.NotNull(hit);
        _out.WriteLine($"hit = ({hit!.Value.X:F3},{hit.Value.Y:F3}) [{hit.Value.Where}]");
        Assert.Equal("白卡「下一步」文字", hit.Value.Where);
        Assert.InRange(hit.Value.X, 0.05, 0.25);
        Assert.InRange(hit.Value.Y, 0.40, 0.60);
    }

    [Fact]
    public void Locate_合并长行不许抢卡面按钮()
    {
        // 同一排版行把卡面「下一步」和右边的表头合成一条长行时，中心会漂到卡外——不许当锚点
        var ocr = Ocr(
            W("球员评价", 0.616, 0.290),
            W("下一步球员评价", 0.400, 0.500));
        // 没有独立的整词 → 走像素/标定落点；这里给 null 帧 + 页签名 → 标定落点（仍在左 1/3）
        var hit = ResultCardProbe.Locate(ocr, null, attempt: 1);
        Assert.NotNull(hit);
        Assert.Equal("白卡标定落点", hit!.Value.Where);
        Assert.InRange(hit.Value.X, 0.05, 0.25);
    }

    // ---------------- ② 像素探针 ----------------

    [Fact]
    public void FindCard_左三分之一的白卡必须认出来()
    {
        // 版式 B：卡 x 0.026~0.242 / y 0.019~0.881（用户 2026-11-25 实机截图像素量取）
        var frame = Frame(1280, 720, (0.026, 0.019, 0.242, 0.881));
        var card = ResultCardProbe.FindCard(frame);
        Assert.NotNull(card);
        _out.WriteLine($"card = {card!.Value}");
        Assert.InRange(card.Value.X0, 0.015, 0.05);
        Assert.InRange(card.Value.X1, 0.22, 0.27);
        Assert.InRange(card.Value.Y0, 0.005, 0.05);
        Assert.InRange(card.Value.Y1, 0.85, 0.92);
    }

    [Fact]
    public void FindCard_版式A短卡同样认得出()
    {
        // 版式 A（fulltime_user_1.png 量取）：卡 x 0.028~0.239 / y 0.031~0.444，下面还有一块深色「建议」卡
        var frame = Frame(1280, 720, (0.028, 0.031, 0.239, 0.444));
        var card = ResultCardProbe.FindCard(frame);
        Assert.NotNull(card);
        Assert.InRange(card!.Value.Y0, 0.01, 0.06);
        Assert.InRange(card.Value.Y1, 0.40, 0.48);
    }

    [Fact]
    public void Locate_整帧漏读按钮字_像素探针点卡内箭头()
    {
        // OCR 什么都没读到（网页层字号/渲染变化时常见）：靠白卡探针，落点必须在卡内箭头位置
        var frame = Frame(1280, 720, (0.026, 0.019, 0.242, 0.881));
        var hit = ResultCardProbe.Locate(Ocr(), frame, attempt: 150);
        Assert.NotNull(hit);
        _out.WriteLine($"hit = ({hit!.Value.X:F3},{hit.Value.Y:F3}) [{hit.Value.Where}]");
        Assert.Equal("白卡像素探针", hit.Value.Where);
        Assert.InRange(hit.Value.X, 0.10, 0.16);   // 卡中心 x ≈ 0.134
        Assert.InRange(hit.Value.Y, 0.36, 0.48);   // 卡高 46% 处 ≈ 0.415
        // 绝不能回到旧版那个「国服右下兜底坐标」（那一页右下角是 RS 翻页指示）
        Assert.False(hit.Value.X > 0.8 && hit.Value.Y > 0.8);
    }

    [Fact]
    public void FindCard_白底文档页不认()
    {
        // 我的联赛说明页那种整幅白底文档：右半屏同样雪白 → 不是结算页，绝不当白卡
        var frame = FrameOn(1280, 720, dark: false);
        Assert.Null(ResultCardProbe.FindCard(frame));
    }

    [Fact]
    public void FindCard_白横条与中下部白块都不认()
    {
        var band = Frame(1280, 720, (0.02, 0.02, 0.28, 0.10));   // 顶部白横条：太矮
        Assert.Null(ResultCardProbe.FindCard(band));
        var low = Frame(1280, 720, (0.02, 0.50, 0.24, 0.92));    // 中下部白块：卡顶太低
        Assert.Null(ResultCardProbe.FindCard(low));
    }

    // ---------------- ③ 标定落点轮换 ----------------

    [Fact]
    public void 标定落点_三档轮换且都在左侧白卡范围内()
    {
        var seen = new HashSet<(double, double)>();
        for (int a = 0; a < ResultCardProbe.FallbackPoints.Length; a++)
        {
            var p = ResultCardProbe.FallbackPoints[Math.Abs(a) % ResultCardProbe.FallbackPoints.Length];
            Assert.InRange(p.X, 0.05, 0.25);   // 卡横范围实机 0.026~0.242
            Assert.InRange(p.Y, 0.15, 0.60);   // 两版实机箭头/文字 y 0.215~0.500
            seen.Add(p);
        }
        Assert.Equal(ResultCardProbe.FallbackPoints.Length, seen.Count);
    }

    [Fact]
    public void Locate_既无字也无帧_不抢非结算页()
    {
        // 起始页/难度页这类没有卡面签名的页面：白卡探针不许给落点（调用方自己有点位）
        var ocr = Ocr(W("前往比赛", 0.14, 0.25), W("设置", 0.20, 0.93));
        Assert.Null(ResultCardProbe.Locate(ocr, null, attempt: 5));
    }

    // ---------------- ④ CnAdvance 的档位顺序 ----------------

    [Fact]
    public void LocateClickPoint_按钮锚点优先()
    {
        var ocr = Ocr(W("RT 下一步", 0.900, 0.925), W("球员评价", 0.616, 0.290));
        var p = CnAdvance.LocateClickPoint(ocr, null, null, attempt: 3, frame: null);
        Assert.Equal("按钮位置", p.Where);
        Assert.True(p.X > 0.8 && p.Y > 0.9);
    }

    [Fact]
    public void LocateClickPoint_页面兜底坐标优先于白卡()
    {
        // 起始页/难度页把版面标定的点位传进来时，白卡探针不许抢（那几页没有白卡，点位更准）
        var frame = Frame(1280, 720, (0.026, 0.019, 0.242, 0.881));
        var p = CnAdvance.LocateClickPoint(Ocr(), null, (0.850, 0.920), attempt: 3, frame: frame);
        Assert.Equal("兜底坐标", p.Where);
        Assert.Equal(0.850, p.X, 3);
    }

    [Fact]
    public void LocateClickPoint_赛后球员评价页_落点在左侧白卡而不是右下角()
    {
        // 本次实机事故的回归点：这一页调用方给的是 null 兜底，旧实现直接掉到 (0.870,0.920)
        var ocr = Ocr(W("全场", 0.615, 0.072), W("球员评价", 0.616, 0.290), W("下一步", 0.132, 0.500));
        var p = CnAdvance.LocateClickPoint(ocr, null, null, attempt: 150, frame: null);
        _out.WriteLine($"p = ({p.X:F3},{p.Y:F3}) [{p.Where}]");
        Assert.NotEqual("国服右下兜底坐标", p.Where);
        Assert.InRange(p.X, 0.05, 0.25);
        Assert.InRange(p.Y, 0.40, 0.60);
    }

    [Fact]
    public void LocateClickPoint_真没线索时仍是右下兜底()
    {
        var p = CnAdvance.LocateClickPoint(Ocr(), Array.Empty<YoloDetection>(), null, attempt: 9, frame: null);
        Assert.Equal("国服右下兜底坐标", p.Where);
        Assert.Equal(CnAdvance.DefaultX, p.X, 3);
        Assert.Equal(CnAdvance.DefaultY, p.Y, 3);
    }

    [Fact]
    public void LocateClickPoint_Yolo右下的confirm框仍然管用()
    {
        var dets = new[] { Box("btn_confirm", 0.93, 0.88, 0.90) };
        var p = CnAdvance.LocateClickPoint(Ocr(), dets, null, attempt: 3, frame: null);
        Assert.Equal("按钮位置", p.Where);
        Assert.True(p.X > 0.8 && p.Y > 0.8);
    }

    // ---------------- ⑤ 后台点不动时的前台真实点击（最后一档） ----------------

    [Fact]
    public void UseForegroundClick_前七轮只走后台_第八轮起每六轮一次()
    {
        for (int a = 1; a < CnAdvance.ForegroundClickFromAttempt; a++)
            Assert.False(CnAdvance.UseForegroundClick(a), $"第 {a} 轮不该抢前台");
        Assert.True(CnAdvance.UseForegroundClick(CnAdvance.ForegroundClickFromAttempt));
        Assert.False(CnAdvance.UseForegroundClick(CnAdvance.ForegroundClickFromAttempt + 1));
        Assert.False(CnAdvance.UseForegroundClick(CnAdvance.ForegroundClickFromAttempt + CnAdvance.ForegroundClickEvery - 1));
        Assert.True(CnAdvance.UseForegroundClick(CnAdvance.ForegroundClickFromAttempt + CnAdvance.ForegroundClickEvery));
        // 不能每轮都抢前台（否则用户在电脑前时窗口会一直被拉走）
        Assert.True(CnAdvance.ForegroundClickEvery >= 3);
    }

    // ---------------- ⑥ 两版实机帧（真 OCR） ----------------

    private static string Dir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData");

    private static FrameData Load(string file)
    {
        using var mat = Cv2.ImRead(file, ImreadModes.Color);
        Assert.False(mat.Empty(), $"图片加载失败: {file}");
        using var bgra = new Mat();
        Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
        var data = new byte[(int)(bgra.Total() * bgra.ElemSize())];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, data.Length);
        return new FrameData(data, mat.Width, mat.Height, 0);
    }

    [Theory]
    [InlineData("fulltime_user_1.png", 0.031, 0.444)]                                  // 版式 A
    [InlineData("cnweekly/cn_fulltime_ratings_20261125.png", 0.019, 0.881)]             // 版式 B
    public async Task 实机帧_球员评价页_落点必须在左侧白卡里(string file, double cardTop, double cardBottom)
    {
        var path = Path.Combine(Dir, file.Replace('/', Path.DirectorySeparatorChar));
        var frame = Load(path);
        using var ocrEngine = new OcrRecognizer();
        var ocr = await ocrEngine.RecognizeAsync(frame);
        // 生产链路里 YOLO 也在场：这一页右下角有「RS ● ● ●」翻页指示，若被误检成 btn_confirm，
        // 落点就会被拽回右下去——所以这里必须带真检测框跑，而不是传 null 蒙过去
        using var det = new YoloDetector(
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx"),
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt"),
            new OnnxSessionFactory("CPU"));
        var dets = det.Detect(frame);

        _out.WriteLine($"---- OCR ({ocr.Words.Count} 词) ----");
        foreach (var w in ocr.Words) _out.WriteLine($"  W \"{w.Text}\" @({w.CenterX:F3},{w.CenterY:F3})");
        _out.WriteLine($"---- DETS ({dets.Count}) ----");
        foreach (var d in dets) _out.WriteLine($"  {d.ClassName} {d.Score:F2} @({d.CenterX:F3},{d.CenterY:F3})");

        var p = CnAdvance.LocateClickPoint(ocr, dets, null, attempt: 150, frame: frame);
        _out.WriteLine($"落点 = ({p.X:F3},{p.Y:F3}) [{p.Where}]，卡 y {cardTop:F3}~{cardBottom:F3}");

        // 卡横范围：两版实机 0.026~0.242；落点必须落在卡内
        Assert.InRange(p.X, 0.05, 0.25);
        // 卡内纵向：允许 OCR 字中心 / 卡高 46% 箭头 / 标定落点三种，全部落在这条带里
        Assert.InRange(p.Y, cardTop + 0.05, cardBottom - 0.10);
        // 绝不再是旧版右下角（那一页右下角是 RS 翻页指示）
        Assert.False(p.X > 0.8 && p.Y > 0.8);
    }
}
