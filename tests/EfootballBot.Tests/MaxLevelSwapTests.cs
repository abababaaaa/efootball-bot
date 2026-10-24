using EfootballBot.Core.Bot;
using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;
using OpenCvSharp;
using Xunit;

namespace EfootballBot.Tests;

// 满级自动换人：纯判定（界面分类/等级读取/排序箭头像素）+ 状态机全流程脚本（录制手柄）。
// 实机几何依据用户 2026-10-24 截图（1280×720）：详情页数据栏 表头 y≈0.376 / 值 y≈0.425，
// 等级列 x≈0.527、能力点列 x≈0.602；排序弹窗右上箭头 x≈0.937~0.969 / y≈0.080~0.149。
public class MaxLevelSwapTests
{
    /// <summary>构造以中心点定位的 OCR 词（宽 0.15 / 高 0.03，与实机词块量级一致）。</summary>
    private static OcrWord W(string text, double cx, double cy)
        => new(text, cx - 0.075, cy - 0.015, 0.15, 0.03);

    private static OcrResult Ocr(params OcrWord[] words) => new(words, 1, 1);

    private static FrameData BlackFrame(int w = 640, int h = 360)
        => new(new byte[w * h * 4], w, h, 0);

    private static readonly Action<string, EfootballBot.Core.Bot.LogLevel> SilentLog = (_, _) => { };
    private static readonly CancellationToken CT = CancellationToken.None;

    // ---------------- 界面分类 ----------------

    private static OcrWord[] LineupOcr() => new[]
    {
        W("完成", 0.90, 0.07), W("整体实力", 0.88, 0.18),
        W("战术", 0.10, 0.28), W("球队", 0.10, 0.40),
        W("替补球员", 0.10, 0.48), W("后备球员", 0.10, 0.55), W("自动配置球员", 0.10, 0.63),
        W("球员详细数据", 0.60, 0.92),
    };

    private static OcrWord[] DetailOcr(string lv, string lvMax, string pts) => new[]
    {
        W("球员动作", 0.42, 0.93), W("完成", 0.06, 0.93),
        W("身高", 0.06, 0.376), W("体重", 0.15, 0.376), W("年龄", 0.25, 0.376),
        W("实时更新", 0.33, 0.376), W("惯用脚", 0.42, 0.376),
        W("等级", 0.527, 0.376), W("能力点", 0.602, 0.376),
        W(lv, 0.520, 0.425), W("/" + lvMax, 0.548, 0.427), W(pts, 0.603, 0.425),
    };

    private static OcrResult PopupOcr() => Ocr(
        W("球员动作", 0.70, 0.10), W("等级训练完成", 0.70, 0.19), W("能力点分配", 0.70, 0.27),
        W("球员融合", 0.70, 0.48), W("锁定", 0.70, 0.55), W("取消", 0.70, 0.93));

    private static OcrResult AllocOcr(string cur) => Ocr(
        W("能力点分配", 0.16, 0.093), W("能力点", 0.28, 0.215),
        W(cur, 0.255, 0.272), W("/72", 0.30, 0.275), W("推荐分配", 0.62, 0.92), W("关闭", 0.90, 0.07));

    private static OcrResult HomeOcr() => Ocr(
        W("前往比赛", 0.17, 0.24), W("赛程表", 0.20, 0.50), W("兑换积分", 0.50, 0.40),
        W("返回", 0.13, 0.94), W("比赛计划", 0.78, 0.94));

    private static OcrResult BenchOcr() => Ocr(
        W("后备球员", 0.15, 0.07), W("Gedeon Verlaine", 0.18, 0.24), W("剩余球员数:828", 0.88, 0.86));

    private static OcrResult SortOcr() => Ocr(
        W("排序", 0.57, 0.09), W("球员姓名", 0.72, 0.19), W("当前等级", 0.72, 0.50),
        W("取消", 0.57, 0.93), W("顺序", 0.74, 0.93));

    private static OcrResult FilterOcr() => Ocr(
        W("筛选", 0.24, 0.09), W("俱乐部球队", 0.40, 0.19), W("卡片类型", 0.40, 0.43),
        W("比赛风格", 0.40, 0.67), W("完成", 0.50, 0.93), W("取消", 0.90, 0.07));

    private static OcrResult CardTypeOcr() => Ocr(
        W("卡片类型", 0.50, 0.09), W("普通", 0.50, 0.24), W("状态火热", 0.50, 0.32),
        W("精选", 0.50, 0.39), W("不可用的球员", 0.50, 0.81), W("返回", 0.42, 0.93));

    [Fact]
    public void Classify_DistinctScreens()
    {
        Assert.Equal(SwapScreen.Lineup, MaxLevelSwapBrain.Classify(Ocr(LineupOcr())));
        Assert.Equal(SwapScreen.Detail, MaxLevelSwapBrain.Classify(Ocr(DetailOcr("37", "37", "0"))));
        Assert.Equal(SwapScreen.ActionPopup, MaxLevelSwapBrain.Classify(PopupOcr()));
        Assert.Equal(SwapScreen.Alloc, MaxLevelSwapBrain.Classify(AllocOcr("72")));
        Assert.Equal(SwapScreen.Bench, MaxLevelSwapBrain.Classify(BenchOcr()));
        Assert.Equal(SwapScreen.Sort, MaxLevelSwapBrain.Classify(SortOcr()));
        Assert.Equal(SwapScreen.Filter, MaxLevelSwapBrain.Classify(FilterOcr()));
        Assert.Equal(SwapScreen.CardType, MaxLevelSwapBrain.Classify(CardTypeOcr()));

        // 联赛主页：不得误判成巡检涉及界面（否则巡检收尾会误判卡住）
        Assert.Equal(SwapScreen.None, MaxLevelSwapBrain.Classify(HomeOcr()));
    }

    // ---------------- 等级 / 能力点读取 ----------------

    [Fact]
    public void ReadPlayerStatus_SplitWords_NotMaxed()
    {
        var s = MaxLevelSwapBrain.ReadPlayerStatus(Ocr(DetailOcr("23", "26", "44")));
        Assert.NotNull(s);
        Assert.Equal((23, 26, 44), (s!.Level, s.LevelMax, s.Points));
        Assert.False(s.NeedsSwap);
    }

    [Fact]
    public void ReadPlayerStatus_MaxedWithPoints_NeedsSwap()
    {
        var s = MaxLevelSwapBrain.ReadPlayerStatus(Ocr(DetailOcr("40", "40", "12")));
        Assert.NotNull(s);
        Assert.True(s!.NeedsSwap);
    }

    [Fact]
    public void ReadPlayerStatus_MaxedPointsZero_NoSwap()
    {
        var s = MaxLevelSwapBrain.ReadPlayerStatus(Ocr(DetailOcr("37", "37", "0")));
        Assert.NotNull(s);
        Assert.False(s!.NeedsSwap);
    }

    [Fact]
    public void ReadPlayerStatus_AdjacentColumns_NoCrosstalk()
    {
        // 相邻列横向只差 ~0.05：「/26」离能力点表头 0.054，固定容差会把它并进能力点值
        // （2026-10-24 测试暴露）——值词必须按「离哪个表头更近」归属
        var minimal = new[]
        {
            W("等级", 0.527, 0.376), W("能力点", 0.602, 0.376),
            W("23", 0.520, 0.425), W("/26", 0.548, 0.427), W("44", 0.603, 0.425),
        };
        var s = MaxLevelSwapBrain.ReadPlayerStatus(Ocr(minimal));
        Assert.NotNull(s);
        Assert.Equal((23, 26, 44), (s!.Level, s.LevelMax, s.Points));

        var full = DetailOcr("23", "26", "44");
        var s2 = MaxLevelSwapBrain.ReadPlayerStatus(Ocr(full));
        Assert.NotNull(s2);
        Assert.Equal((23, 26, 44), (s2!.Level, s2.LevelMax, s2.Points));
    }

    [Fact]
    public void ReadPlayerStatus_MergedSlashWord()
    {
        // OCR 把 "37/37" 读成一个词（值列只有一条）仍可解析
        var s = MaxLevelSwapBrain.ReadPlayerStatus(Ocr(
            W("球员动作", 0.42, 0.93), W("等级", 0.527, 0.376), W("能力点", 0.602, 0.376),
            W("37/37", 0.527, 0.425), W("0", 0.603, 0.425)));
        Assert.NotNull(s);
        Assert.Equal((37, 37, 0), (s!.Level, s.LevelMax, s.Points));
    }

    [Fact]
    public void ReadPlayerStatus_CropGeometry()
    {
        // 裁切放大帧：数据栏相对几何放大（表头 y≈0.31、值 y≈0.58，容差带调大后仍可读）
        var ocr = Ocr(
            W("等级", 0.31, 0.31), W("能力点", 0.58, 0.31),
            W("23", 0.30, 0.58), W("/26", 0.335, 0.585), W("44", 0.585, 0.58));
        var s = MaxLevelSwapBrain.ReadPlayerStatus(ocr, headYMin: 0.15, valueMaxDy: 0.35);
        Assert.NotNull(s);
        Assert.Equal((23, 26, 44), (s!.Level, s.LevelMax, s.Points));
    }

    [Fact]
    public void ReadRemainingPoints_AllocPage()
    {
        // 分配页左栏：表头 y≈0.215（页首大字标题「能力点分配」y≈0.093 必须被 y 带排除）
        Assert.Equal(72, MaxLevelSwapBrain.ReadRemainingPoints(AllocOcr("72")));
        Assert.Equal(0, MaxLevelSwapBrain.ReadRemainingPoints(AllocOcr("0")));
    }

    [Theory]
    [InlineData("27/29", 27, 29)]
    [InlineData("37/37", 37, 37)]
    [InlineData("537", 5, 37)]    // 斜杠丢失：3 位按 cur ≤ max 拆（不能拆成 53/7）
    [InlineData("2729", 27, 29)]  // 斜杠丢失：4 位等分
    public void ParseLevelPair_HandlesLostSlash(string raw, int cur, int max)
    {
        Assert.Equal((cur, max), MaxLevelSwapBrain.ParseLevelPair(raw));
    }

    [Fact]
    public async Task MaxedZeroPoints_SkipsAlloc_GoesStraightToSwap()
    {
        // 满级且能力点 = 0（用户 2026-10-24 修正）：不按 X 分配，B 退出详情后直接 A 选中走换人
        var c = new MaxLevelSwapController();
        var pad = new FakePad();
        var reader = new ScriptReader();
        int maxedCalls = 0;
        reader.Enqueue(new PlayerStatus(40, 40, 0));
        var lineup = Ocr(LineupOcr());
        Action onMaxed = () => maxedCalls++;

        await c.StartCore(pad, SilentLog);
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, onMaxed, null, CT);  // Opening → Inspect
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, onMaxed, null, CT);  // Inspect：Y
        await c.StepCore(Ocr(DetailOcr("40", "40", "0")), BlackFrame(), pad, reader.Read, SilentLog, onMaxed, null, CT); // 满级 0 点：B
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, onMaxed, null, CT);  // A 选中
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, onMaxed, null, CT);  // 侧栏 → 后备球员

        Assert.Equal(new[] { "Y", "Y", "B", "A", "Leftx8", "Upx5", "Downx3", "A" }, pad.Log);
        Assert.Equal(1, maxedCalls);
    }

    [Fact]
    public async Task Alloc_PressesYOnceThenBack_NoInfiniteRetry()
    {
        // 分配页只按一次 Y 就 B 返回（用户 2026-10-24 实机：OCR 丢斜杠曾致无限按 Y）
        var c = new MaxLevelSwapController();
        var pad = new FakePad();
        var reader = new ScriptReader();
        reader.Enqueue(new PlayerStatus(40, 40, 12));
        reader.Enqueue(new PlayerStatus(40, 40, 0));
        var lineup = Ocr(LineupOcr());

        await c.StartCore(pad, SilentLog);
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        await c.StepCore(Ocr(DetailOcr("40", "40", "12")), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // X
        await c.StepCore(PopupOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);  // ↓+A
        // 分配页连续出现多轮（无论读出什么数字）也只按一次 Y、然后 B
        await c.StepCore(AllocOcr("72"), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // Y
        await c.StepCore(AllocOcr("72"), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // B（已分配完成）
        var tail = pad.Log.Skip(3).ToList();
        Assert.Equal(new[] { "Down", "A", "Y", "B" }, tail);
    }

    // ---------------- 排序箭头像素判定 ----------------

    private static FrameData ArrowFrame(bool up)
    {
        int w = 640, h = 360;
        var bgra = new byte[w * h * 4];
        void Lit(int x, int y)
        {
            if (x < 0 || x >= w || y < 0 || y >= h) return;
            int i = (y * w + x) * 4;
            bgra[i] = bgra[i + 1] = bgra[i + 2] = 255;
        }
        // 探针区域：x 588~627（0.92~0.98）；实机箭头为「菱形头 + 细杆」emoji 形态
        // （2026-10-24 实机帧 dump：最宽 22px 的菱形 + 6px 杆），合成帧按同款画
        int cx = 607;
        int[] diamond = { 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 22, 22, 22 };
        int y = 24;
        if (up)
        {
            foreach (var hw in diamond) { for (int x = cx - hw / 2; x <= cx + hw / 2; x++) Lit(x, y); y++; }
            for (; y <= 51; y++) { for (int x = cx - 3; x <= cx + 3; x++) Lit(x, y); }   // 杆在下半
        }
        else
        {
            for (; y <= 37; y++) { for (int x = cx - 3; x <= cx + 3; x++) Lit(x, y); }   // 杆在上半
            for (int k = diamond.Length - 1; k >= 0; k--)
            {
                for (int x = cx - diamond[k] / 2; x <= cx + diamond[k] / 2; x++) Lit(x, y);
                y++;
            }
        }
        return new FrameData(bgra, w, h, 0);
    }

    [Fact]
    public void SortArrow_UpDetected() => Assert.True(MaxLevelSwapBrain.IsSortArrowUp(ArrowFrame(up: true)));

    [Fact]
    public void SortArrow_DownDetected() => Assert.False(MaxLevelSwapBrain.IsSortArrowUp(ArrowFrame(up: false)));

    [Fact]
    public void SortArrow_BlackFrame_Unknown() => Assert.Null(MaxLevelSwapBrain.IsSortArrowUp(BlackFrame()));

    // ---------------- 实机帧校准（用户 2026-10-24 排序弹窗截图，1280×720） ----------------

    private static string SwapDir => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestData", "swap");

    private static OcrRecognizer RealOcr() => new();

    private static FrameData LoadFrame(string file)
    {
        using var mat = Cv2.ImRead(Path.Combine(SwapDir, file), ImreadModes.Color);
        Assert.False(mat.Empty(), $"图片加载失败: {file}");
        using var bgra = new Mat();
        Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
        int byteCount = (int)(bgra.Total() * bgra.ElemSize());
        var data = new byte[byteCount];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, byteCount);
        return new FrameData(data, mat.Width, mat.Height, 0);
    }

    [Fact]
    public void RealSortPopup_ArrowDirection()
    {
        Assert.False(MaxLevelSwapBrain.IsSortArrowUp(LoadFrame("sort_arrow_down.png")));
        Assert.True(MaxLevelSwapBrain.IsSortArrowUp(LoadFrame("sort_arrow_up.png")));
    }

    [Fact]
    public void RealSortPopup_OcrClassifySort()
    {
        using var ocr = new OcrRecognizer();
        foreach (var f in new[] { "sort_arrow_down.png", "sort_arrow_up.png" })
            Assert.Equal(SwapScreen.Sort, MaxLevelSwapBrain.Classify(TestOcr.Run(ocr, LoadFrame(f))));
    }

    [Fact]
    public void RealSortPopup_CurrentLevelChecked_Probe()
    {
        // p1 实机帧（2026-10-24）：当前等级已勾 + 箭头向上 → 已设置探针 true、方向 true
        var ocr = TestOcr.Run(RealOcr(), LoadFrame("sort_checked_up.png"));
        var frame = LoadFrame("sort_checked_up.png");
        Assert.True(MaxLevelSwapBrain.IsCurrentLevelChecked(ocr, frame));
        Assert.True(MaxLevelSwapBrain.IsSortArrowUp(frame));

        // 旧帧：勾在「整体评价」行 → 当前等级未勾
        var ocr2 = TestOcr.Run(RealOcr(), LoadFrame("sort_arrow_up.png"));
        Assert.False(MaxLevelSwapBrain.IsCurrentLevelChecked(ocr2, LoadFrame("sort_arrow_up.png")));
    }

    [Fact]
    public void RealFilterPage_CardTypeSet_Probe()
    {
        // p2 实机帧：卡片类型右侧值 = 精选/高光/… → 已设置
        var ocr = TestOcr.Run(RealOcr(), LoadFrame("filter_cardtype_set.png"));
        Assert.True(MaxLevelSwapBrain.IsCardTypeFilterSet(ocr));

        // 合成帧：值 = 无 / 值词缺失 → 未设置
        Assert.False(MaxLevelSwapBrain.IsCardTypeFilterSet(FilterOcr()));
        var none = Ocr(W("筛选", 0.24, 0.09), W("俱乐部球队", 0.40, 0.19), W("卡片类型", 0.40, 0.43),
            W("无", 0.72, 0.43), W("比赛风格", 0.40, 0.67), W("完成", 0.50, 0.93));
        Assert.False(MaxLevelSwapBrain.IsCardTypeFilterSet(none));
    }

    [Fact]
    public async Task SortAlreadyChecked_BReturnsImmediately()
    {
        // 排序弹窗已勾「当前等级」+ 箭头为上：不导航不按 A，直接 B 返回继续筛选
        var c = new MaxLevelSwapController();
        var pad = new FakePad();
        var reader = new ScriptReader();
        var lineup = Ocr(LineupOcr());
        reader.Enqueue(new PlayerStatus(40, 40, 0)); // 满级 0 点 → 直接换人路径
        var checkedSortOcr = TestOcr.Run(RealOcr(), LoadFrame("sort_checked_up.png"));
        var checkedFrame = LoadFrame("sort_checked_up.png");

        await c.StartCore(pad, SilentLog);
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        await c.StepCore(Ocr(DetailOcr("40", "40", "0")), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);  // A 选中
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);  // 侧栏
        await c.StepCore(BenchOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // ←+A 开排序

        int before = pad.Log.Count;
        await c.StepCore(checkedSortOcr, checkedFrame, pad, reader.Read, SilentLog, null, null, CT);
        Assert.Equal(new[] { "B" }, pad.Log.Skip(before).ToArray());
    }

    [Fact]
    public async Task FilterAlreadySet_CompletesDirectly()
    {
        // 筛选页卡片类型已设置：归顶 ↓×9 直达完成，不进卡牌子页重复勾选
        var c = new MaxLevelSwapController();
        var pad = new FakePad();
        var reader = new ScriptReader();
        var setFilterOcr = TestOcr.Run(RealOcr(), LoadFrame("filter_cardtype_set.png"));

        // 手动把内部状态推到 FilterWait：借 FullFlow 前置太长，直接从排序阶段复用驱动
        var lineup = Ocr(LineupOcr());
        reader.Enqueue(new PlayerStatus(40, 40, 0));
        await c.StartCore(pad, SilentLog);
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        await c.StepCore(Ocr(DetailOcr("40", "40", "0")), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        await c.StepCore(BenchOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        // 排序弹窗已正确（p1 帧）→ B；回列表 ←↓A 开筛选
        await c.StepCore(TestOcr.Run(RealOcr(), LoadFrame("sort_checked_up.png")), LoadFrame("sort_checked_up.png"),
            pad, reader.Read, SilentLog, null, null, CT);
        await c.StepCore(BenchOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);

        int before = pad.Log.Count;
        await c.StepCore(setFilterOcr, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        Assert.Equal(new[] { "Upx9", "Downx9", "A" }, pad.Log.Skip(before).ToArray());
    }

    // ---------------- 状态机全流程（录制手柄） ----------------

    private sealed class FakePad : ISwapPad
    {
        public readonly List<string> Log = new();
        public Task Y() { Log.Add("Y"); return Task.CompletedTask; }
        public Task X() { Log.Add("X"); return Task.CompletedTask; }
        public Task Confirm() { Log.Add("A"); return Task.CompletedTask; }
        public Task Back() { Log.Add("B"); return Task.CompletedTask; }
        public Task Dpad(PadDir d) { Log.Add(d.ToString()); return Task.CompletedTask; }
        public Task DpadRepeat(PadDir d, int n) { Log.Add($"{d}x{n}"); return Task.CompletedTask; }
    }

    private sealed class ScriptReader
    {
        private readonly Queue<PlayerStatus> _q = new();
        public void Enqueue(PlayerStatus s) => _q.Enqueue(s);
        public Task<PlayerStatus?> Read(OcrResult ocr, FrameData f)
            => Task.FromResult<PlayerStatus?>(_q.Count > 0 ? _q.Dequeue() : new PlayerStatus(20, 30, 5));
    }

    /// <summary>驱动到「后备球员页、排序弹窗即将打开」：门将满级 → 分配 → 侧栏 → 后备列表 → ←+A。</summary>
    private static async Task<FakePad> DriveToSortAsync(MaxLevelSwapController c, ScriptReader reader)
    {
        var pad = new FakePad();
        var lineup = Ocr(LineupOcr());
        reader.Enqueue(new PlayerStatus(40, 40, 12)); // 详情：满级 → 换人
        reader.Enqueue(new PlayerStatus(40, 40, 0));  // 分配后详情核验

        await c.StartCore(pad, SilentLog);
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);          // Opening → Inspect
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);          // Inspect pos0：Y
        await c.StepCore(Ocr(DetailOcr("40", "40", "12")), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // X
        await c.StepCore(PopupOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);      // ↓+A
        await c.StepCore(AllocOcr("72"), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);  // Y 推荐分配
        await c.StepCore(AllocOcr("0"), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);   // B 关闭
        await c.StepCore(Ocr(DetailOcr("40", "40", "0")), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // B
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);          // A 选中
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);          // 侧栏 → 后备球员
        await c.StepCore(BenchOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);      // ←+A 开排序
        return pad;
    }

    [Fact]
    public async Task SortArrowDown_PressesYToFlip()
    {
        var c = new MaxLevelSwapController();
        var pad = await DriveToSortAsync(c, new ScriptReader());

        int before = pad.Log.Count;
        await c.StepCore(SortOcr(), ArrowFrame(up: false), pad,
            new ScriptReader().Read, SilentLog, null, null, CT);
        Assert.Equal(new[] { "Upx6", "Downx5", "Y", "A" }, pad.Log.Skip(before).ToArray());
    }

    [Fact]
    public async Task SortArrowUp_NoFlip()
    {
        var c = new MaxLevelSwapController();
        var pad = await DriveToSortAsync(c, new ScriptReader());

        int before = pad.Log.Count;
        await c.StepCore(SortOcr(), ArrowFrame(up: true), pad,
            new ScriptReader().Read, SilentLog, null, null, CT);
        Assert.Equal(new[] { "Upx6", "Downx5", "A" }, pad.Log.Skip(before).ToArray());
    }

    [Fact]
    public async Task FullFlow_SwapsMaxedPlayer_AndFinishes()
    {
        var c = new MaxLevelSwapController();
        var reader = new ScriptReader();
        var pad = await DriveToSortAsync(c, reader);

        var lineup = Ocr(LineupOcr());
        var detailNot = Ocr(DetailOcr("23", "26", "44"));
        var upArrow = ArrowFrame(up: true);
        for (int k = 0; k < 10; k++) reader.Enqueue(new PlayerStatus(23, 26, 44));

        // 排序 → 筛选 → 卡片类型 → 应用 → 交换 → 退出 → 重进 → 继续巡检
        await c.StepCore(SortOcr(), upArrow, pad, reader.Read, SilentLog, null, null, CT);        // ↑×6 ↓×5 A
        await c.StepCore(BenchOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);  // ← ↓ A 开筛选
        await c.StepCore(FilterOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // ↑×9 ↓×4 A
        await c.StepCore(CardTypeOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // 勾选 + B
        await c.StepCore(FilterOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // ↓×4 ↓ A 应用
        await c.StepCore(BenchOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);  // A 交换
        await c.StepCore(BenchOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);  // B 回站位页
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);      // B 完成
        await c.StepCore(HomeOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);   // Y 重进
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);      // 盲走回换人位（swapPos=0 无移动）

        // 位 1~10 巡检（非满级 B 退出）；路径 ↑←↑→→→→→↓←
        PadDir[] moves = MaxLevelSwapController.CursorMoves;
        for (int p = 1; p <= 10; p++)
        {
            await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);      // Inspect：move + Y
            await c.StepCore(detailNot, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);   // DetailRead：B
            await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);      // InspectAdvance：推进
        }

        // 收尾：Done（B）→ 主页 → Reset 交还
        int? wait = await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        Assert.NotNull(wait);
        wait = await c.StepCore(HomeOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        Assert.Null(wait);
        Assert.True(c.Idle);

        // ---- 断言关键按键序列 ----
        var log = pad.Log.ToArray();
        int i = 0;
        Assert.Equal("Y", log[i++]);                                // 进比赛计划
        Assert.Equal("Y", log[i++]);                                // 门将巡检（无移动）
        Assert.Equal("X", log[i++]);                                // 球员动作
        Assert.Equal("Down", log[i++]); Assert.Equal("A", log[i++]); // ↓ 能力点分配 + A
        Assert.Equal("Y", log[i++]);                                // 推荐分配
        Assert.Equal("B", log[i++]);                                // 关闭分配页
        Assert.Equal("B", log[i++]);                                // 退出详情
        Assert.Equal("A", log[i++]);                                // 选中球员
        Assert.Equal(new[] { "Leftx8", "Upx5", "Downx3", "A" }, log[i..(i += 4)]); // 侧栏 → 后备球员
        Assert.Equal(new[] { "Left", "A" }, log[i..(i += 2)]);      // 开排序
        Assert.Equal(new[] { "Upx6", "Downx5", "A" }, log[i..(i += 3)]); // 当前等级（箭头朝上不翻）
        Assert.Equal(new[] { "Left", "Down", "A" }, log[i..(i += 3)]);   // 开筛选
        Assert.Equal(new[] { "Upx9", "Downx4", "A" }, log[i..(i += 3)]); // → 卡片类型
        Assert.Equal(new[] { "Upx9", "Downx2" }, log[i..(i += 2)]);
        for (int k = 0; k < 6; k++)
            Assert.Equal(new[] { "A", "Down" }, log[i..(i += 2)]);  // 6 张高阶卡逐个勾选
        Assert.Equal("B", log[i++]);                                // 回筛选页
        Assert.Equal(new[] { "Upx9", "Downx9", "A" }, log[i..(i += 3)]); // 归顶 ↓×9 → 完成（应用筛选）
        Assert.Equal("A", log[i++]);                                // 交换球员
        Assert.Equal("B", log[i++]);                                // 回站位页
        Assert.Equal("B", log[i++]);                                // 完成退出计划
        Assert.Equal("Y", log[i++]);                                // 重进（光标回门将）
        // 盲走 0 步（swapPos=0）→ 位 1 巡检：Up + Y
        Assert.Equal("Up", log[i++]); Assert.Equal("Y", log[i++]);
        // 位 1~10：非满级 B 退出 + 各自路径
        for (int p = 1; p <= 10; p++)
        {
            Assert.Equal("B", log[i++]);                            // 非满级退出详情
            if (p < 10)
            {
                Assert.Equal(moves[p].ToString(), log[i++]);        // 移动到下一位
                Assert.Equal("Y", log[i++]);
            }
        }
        Assert.Equal("B", log[i++]);                                // p=10 推进即完成 → B
        Assert.Equal("B", log[i]);                                  // Done 收尾 B（主页识别前）
        Assert.Equal(i + 1, log.Length);                            // 没有多余按键
    }
}
