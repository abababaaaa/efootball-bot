using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Bot.Scenarios.Weekly;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 校准/回归：2026-11-19 用户国际服实机三帧（新周活动「阵容挑战 / Team Effort」）
// ① 活动列表新 UI（卡片按钮改成「继续」，第 3 张卡绿勾）
// ② 入场「球员卡限制 / OK」弹窗（盖在阵容挑战主页上）
// ③ 阵容挑战主页（前往比赛 / 赛程 / 下一场比赛 / 赛事等级 / 奖励 挑战1..3）
// Dump 用例打印真实 OCR + YOLO 词表（规则的数据依据）；Classify 用例锁定分类不回归。
public class TeamEffortRealFrameTests
{
    private readonly ITestOutputHelper _out;
    public TeamEffortRealFrameTests(ITestOutputHelper output) => _out = output;

    private static string WeeklyDir => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestData", "weekly");

    private static string[] Frames => new[]
    {
        "weekly_teameffort_list.png",
        "weekly_teameffort_cardrestrict.png",
        "weekly_teameffort_home.png",
        // 2026-11-22 用户国际服实机六帧：比赛计划失效修复链
        "weekly_teameffort_cannot_play.png",     // 赛后「无法进行比赛 / OK」弹窗
        "weekly_teameffort_plan_badge.png",      // 主页：比赛计划按钮右上角红色叹号
        "weekly_teameffort_home_clean.png",      // 主页：红点消失（可开赛）
        "weekly_teameffort_plan_editor.png",     // 比赛计划编辑页（焦点在侧边栏「球队」）
        "weekly_teameffort_autoconfig.png",      // 编辑页（焦点「自动配置球员」）
        "weekly_teameffort_autoconfig_done.png", // 自动配置后的阵容
    };

    private static YoloDetector NewDetector() => new(
        Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx"),
        Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt"),
        new OnnxSessionFactory("CPU"));

    [Fact]
    public void Dump_TeamEffort_Frames()
    {
        using var det = NewDetector();
        using var ocr = new OcrRecognizer();

        foreach (var name in Frames)
        {
            var path = Path.Combine(WeeklyDir, name);
            if (!File.Exists(path)) { _out.WriteLine($"==== {name}: missing, skip ===="); continue; }
            var data = TestFrameLoader.LoadPngBgra(path, out int w, out int h);
            var frame = new FrameData(data, w, h, 0);
            var dets = det.Detect(frame);
            var ocrRes = TestOcr.Run(ocr, frame);
            var screen = WeeklyBrain.Classify(dets, ocrRes,
                ScreenProbe.CornerHasContent(frame),
                matchContext: false,
                ScreenProbe.IsMostlyLightPage(frame));
            var ml = MatchBrain.Classify(dets, ocrRes, ScreenProbe.CornerHasContent(frame),
                false, ScreenProbe.IsMostlyLightPage(frame), false);

            _out.WriteLine($"==== {name} -> wk={screen} ml={ml} ====");
            foreach (var d in dets.Where(d => d.Score >= MatchBrain.MinScore))
                _out.WriteLine($"  YOLO {d.ClassName} {d.Score:F2} @({d.CenterX:F2},{d.CenterY:F2})");
            foreach (var l in ocrRes.Lines)
                _out.WriteLine($"  L \"{l.Text}\" @({l.CenterX:F2},{l.CenterY:F2})");
            foreach (var wd in ocrRes.Words)
                _out.WriteLine($"  W \"{wd.Text}\" norm=[{wd.NormText}] @({wd.CenterX:F2},{wd.CenterY:F2}) wh=({wd.W:F3},{wd.H:F3})");
            _out.WriteLine($"  popupButtons={PopupBrain.Describe(PopupBrain.FindButtons(frame, ocrRes))}");
            _out.WriteLine($"  greenCheck slot0={WeeklyBrain.DetectGreenCheck(frame, 0)} slot1={WeeklyBrain.DetectGreenCheck(frame, 1)} slot2={WeeklyBrain.DetectGreenCheck(frame, 2)}");
            _out.WriteLine($"  planBadge={WeeklyBrain.DetectPlanBadge(frame, ocrRes)} " +
                           $"red(0.70~0.80,0.865~0.965)={RegionProbe.CountRedRef(frame, 0.700, 0.800, 0.865, 0.965)}");
            _out.WriteLine($"  sidebarFocus={WeeklyBrain.DetectFocusedSidebarItem(frame, ocrRes) ?? "null"}");
            foreach (var item in WeeklyBrain.SquadSidebarItems)
            {
                var sw = ocrRes.Words
                    .Where(x => x.CenterX < 0.30 && x.NormText.Contains(item))
                    .OrderByDescending(x => x.H).FirstOrDefault();
                if (sw is not null)
                    _out.WriteLine($"    sidebar \"{item}\" @({sw.CenterX:F2},{sw.CenterY:F2}) wh=({sw.W:F3},{sw.H:F3})");
            }
        }
    }

    [Fact]
    public void Classify_TeamEffort_Frames_Asserted()
    {
        using var det = NewDetector();
        using var ocr = new OcrRecognizer();

        var expected = new (string Name, WkScreen Screen)[]
        {
            // 新 UI 活动列表：顶部「对阵AI / 对阵玩家」页签 + 卡片「继续」按钮 → 仍是列表页
            ("weekly_teameffort_list.png", WkScreen.EventsList),
            // 入场弹窗「球员卡限制 / OK」：盖在主页上，必须先于一切主页判定
            ("weekly_teameffort_cardrestrict.png", WkScreen.PlayerCardRestrictPopup),
            // 阵容挑战主页：无积分栏的新版式，旧逻辑恒判 None（共享决策原地干等）
            ("weekly_teameffort_home.png", WkScreen.TeamEffortHome),
            // 2026-11-22 比赛计划失效链：赛后「无法进行比赛」弹窗盖在主页上 → 必须先判弹窗
            ("weekly_teameffort_cannot_play.png", WkScreen.MatchPlanInvalidPopup),
            // 主页（红点在 / 已消失）：同一版式，红点由 DetectPlanBadge 像素探针判，不影响分类
            ("weekly_teameffort_plan_badge.png", WkScreen.TeamEffortHome),
            ("weekly_teameffort_home_clean.png", WkScreen.TeamEffortHome),
            // 比赛计划编辑页与其「自动配置球员」状态：底栏 X/Y 键 + 左列侧边栏
            ("weekly_teameffort_plan_editor.png", WkScreen.SquadLineupEdit),
            ("weekly_teameffort_autoconfig.png", WkScreen.SquadLineupEdit),
            ("weekly_teameffort_autoconfig_done.png", WkScreen.SquadLineupEdit),
        };

        foreach (var (name, want) in expected)
        {
            var path = Path.Combine(WeeklyDir, name);
            if (!File.Exists(path)) continue;
            var data = TestFrameLoader.LoadPngBgra(path, out int w, out int h);
            var frame = new FrameData(data, w, h, 0);
            var dets = det.Detect(frame);
            var ocrRes = TestOcr.Run(ocr, frame);
            var screen = WeeklyBrain.Classify(dets, ocrRes,
                ScreenProbe.CornerHasContent(frame),
                matchContext: false,
                ScreenProbe.IsMostlyLightPage(frame));
            _out.WriteLine($"{name}: {screen} (want {want})");
            Assert.True(screen == want, $"{name}: got {screen}, want {want}");
        }
    }

    [Fact]
    public void TeamEffortList_GreenCheck_OnlyThirdCard()
    {
        // 活动列表新 UI：第 3 张卡（Spanish Clubs）右上角绿勾 = 主要奖励已完成，进入时跳过。
        // 卡片几何（右缘 0.352 / 0.643 / 0.946）在新版式下仍成立，勾探针不得命中前两张。
        var path = Path.Combine(WeeklyDir, "weekly_teameffort_list.png");
        if (!File.Exists(path)) return;
        var data = TestFrameLoader.LoadPngBgra(path, out int w, out int h);
        var frame = new FrameData(data, w, h, 0);
        Assert.False(WeeklyBrain.DetectGreenCheck(frame, 0));
        Assert.False(WeeklyBrain.DetectGreenCheck(frame, 1));
        Assert.True(WeeklyBrain.DetectGreenCheck(frame, 2));
    }

    [Fact]
    public void CardRestrictPopup_SingleOkButton_PressConfirm()
    {
        // 弹窗只有一枚 OK（偏亮的蓝底按钮）：PopupBrain 必须直接按 A，不按任何方向键
        // （多按一次方向键会打到弹窗背后主页的焦点，关掉弹窗后落点不可控）
        var path = Path.Combine(WeeklyDir, "weekly_teameffort_cardrestrict.png");
        if (!File.Exists(path)) return;
        var data = TestFrameLoader.LoadPngBgra(path, out int w, out int h);
        var frame = new FrameData(data, w, h, 0);
        using var ocr = new OcrRecognizer();
        var ocrRes = TestOcr.Run(ocr, frame);
        var buttons = PopupBrain.FindButtons(frame, ocrRes);
        Assert.Single(buttons);
        Assert.Equal("ok", buttons[0].Label, ignoreCase: true);
        Assert.Equal(PopupAction.PressConfirm, PopupBrain.Decide(frame, ocrRes, out _));
    }

    // ---------------- 2026-11-22 比赛计划失效修复链（用户国际服实机六帧） ----------------

    private static (FrameData Frame, OcrResult Ocr) Load(string name, OcrRecognizer ocr)
    {
        var data = TestFrameLoader.LoadPngBgra(Path.Combine(WeeklyDir, name), out int w, out int h);
        var frame = new FrameData(data, w, h, 0);
        return (frame, TestOcr.Run(ocr, frame));
    }

    [Fact]
    public void PlanBadge_RedDot_OnlyOnInvalidPlan()
    {
        // 用户规则：回到活动页先看底栏「Y 比赛计划」右上角有没有红色叹号，有就得先进 Y 修阵容。
        // 红点帧必须命中；红点消失帧（可开赛）与入场弹窗帧必须不命中——误判会在干净主页上
        // 反复进比赛计划，把开赛拖成零场次。
        using var ocr = new OcrRecognizer();
        var (badgeFrame, badgeOcr) = Load("weekly_teameffort_plan_badge.png", ocr);
        Assert.True(WeeklyBrain.DetectPlanBadge(badgeFrame, badgeOcr));

        foreach (var clean in new[] { "weekly_teameffort_home_clean.png", "weekly_teameffort_home.png" })
        {
            var (f, o) = Load(clean, ocr);
            Assert.False(WeeklyBrain.DetectPlanBadge(f, o), $"{clean} 不该被判成有红点");
        }
    }

    [Fact]
    public void PlanEditor_SidebarFocus_WhitePillOnly()
    {
        // 比赛计划编辑页：光标在「自动配置球员」行时该行是白底圆角块（可 A 下一步）；
        // 光标在阵容板上时侧边栏没有白底行（必须先左移归位，不能盲按下移）。
        using var ocr = new OcrRecognizer();
        var (autoFrame, autoOcr) = Load("weekly_teameffort_autoconfig.png", ocr);
        Assert.Equal("自动配置球员", WeeklyBrain.DetectFocusedSidebarItem(autoFrame, autoOcr));

        var (teamFrame, teamOcr) = Load("weekly_teameffort_plan_editor.png", ocr);
        var teamFocus = WeeklyBrain.DetectFocusedSidebarItem(teamFrame, teamOcr);
        Assert.NotEqual("自动配置球员", teamFocus); // 焦点在「球队」或读不出：都先下移/左移
    }

    [Fact]
    public void CannotPlayPopup_SingleOk_PressConfirm()
    {
        // 赛后「无法进行比赛 / OK」：单键弹窗 → 直接 A 继续（用户规则），不按方向键
        using var ocr = new OcrRecognizer();
        var (frame, ocrRes) = Load("weekly_teameffort_cannot_play.png", ocr);
        Assert.True(WeeklyBrain.IsMatchPlanInvalidPopup(ocrRes));
        var buttons = PopupBrain.FindButtons(frame, ocrRes);
        Assert.Single(buttons);
        Assert.Equal("ok", buttons[0].Label, ignoreCase: true);
        Assert.Equal(PopupAction.PressConfirm, PopupBrain.Decide(frame, ocrRes, out _));
    }

    [Fact]
    public void SidebarOrder_LastItemIsAutoConfig()
    {
        // 侧边栏固定顺序（用户实机截图自上而下）：球队 / 替补球员 / 后备球员 / 自动配置球员。
        // 场景层「读到其它项就下移一行」依赖这个顺序。
        Assert.Equal(new[] { "球队", "替补球员", "后备球员", "自动配置球员" },
            WeeklyBrain.SquadSidebarItems);
    }
}
