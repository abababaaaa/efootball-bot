using EfootballBot.Core.Bot.Modules;
using Xunit;

namespace EfootballBot.Tests;

// 架构护栏（模块化的「不许」）：这些断言直接扫源码，防的是「今天能跑、下个月又串回去」。
// 三条边界：
//   ① 共享层 Core/Bot/Match 不许反向依赖任何模块（Scenarios.*）；
//   ② 模块之间只允许「只读借用界面判定」（*Brain.Classify），不许引用别的模块的 Scenario；
//   ③ 模块 id 字面量只许出现在注册表（Modules）里，其他地方一律用 ModuleIds 常量。
public class ArchitectureTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "EfootballBot")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static IEnumerable<string> SourceFiles(string relative)
    {
        string root = Path.Combine(RepoRoot(), "src", "EfootballBot", relative);
        if (!Directory.Exists(root)) yield break;
        foreach (var f in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string p = f.Replace('\\', '/');
            if (p.Contains("/bin/") || p.Contains("/obj/")) continue;
            yield return f;
        }
    }

    /// <summary>去掉行注释后再匹配，避免把「说明文字里提到 Scenarios」当成依赖。</summary>
    private static IEnumerable<(int Line, string Text)> CodeLines(string file)
    {
        int n = 0;
        foreach (var raw in File.ReadLines(file))
        {
            n++;
            string t = raw.Trim();
            if (t.StartsWith("//") || t.StartsWith("*") || t.StartsWith("///")) continue;
            yield return (n, raw);
        }
    }

    [Fact]
    public void 共享层不得反向依赖任何模块()
    {
        var bad = new List<string>();
        foreach (var file in SourceFiles("Core/Bot/Match"))
            foreach (var (line, text) in CodeLines(file))
                if (text.Contains("EfootballBot.Core.Bot.Scenarios"))
                    bad.Add($"{Path.GetFileName(file)}:{line}: {text.Trim()}");
        Assert.True(bad.Count == 0, "共享层引用了模块命名空间：\n" + string.Join("\n", bad));
    }

    [Fact]
    public void 模块不得引用其他模块的场景类型()
    {
        // 允许的继承/引用白名单：国服我的联赛继承国际服基类（联赛内流程完全一致）
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "CnMyLeagueScenario->MyLeagueScenario",
        };

        string scenariosRoot = Path.Combine(RepoRoot(), "src", "EfootballBot", "Core", "Bot", "Scenarios");
        var scenarioTypes = new Dictionary<string, string>(StringComparer.Ordinal); // 类型名 → 所属模块目录
        foreach (var dir in Directory.EnumerateDirectories(scenariosRoot))
        {
            string module = Path.GetFileName(dir);
            foreach (var f in Directory.EnumerateFiles(dir, "*Scenario.cs", SearchOption.AllDirectories))
                scenarioTypes[Path.GetFileNameWithoutExtension(f)] = module;
        }

        var bad = new List<string>();
        foreach (var file in SourceFiles("Core/Bot/Scenarios"))
        {
            string rel = file.Substring(scenariosRoot.Length).TrimStart('\\', '/');
            string module = rel.Contains('\\') || rel.Contains('/')
                ? rel.Split('\\', '/')[0]
                : "(root)";
            string self = Path.GetFileNameWithoutExtension(file);

            foreach (var (line, text) in CodeLines(file))
                foreach (var (type, owner) in scenarioTypes)
                {
                    if (type == self || owner == module) continue;
                    if (allowed.Contains($"{self}->{type}")) continue;
                    if (!text.Contains(type, StringComparison.Ordinal)) continue;
                    bad.Add($"{module}/{self}:{line} 引用了 {owner}/{type}");
                }
        }
        Assert.True(bad.Count == 0, "模块之间互相引用了场景类型：\n" + string.Join("\n", bad));
    }

    [Fact]
    public void 模块_id_字面量只许出现在注册表里()
    {
        var ids = typeof(ModuleIds)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();
        Assert.NotEmpty(ids);

        var bad = new List<string>();
        foreach (var file in SourceFiles("."))
        {
            string norm = file.Replace('\\', '/');
            if (norm.Contains("/Core/Bot/Modules/")) continue; // 注册表就是这些字面量的家
            if (norm.EndsWith("/Core/ServerRegion.cs")) continue; // 分区键定义（cn/intl）
            foreach (var (line, text) in CodeLines(file))
                foreach (var id in ids)
                    if (text.Contains($"\"{id}\"", StringComparison.Ordinal))
                        bad.Add($"{Path.GetFileName(file)}:{line} 出现字面量 \"{id}\"");
        }
        Assert.True(bad.Count == 0,
            "模块 id 请用 ModuleIds 常量（注册表之外不许写字面量）：\n" + string.Join("\n", bad));
    }

    /// <summary>
    /// 以构建期脚本为准（ArchitectureTests 里的 C# 规则只是「IDE 内方便跑」的子集）。
    /// 同一个仓库里规则只许有一份：脚本改了、测试自动跟上，不会出现两套口径。
    /// </summary>
    [Fact]
    public void 架构锁脚本通过_规则唯一来源()
    {
        string root = RepoRoot();
        string script = Path.Combine(root, "tools", "arch-guard", "arch-guard.ps1");
        Assert.True(File.Exists(script), "架构锁脚本缺失：tools/arch-guard/arch-guard.ps1");

        var psi = new System.Diagnostics.ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = root,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(script);

        System.Diagnostics.Process? p;
        try
        {
            p = System.Diagnostics.Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // pwsh（PowerShell 7+）未安装的精简环境（部分 CI/裸机）无法运行脚本，
            // 跳过而非失败 —— 架构锁规则本身已由本文件其余纯 C# 断言覆盖，
            // 构建期 Directory.Build.targets 的 ArchGuard hook 在有 pwsh 的机器上仍生效。
            return;
        }
        Assert.NotNull(p);
        using (p)
        {
            string stdout = p!.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(120_000);

            Assert.True(p.ExitCode == 0, $"arch-guard 未通过：\n{stdout}\n{stderr}");
        }
    }

    [Fact]
    public void 工具层不依赖挂机层()
    {
        var bad = new List<string>();
        foreach (var file in SourceFiles("Core/Tools"))
            foreach (var (line, text) in CodeLines(file))
                if (text.Contains("EfootballBot.Core.Bot"))
                    bad.Add($"{Path.GetFileName(file)}:{line}: {text.Trim()}");
        Assert.True(bad.Count == 0, "工具层引用了挂机层：\n" + string.Join("\n", bad));
    }

    [Fact]
    public void 界面层不许直接起进程()
    {
        var bad = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "EfootballBot"), "MainWindow*.cs"))
            foreach (var (line, text) in CodeLines(file))
                if (text.Contains("Process.Start") || text.Contains("ProcessStartInfo"))
                    bad.Add($"{Path.GetFileName(file)}:{line}: {text.Trim()}");
        Assert.True(bad.Count == 0,
            "界面层只渲染；起进程/打开路径请走 Core/Tools（ToolCatalog）：\n" + string.Join("\n", bad));
    }

    [Fact]
    public void 工具_id_字面量只许出现在_ToolIds()
    {
        var ids = typeof(EfootballBot.Core.Tools.ToolIds)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();
        Assert.NotEmpty(ids);

        var bad = new List<string>();
        foreach (var file in SourceFiles("."))
        {
            // 例外：ServerRegion.cs 定义分区键（cn/intl），与模块 id 恰好同字，属正当定义
            if (Path.GetFileName(file) is "ToolIds.cs" or "ServerRegion.cs") continue;
            foreach (var (line, text) in CodeLines(file))
                foreach (var id in ids)
                    if (text.Contains($"\"{id}\"", StringComparison.Ordinal))
                        bad.Add($"{Path.GetFileName(file)}:{line} 出现字面量 \"{id}\"");
        }
        Assert.True(bad.Count == 0, "工具 id 请用 ToolIds 常量：\n" + string.Join("\n", bad));
    }

    [Fact]
    public void 引擎不认识具体场景类型()
    {
        string engine = Path.Combine(RepoRoot(), "src", "EfootballBot", "Core", "Bot", "BotEngine.cs");
        var text = File.ReadAllText(engine);
        foreach (var type in new[]
                 {
                     "MyLeagueScenario", "CnMyLeagueScenario", "WeeklyEventScenario", "CnWeeklyScenario",
                     "AiLadderScenario", "PeakLadderScenario", "StudTrainingScenario",
                 })
            Assert.DoesNotContain(type, text);
    }
}
