using Xunit;

namespace EfootballBot.Tests;

// 界面归属护栏：**每个挂机模块的专属设置必须长在该模块自己的页面里**，不许回退到「设置」页。
//
// 起因（2026-11-25 用户反馈）：挂机节奏（Timing.IdleTapMin/MaxMs）与赛后活动任务
// （PostMatchActivity.AutoAdvanceWeeklyTasks）原先挂在「设置 → 重要设定」下，
// 用户在模块页改完设置切到另一个模块，看到的是另一份值，也会找不到该模块的设置。
// 现在每个比赛模块页各有一份同名字段的控件，改动同步回填。
//
// 所以这里锁三条：
//   ① 六个比赛模块页各自带一份挂机节奏控件（含国服三个模块）；
//   ② 三个国际服比赛模块页各带一份赛后活动开关；
//   ③ 「设置」页里不许再出现这两类控件（否则又变成两份入口）。
public class ModuleSettingsPlacementTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "EfootballBot")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string MainWindowXaml()
    {
        string path = Path.Combine(RepoRoot(), "src", "EfootballBot", "MainWindow.xaml");
        Assert.True(File.Exists(path), "找不到 MainWindow.xaml：" + path);
        return File.ReadAllText(path);
    }

    /// <summary>模块内容区在 XAML 里的顺序（含两个服务器的「赛前快捷设置」段，用来自行界定边界）。</summary>
    private static readonly string[] ModuleOrder =
    {
        "PreMatchContent",
        "LeagueContent",
        "WeeklyContent",
        "AiLadderContent",
        "StudContent",
        "CnPreMatchContent",
        "CnLeagueContent",
        "CnWeeklyContent",
        "CnMyLeagueContent",
        "SettingsPanel",
    };

    /// <summary>切出某个模块内容区（从自己的 x:Name 到下一个已知区块开头）。</summary>
    private static string Region(string xaml, string name)
    {
        string marker = $"x:Name=\"{name}\"";
        int start = xaml.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"MainWindow.xaml 里找不到 {marker}");

        int next = ModuleOrder.Length;
        for (int i = 0; i < ModuleOrder.Length; i++)
        {
            if (ModuleOrder[i] != name) continue;
            next = i + 1;
            break;
        }
        Assert.True(next < ModuleOrder.Length, $"{name} 不在 ModuleOrder 的边界表里");

        int end = xaml.IndexOf($"x:Name=\"{ModuleOrder[next]}\"", start, StringComparison.Ordinal);
        Assert.True(end > start, $"{name} 区块的结束边界定位失败（{ModuleOrder[next]}）");
        return xaml[start..end];
    }

    /// <summary>比赛类模块 → 该模块页里那份挂机节奏控件的名字。</summary>
    private static readonly (string Module, string MinBox, string MaxBox, string ErrText)[] IdlePacingOwners =
    {
        ("LeagueContent", "TxtIdleMinLeague", "TxtIdleMaxLeague", "TxtIdleErrLeague"),
        ("WeeklyContent", "TxtIdleMinWeekly", "TxtIdleMaxWeekly", "TxtIdleErrWeekly"),
        ("AiLadderContent", "TxtIdleMinAi", "TxtIdleMaxAi", "TxtIdleErrAi"),
        ("CnLeagueContent", "TxtIdleMinCnLeague", "TxtIdleMaxCnLeague", "TxtIdleErrCnLeague"),
        ("CnWeeklyContent", "TxtIdleMinCnWeekly", "TxtIdleMaxCnWeekly", "TxtIdleErrCnWeekly"),
        ("CnMyLeagueContent", "TxtIdleMinCnMl", "TxtIdleMaxCnMl", "TxtIdleErrCnMl"),
    };

    [Fact]
    public void 每个比赛模块页都自带挂机节奏控件()
    {
        string xaml = MainWindowXaml();
        var missing = new List<string>();

        foreach (var (module, minBox, maxBox, errText) in IdlePacingOwners)
        {
            string region = Region(xaml, module);
            foreach (var name in new[] { minBox, maxBox, errText })
                if (!region.Contains($"x:Name=\"{name}\"", StringComparison.Ordinal))
                    missing.Add($"{module} 缺少 {name}");
            if (!region.Contains("TextChanged=\"IdleTap_TextChanged\"", StringComparison.Ordinal))
                missing.Add($"{module} 的节奏输入框没有接 IdleTap_TextChanged");
        }

        Assert.True(missing.Count == 0,
            "这些模块页里的挂机节奏控件没长在模块自己身上：\n" + string.Join("\n", missing));
    }

    [Fact]
    public void 国际服三个比赛模块页各自带赛后活动开关()
    {
        string xaml = MainWindowXaml();
        var pairs = new[]
        {
            ("LeagueContent", "ChkPmAdvLeague"),
            ("WeeklyContent", "ChkPmAdvWeekly"),
            ("AiLadderContent", "ChkPmAdvAi"),
        };

        var missing = new List<string>();
        foreach (var (module, box) in pairs)
        {
            string region = Region(xaml, module);
            if (!region.Contains($"x:Name=\"{box}\"", StringComparison.Ordinal))
                missing.Add($"{module} 缺少 {box}");
            if (!region.Contains("Checked=\"PmAutoAdvance_Changed\"", StringComparison.Ordinal))
                missing.Add($"{module} 的赛后活动开关没有接 PmAutoAdvance_Changed");
        }

        Assert.True(missing.Count == 0,
            "赛后活动任务开关必须长在国际服各比赛模块页里：\n" + string.Join("\n", missing));
    }

    [Fact]
    public void 设置页不得再出现模块专属设置()
    {
        string xaml = MainWindowXaml();
        int start = xaml.IndexOf("x:Name=\"SettingsPanel\"", StringComparison.Ordinal);
        int end = xaml.IndexOf("x:Name=\"FeedbackPanel\"", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "设置面板区间定位失败");
        string settings = xaml[start..end];

        var leaked = new List<string>();
        foreach (var token in new[]
                 {
                     "IdleTap_TextChanged", "TxtIdleMin", "TxtIdleMax", "TxtIdleErr",
                     "ChkPmAdv", "PmAutoAdvance_Changed", "Tag=\"Imp2\"", "Tag=\"Imp3\"",
                     "SubRbImp2", "SubRbImp3",
                 })
            if (settings.Contains(token, StringComparison.Ordinal))
                leaked.Add(token);

        Assert.True(leaked.Count == 0,
            "设置页里还留着已下放到模块页的控件：\n" + string.Join("\n", leaked));
    }

    // ② 本模块运行环境：每个模块页都要看得到「本次挂机会读到的全局开关」。
    //
    // 这四项（Pad.Backend / HidHide.HidePhysicalGamepad / Timing.HumanPacingEnabled /
    // BackgroundParking.Enabled）由引擎每次挂机现读，此前只有「设置」页一处入口：
    // 用户在模块页调完就开始挂机，根本不知道这次用的是哪套手柄/节奏/停靠行为。
    // 现在模块页各带一份（与设置页同字段、互相同步），测试锁住「页面里确实有」+「确实接了处理」。

    private static readonly (string Module, string Suffix)[] RunEnvOwners =
    {
        ("LeagueContent", "League"),
        ("WeeklyContent", "Weekly"),
        ("AiLadderContent", "Ai"),
        ("StudContent", "Stud"),
        ("CnLeagueContent", "CnLeague"),
        ("CnWeeklyContent", "CnWeekly"),
        ("CnMyLeagueContent", "CnMl"),
    };

    [Fact]
    public void 每个模块页都带本模块运行环境控件()
    {
        string xaml = MainWindowXaml();
        var missing = new List<string>();

        foreach (var (module, suffix) in RunEnvOwners)
        {
            string region = Region(xaml, module);
            var expect = new (string Name, string Handler)[]
            {
                ($"CmbEnvBackend{suffix}", "ModuleEnvBackend_Changed"),
                ($"ChkEnvHidePad{suffix}", "ModuleEnvToggles_Changed"),
                ($"ChkEnvPace{suffix}", "ModuleEnvToggles_Changed"),
                ($"ChkEnvParking{suffix}", "ModuleEnvToggles_Changed"),
                ($"TxtEnvTimeout{suffix}", null!),
            };

            foreach (var (name, handler) in expect)
            {
                if (!region.Contains($"x:Name=\"{name}\"", StringComparison.Ordinal))
                {
                    missing.Add($"{module} 缺少 {name}");
                    continue;
                }
                if (handler is null) continue;
                if (!region.Contains($"\"{handler}\"", StringComparison.Ordinal))
                    missing.Add($"{module} 的 {name} 没有接 {handler}");
            }
        }

        Assert.True(missing.Count == 0,
            "本模块运行环境控件没长在模块自己身上：\n" + string.Join("\n", missing));
    }

    [Fact]
    public void 运行环境控件必须登记回填()
    {
        // XAML 里加了控件但忘了在 InitModuleSettingBindings 登记 → 控件永远停在 XAML 默认值，
        // 表现为「设置页勾着、模块页没勾」，比没有更糟。这里按后缀逐个核对登记行。
        string path = Path.Combine(RepoRoot(), "src", "EfootballBot", "MainWindow.Settings.cs");
        Assert.True(File.Exists(path), "找不到 MainWindow.Settings.cs：" + path);
        string code = File.ReadAllText(path);

        var missing = new List<string>();
        foreach (var (_, suffix) in RunEnvOwners)
        {
            if (!code.Contains($"CmbEnvBackend{suffix}", StringComparison.Ordinal))
                missing.Add($"InitModuleSettingBindings 未登记 CmbEnvBackend{suffix}");
            if (!code.Contains($"ChkEnvHidePad{suffix}", StringComparison.Ordinal))
                missing.Add($"InitModuleSettingBindings 未登记 ChkEnvHidePad{suffix}");
        }

        Assert.True(missing.Count == 0,
            "运行环境控件漏登记（页面不会跟着配置刷新）：\n" + string.Join("\n", missing));
    }

    [Fact]
    public void 国服我的联赛页带满级换人设置()
    {
        // 国服我的联赛继承国际服场景，满级换人巡检读的就是 CnMyLeague.AutoSwapMaxLevel /
        // AutoSwapEveryMatches。此前这页只有难度/场次/道具/节奏，用户以为国服不支持满级换人。
        string xaml = MainWindowXaml();
        string region = Region(xaml, "CnMyLeagueContent");

        Assert.True(region.Contains("x:Name=\"ChkCnMlAutoSwapMaxLevel\"", StringComparison.Ordinal),
            "国服我的联赛页缺少「满级自动换人」开关");
        Assert.True(region.Contains("x:Name=\"CmbCnMlAutoSwapEvery\"", StringComparison.Ordinal),
            "国服我的联赛页缺少「满级换人巡检间隔」下拉");

        string code = File.ReadAllText(Path.Combine(RepoRoot(), "src", "EfootballBot", "MainWindow.Settings.cs"));
        Assert.True(code.Contains("_cfg.CnMyLeague.AutoSwapMaxLevel", StringComparison.Ordinal),
            "CnMyLeague.AutoSwapMaxLevel 没有接线（加载/保存）");
        Assert.True(code.Contains("_cfg.CnMyLeague.AutoSwapEveryMatches", StringComparison.Ordinal),
            "CnMyLeague.AutoSwapEveryMatches 没有接线（加载/保存）");
    }
}
