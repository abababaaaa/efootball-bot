using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 防掉线重登链回归（eFootball 2027 v6.1.0 实机 9 帧）：掉线/重启回落「按确认按钮」标题页
// → 登录 → 弹窗链（登录奖励/Campaign 网格/物品到收件箱/上一场被放弃）→ 误入抽取页 → 付款确认；
// 另有会话掉线第二入口「未登录」页（游戏不重启）。
// 核心约定（用户 2026-10-19）：抽取界面必须 B 退出绝不消费金币；误看到付款确认必须直接按 A
// （光标默认在取消，上方是金币付款键——绝不能套用「有取消先上移」规则）。
// 首次运行看 Dump 输出校对 OCR 词；之后作为回归锁定（各帧分类/动作不得回退）。
public class IntlBootRealFrameTests
{
    private readonly ITestOutputHelper _out;
    public IntlBootRealFrameTests(ITestOutputHelper output) => _out = output;

    private static string Dir => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestData", "intlboot");

    private static readonly Lazy<YoloDetector> Det = new(() => new YoloDetector(
        Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx"),
        Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt"),
        new OnnxSessionFactory("CPU")));

    private static readonly Lazy<OcrRecognizer> OcrRec = new(() => new OcrRecognizer());

    private (MlScreen Screen, OcrResult Ocr) Load(string name, bool loginRecovery)
    {
        var path = Path.Combine(Dir, name);
        Assert.True(File.Exists(path), $"缺少实机帧: {path}");
        var data = TestFrameLoader.LoadPngBgra(path, out int w, out int h);
        var frame = new FrameData(data, w, h, 0);
        var dets = Det.Value.Detect(frame);
        var ocrRes = TestOcr.Run(OcrRec.Value, frame);
        var screen = MyLeagueBrain.Classify(dets, ocrRes,
            MyLeagueScenario.CornerHasContent(frame),
            allowCornerInMatch: false,
            MyLeagueScenario.IsMostlyLightPage(frame),
            loginRecovery);

        _out.WriteLine($"==== {name} (loginRecovery={loginRecovery}) -> {screen} ====");
        foreach (var d in dets.Where(d => d.Score >= MyLeagueBrain.MinScore))
            _out.WriteLine($"  YOLO {d.ClassName} {d.Score:F2} @({d.CenterX:F2},{d.CenterY:F2})");
        foreach (var l in ocrRes.Lines)
            _out.WriteLine($"  L \"{l.Text}\" @({l.CenterX:F2},{l.CenterY:F2})");
        return (screen, ocrRes);
    }

    [Fact]
    public void Dump_IntlBoot_Frames()
    {
        foreach (var f in new[]
        {
            "boot_splash.png", "boot_splash_loading.png", "campaign_grid.png",
            "playing_style_campaign.png",
            "gacha_page.png", "inbox_popup.png", "login_bonus_popup.png", "payment_confirm.png",
            "not_logged_in.png", "points_website_popup.png",
        })
        {
            if (File.Exists(Path.Combine(Dir, f))) Load(f, loginRecovery: true);
        }
    }

    [Fact]
    public void BootSplash_ColdBoot_ClassifiesWithoutLoginRecovery()
    {
        // 冷启动/掉线首帧：流程记忆里还没有 BootSplash（loginRecovery=false）也必须判出——
        // 主判底栏「法律及隐私政策」，副判「确认按钮」+konami/版本。判不出会落旧 TitleScreen
        // 规则按 X，X 在此页无效 → 防掉线恢复从第一步就卡死
        var (screen, _) = Load("boot_splash.png", loginRecovery: false);
        Assert.Equal(MlScreen.BootSplash, screen);

        // 前几轮 下→右→A 登录序列；超过 BootLoginNavRounds 改持续按 A（页面提示按「确认」按钮）
        var nav = MyLeagueBrain.Decide(MlScreen.BootSplash, OcrResult.Empty, screenStreak: 0);
        Assert.Equal(MlActionKind.PadLoginNav, nav.Kind);
        var confirm = MyLeagueBrain.Decide(MlScreen.BootSplash, OcrResult.Empty,
            screenStreak: MyLeagueBrain.BootLoginNavRounds);
        Assert.Equal(MlActionKind.PadConfirm, confirm.Kind);
    }

    [Fact]
    public void BootSplash_LoadingFrame_StillClassifiesBootSplash()
    {
        // 转圈加载帧（同页变暗+转圈）：底栏词仍在，同判 BootSplash；加载期按键无效但无害，
        // 绝不能落 Unknown→连续 B 逃生（B 会打开侧边栏/退出登录）
        var (screen, _) = Load("boot_splash_loading.png", loginRecovery: false);
        Assert.Equal(MlScreen.BootSplash, screen);
    }

    [Fact]
    public void GachaPage_AlwaysBacksOut_NeverConsumes()
    {
        // 抽取页（Special Player List）：「有限次数抽取」主判、「查看全部+名球员」兜底。
        // 分类与 loginRecovery 无关（判定块先于门控块）。处置=B 退出，绝无确认类动作
        var (screen, _) = Load("gacha_page.png", loginRecovery: false);
        Assert.Equal(MlScreen.GachaPage, screen);
        var action = MyLeagueBrain.Decide(MlScreen.GachaPage, OcrResult.Empty, screenStreak: 0);
        Assert.Equal(MlActionKind.GamepadBack, action.Kind);
    }

    [Fact]
    public void PaymentConfirmDialog_DirectConfirmOnCancel()
    {
        // 付款确认弹窗（抽取页误按后）：弹窗底下就是抽取页（查看全部/名球员同屏），
        // ★必须先于抽取页判定；含「取消」也绝不能走 HasConfirm 的「有取消先上移」——
        // 上方是金币付款键。直接按 A = 点默认焦点「取消」
        var (screen, ocr) = Load("payment_confirm.png", loginRecovery: true);
        Assert.Equal(MlScreen.PaymentConfirmDialog, screen);
        var action = MyLeagueBrain.Decide(screen, ocr, screenStreak: 0);
        Assert.Equal(MlActionKind.PadConfirm, action.Kind);
        Assert.NotEqual(MlActionKind.PadUpConfirm, action.Kind);

        // 判定序守卫（纯构造）：付款确认 + 抽取页词同屏 → 付款确认赢
        Assert.Equal(MlScreen.PaymentConfirmDialog, MyLeagueBrain.Classify(
            Array.Empty<YoloDetection>(),
            Ocr(("付款确认", 0.5, 0.3), ("取消", 0.5, 0.6), ("查看全部", 0.2, 0.6), ("名球员", 0.2, 0.3)),
            loginRecovery: true));
    }

    [Fact]
    public void LoginRecoveryPopupChain_ClassifiesHasConfirm_AndPressesA()
    {
        // 重登弹窗链（每周轮换不认具体词，只认推进按钮词）：登录奖励「继续」/
        // 物品已送到收件箱「OK」/上一场比赛被放弃「OK」。
        // （campaign_grid 已升级为标题专用判定 CampaignGridPage，见下，不再走此门控块）
        // ★被放弃弹窗底下透出标题页的「法律及隐私政策」——弹窗链判定必须先于 BootSplash
        foreach (var f in new[] { "login_bonus_popup.png", "inbox_popup.png", "boot_abandoned_popup.png" })
        {
            var (screen, ocr) = Load(f, loginRecovery: true);
            Assert.Equal(MlScreen.HasConfirm, screen);
            var action = MyLeagueBrain.Decide(screen, ocr, screenStreak: 0);
            Assert.True(action.Kind is MlActionKind.PadConfirm or MlActionKind.PadUpConfirm,
                $"{f}: 应为推进类动作，实际 {action.Kind}:{action.Target}");
        }
    }

    [Fact]
    public void CampaignGridPage_TitleJudge_DirectContinue()
    {
        // New Playing Style 活动奖励网格页（实机 2026-10-19/21 两帧同页）：标题专用判定。
        // ★不依赖 loginRecovery——登录加载慢时该页可能落在重登 100s 窗口之外，
        // 旧门控路径（只认「继续」词）会漏判。处置=直接 A 点默认焦点「继续」领奖推进
        foreach (var f in new[] { "campaign_grid.png", "playing_style_campaign.png" })
        {
            var (cold, ocr) = Load(f, loginRecovery: false);
            Assert.Equal(MlScreen.CampaignGridPage, cold);
            var (warm, _) = Load(f, loginRecovery: true);
            Assert.Equal(MlScreen.CampaignGridPage, warm);
            var action = MyLeagueBrain.Decide(cold, ocr, screenStreak: 0);
            Assert.Equal(MlActionKind.PadConfirm, action.Kind);
            Assert.NotEqual(MlActionKind.PadUpConfirm, action.Kind);
        }

        // 判定词守卫（纯构造）：标题整行 / 逐词拆开（JoinedText 拼回）+ 推进词 双证据命中
        Assert.Equal(MlScreen.CampaignGridPage, MyLeagueBrain.Classify(
            Array.Empty<YoloDetection>(),
            Ocr(("New Playing Style Campaign", 0.5, 0.19), ("继续", 0.5, 0.93)),
            loginRecovery: false));
        Assert.Equal(MlScreen.CampaignGridPage, MyLeagueBrain.Classify(
            Array.Empty<YoloDetection>(),
            Ocr(("New", 0.35, 0.19), ("Playing", 0.44, 0.19), ("Style", 0.53, 0.19),
                ("Campaign", 0.63, 0.19), ("继续", 0.5, 0.93)),
            loginRecovery: false));

        // 负例守卫：标题在而推进词漏读 → 不命中（宁落后续规则也不盲按）；
        // 「继续」词在而标题不在、又无重登窗口 → 不命中（正常流程同名词保护）
        Assert.NotEqual(MlScreen.CampaignGridPage, MyLeagueBrain.Classify(
            Array.Empty<YoloDetection>(),
            Ocr(("New Playing Style Campaign", 0.5, 0.19), ("Y详情", 0.1, 0.93)),
            loginRecovery: false));
        Assert.NotEqual(MlScreen.CampaignGridPage, MyLeagueBrain.Classify(
            Array.Empty<YoloDetection>(), Ocr(("继续", 0.5, 0.75)), loginRecovery: false));

        // 实机负例（2026-10-21 回归）：主菜单横幅同样含整行标题词（收件箱弹窗背后
        // @y0.68 被 OCR 整行读出）——标题必须落在页首（y&lt;0.35）才算网格页，
        // 否则收件箱弹窗被抢判、OK 漏按（首版不限位置的判定在此帧翻车）
        var bannerUnderPopup = Ocr(("NewPlayingStyleCampaign", 0.5, 0.68),
            ("物品已送到收件箱", 0.5, 0.55), ("OK", 0.5, 0.78));
        Assert.NotEqual(MlScreen.CampaignGridPage, MyLeagueBrain.Classify(
            Array.Empty<YoloDetection>(), bannerUnderPopup, loginRecovery: true));
        Assert.Equal(MlScreen.HasConfirm, MyLeagueBrain.Classify(
            Array.Empty<YoloDetection>(), bannerUnderPopup, loginRecovery: true));
    }

    [Fact]
    public void LoginRecoveryGate_RequiresBootSplashMemory()
    {
        // 门控守卫：推进词（继续/OK/下一步/确定）只有在最近见过标题页时才抢判 HasConfirm，
        // 否则正常流程里的同名词（联赛页「继续」等）会被抢走
        var ocr = Ocr(("继续", 0.5, 0.75));
        Assert.NotEqual(MlScreen.HasConfirm, MyLeagueBrain.Classify(
            Array.Empty<YoloDetection>(), ocr, loginRecovery: false));
        Assert.Equal(MlScreen.HasConfirm, MyLeagueBrain.Classify(
            Array.Empty<YoloDetection>(), ocr, loginRecovery: true));
    }

    [Fact]
    public void GachaJudge_ConstructedWords()
    {
        // 抽取页判定词守卫：主判词带价格尾巴（有限次数抽取:150/150）Norm 去标点后可命中；
        // 兜底 查看全部+名球员 双词同屏；付款确认词不存在时不误判付款弹窗
        Assert.Equal(MlScreen.GachaPage, MyLeagueBrain.Classify(
            Array.Empty<YoloDetection>(), Ocr(("有限次数抽取:150/150", 0.8, 0.07)), loginRecovery: false));
        Assert.Equal(MlScreen.GachaPage, MyLeagueBrain.Classify(
            Array.Empty<YoloDetection>(), Ocr(("查看全部", 0.2, 0.6), ("10名球员", 0.2, 0.48)), loginRecovery: false));
    }

    [Fact]
    public void NotLoggedIn_NavigatesDownRightToLogin()
    {
        // 未登录页（掉线重登链第二入口，实机 2026-10-19：游戏未重启、仅会话失效，停在
        // 主菜单壳上）。带主菜单页签/磁贴词，必须先于 MainMenu/YOLO 判定——否则盲走
        // 下×2 导航落点不可控（最坏按在「退出游戏」上）。分类不依赖 loginRecovery
        // （会话可在挂机任意时刻掉，附近没有标题页）
        var (screen, _) = Load("not_logged_in.png", loginRecovery: false);
        Assert.Equal(MlScreen.NotLoggedIn, screen);
        // 实机 2026-10-21 用户视频：默认焦点在顶部页签「游戏主菜单」，直接 A 无效、
        // 先按右会切到学院页签；必须 下（友谊赛）→右（登录）→A
        var action = MyLeagueBrain.Decide(screen, OcrResult.Empty, screenStreak: 0);
        Assert.Equal(MlActionKind.PadLoginNav, action.Kind);

        // 判定词守卫（纯构造）：主判「未登录」；兜底「网络环境」（请检查你的网络环境并登录游戏）
        Assert.Equal(MlScreen.NotLoggedIn, MyLeagueBrain.Classify(
            Array.Empty<YoloDetection>(),
            Ocr(("未登录", 0.6, 0.33), ("登录", 0.65, 0.54), ("退出游戏", 0.65, 0.64)),
            loginRecovery: false));
        Assert.Equal(MlScreen.NotLoggedIn, MyLeagueBrain.Classify(
            Array.Empty<YoloDetection>(),
            Ocr(("请检查你的网络环境并登录游戏", 0.65, 0.43), ("登录", 0.65, 0.54), ("退出游戏", 0.65, 0.64)),
            loginRecovery: false));
    }

    [Fact]
    public void NotLoggedIn_FocusProbe_MakesNavigationIdempotent()
    {
        // 未登录页右侧按钮列：聚焦键整块变白，未聚焦 #333（实机 2026-10-21 视频逐帧）。
        // ★登录失败重试时焦点可能已停在「登录」甚至「退出游戏」，盲目重跑下→右→A 会把
        // 「下」落到退出游戏上、A 直接退出游戏——探针按焦点状态选择动作，序列幂等
        var none = Frame();
        Assert.Equal(MyLeagueBrain.LoginButtonFocus.None, MyLeagueBrain.DetectNotLoggedInFocus(none));
        Assert.Equal(MlActionKind.PadLoginNav, MyLeagueBrain.Decide(
            MlScreen.NotLoggedIn, OcrResult.Empty, 0, frame: none).Kind);

        var login = Frame(band: (0.505, 0.560));
        Assert.Equal(MyLeagueBrain.LoginButtonFocus.Login, MyLeagueBrain.DetectNotLoggedInFocus(login));
        Assert.Equal(MlActionKind.PadConfirm, MyLeagueBrain.Decide(
            MlScreen.NotLoggedIn, OcrResult.Empty, 0, frame: login).Kind);

        var exit = Frame(band: (0.600, 0.655));
        Assert.Equal(MyLeagueBrain.LoginButtonFocus.ExitGame, MyLeagueBrain.DetectNotLoggedInFocus(exit));
        Assert.Equal(MlActionKind.PadUpConfirm, MyLeagueBrain.Decide(
            MlScreen.NotLoggedIn, OcrResult.Empty, 0, frame: exit).Kind);
    }

    [Fact]
    public void BootSplash_FallbackDoesNotSwallowMainMenu()
    {
        // 登录成功后 100s 重登窗口内：主菜单带 eFootball™ 磁贴词（footbal）+ YOLO main_menu。
        // 兜底规则若只看「窗口内 footbal」就会把主菜单判成标题页 → 无限下右A
        // （实机 2026-10-21 用户反馈「标题页判断和未登录走混了」）
        var mainMenuOcr = Ocr(("游戏主菜单", 0.30, 0.06), ("学院", 0.52, 0.06), ("额外选项", 0.72, 0.06),
            ("友谊赛", 0.12, 0.78), ("eFootball联赛", 0.50, 0.80), ("我的联赛", 0.70, 0.80));
        var mainMenuDet = new[] { new YoloDetection("main_menu", 0, 0.90f, 0.10, 0.05, 0.80, 0.90) };
        Assert.Equal(MlScreen.MainMenu, MyLeagueBrain.Classify(
            mainMenuDet, mainMenuOcr, loginRecovery: true));

        // 稀疏加载帧（只剩 eFootball 标志词、无主菜单特征）仍由兜底判标题页——
        // 掉线恢复不能因为收紧规则而漏判
        Assert.Equal(MlScreen.BootSplash, MyLeagueBrain.Classify(
            Array.Empty<YoloDetection>(), Ocr(("eFootball", 0.50, 0.50), ("2027", 0.50, 0.60)),
            loginRecovery: true));
    }

    [Fact]
    public void PointsWebsiteAnnouncement_BacksOutWithB()
    {
        // 登录后公告白页（实机 2026-10-21 用户截图）：「eFootball™积分官方网站问题」+
        // 右上角「B 关闭」。白底整幅文档 + YOLO main_menu（底下是主菜单）→ 必须专用判定，
        // 处置 B 关闭；绝不走主菜单导航（下×2 盲走）或 HasConfirm（上移+A）
        var (screen, _) = Load("points_website_popup.png", loginRecovery: true);
        Assert.Equal(MlScreen.AnnouncementPage, screen);
        var action = MyLeagueBrain.Decide(screen, OcrResult.Empty, screenStreak: 0);
        Assert.Equal(MlActionKind.GamepadBack, action.Kind);
        Assert.Equal(MlActionKind.GamepadBack, MyLeagueBrain.Decide(
            screen, OcrResult.Empty, screenStreak: MyLeagueBrain.StuckStreak).Kind);

        // 判定词守卫（纯构造）：标题碎裂时正文固定句「深表歉意」兜底
        Assert.Equal(MlScreen.AnnouncementPage, MyLeagueBrain.Classify(
            Array.Empty<YoloDetection>(),
            Ocr(("eFootball积分官方网站问题", 0.50, 0.30), ("我们对此问题造成的不便深表歉意", 0.50, 0.55)),
            loginRecovery: false));
    }

    [Fact]
    public void LoginRecoveryWindow_CoversNotLoggedInEntry()
    {
        // 未登录页与标题页同样武装重登窗口：点「登录」后的奖励弹窗链只认推进词，
        // 不依赖附近是否见过标题页（会话掉线可发生在挂机任意时刻）
        var m = new EfootballBot.Core.Bot.Scenarios.MyLeague.MlFlowMemory();
        var t0 = DateTime.Now;
        m.Record(MlScreen.NotLoggedIn, t0);
        Assert.True(EfootballBot.Core.Bot.Scenarios.ScenarioShared.LoginRecovery(m, t0.AddSeconds(50)));
        Assert.False(EfootballBot.Core.Bot.Scenarios.ScenarioShared.LoginRecovery(m, t0.AddSeconds(101)));
    }

    /// <summary>构造测试帧：默认全黑；band 给出时在未登录页按钮列区域（x 0.52~0.77）
    /// 按归一化 y 区间画白带（模拟被聚焦的按钮）。</summary>
    private static FrameData Frame(int w = 200, int h = 200, (double Y0, double Y1)? band = null)
    {
        var bgra = new byte[w * h * 4];
        if (band is { } b)
        {
            for (int y = (int)(h * b.Y0); y < (int)(h * b.Y1); y++)
            {
                for (int x = (int)(w * 0.52); x < (int)(w * 0.77); x++)
                {
                    int i = (y * w + x) * 4;
                    bgra[i] = 255;
                    bgra[i + 1] = 255;
                    bgra[i + 2] = 255;
                }
            }
        }
        return new FrameData(bgra, w, h, 0);
    }

    private static OcrResult Ocr(params (string text, double x, double y)[] words)
    {
        var list = words.Select(w => new OcrWord(w.text, w.x, w.y, 0.1, 0.03)).ToList();
        return new OcrResult(list, 1280, 720);
    }
}
