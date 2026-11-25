using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace EfootballBot.Core.System;

/// <summary>
/// 定位游戏自带的设置工具 Settings.exe（「赛前快捷设置」模块）。安装位置因人而异，按三级回退查找：
/// ① 运行中的游戏进程路径（最可靠，Steam/国服任何安装位置都覆盖）——从游戏 exe 目录
///    向上逐级找 Settings.exe（国际服：游戏根目录）或 SettingTool\Settings.exe（国服：子目录）；
/// ② Steam 注册表 + steamapps/libraryfolders.vdf 逐库解析（游戏未启动时，国际服）；
/// ③ Steam 默认安装路径（Program Files (x86)\Steam）。
/// 实测目录结构（2026-10-12）：
///   国际服 {Steam库}\steamapps\common\eFootball\Settings.exe（根目录）
///   国服   {安装目录}\eFootball\Binaries\Win64\eFootballOnline.exe + {安装目录}\SettingTool\Settings.exe
/// </summary>
public static class GameSettingsLocator
{
    public const string SettingsExeName = "Settings.exe";
    public const string SettingsSubDir = "SettingTool";
    private const string IntlGameDirName = "eFootball";
    private const int MaxUpwardLevels = 6;

    /// <summary>游戏本体 exe 文件名（不带扩展名；进程名与 exe 同名）：国际服在游戏根目录，
    /// 国服在 {安装目录}\eFootball\Binaries\Win64\（实测目录结构见类注释）。</summary>
    public const string IntlGameExeName = "eFootball";
    public const string CnGameExeName = "eFootballOnline";

    /// <summary>国际服 Steam 应用 ID（eFootball，steam://rungameid 正规拉起用）。</summary>
    public const string IntlSteamAppId = "1665460";

    /// <summary>返回定位到的 Settings.exe 完整路径；找不到返回 null。
    /// <paramref name="allowSteamFallback"/>=false（国服）时只走进程锚定——Steam 回退两级
    /// 是国际服专属：国服不走 Steam，游戏未启动时跌进 Steam 库会打开国际服的设置工具
    /// （实机 2026-10-13 用户报告：国服面板点开的是 c:\program files (x86)\steam\...）。</summary>
    public static string? Locate(string gameProcessName, bool allowSteamFallback = true)
        => LocateFromRunningProcess(gameProcessName)
           ?? (allowSteamFallback ? LocateFromSteamLibraries() ?? LocateFromDefaultSteamPath() : null);

    /// <summary>
    /// 从 startDir 起向上逐级检查：{级}\Settings.exe（国际服在根目录）或
    /// {级}\SettingTool\Settings.exe（国服在 SettingTool 子目录），最多向上 6 级。
    /// </summary>
    public static string? FindSettingsNear(string? startDir)
    {
        var dir = startDir;
        for (int depth = 0; dir is not null && depth < MaxUpwardLevels; depth++)
        {
            var plain = Path.Combine(dir, SettingsExeName);
            if (File.Exists(plain)) return plain;

            var inSub = Path.Combine(dir, SettingsSubDir, SettingsExeName);
            if (File.Exists(inSub)) return inSub;

            var parent = Directory.GetParent(dir)?.FullName;
            if (parent is null || string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase))
                break;
            dir = parent;
        }
        return null;
    }

    /// <summary>
    /// 定位游戏本体 exe（「快速打开游戏」按钮）。国际服 = {游戏根}\eFootball.exe，
    /// 国服 = {安装目录}\eFootball\Binaries\Win64\eFootballOnline.exe。
    /// 先试 hintDirs（通常是上次定位的 Settings.exe 所在目录，向上逐级找），国际服再走
    /// Steam 库/默认路径回退（与设置工具同链路）；国服绝不碰 Steam——那是国际服的安装。
    /// 找不到返回 null。
    /// </summary>
    public static string? LocateGameExe(bool intl, params string?[] hintDirs)
    {
        // 候选池 + 打分：进程 > Steam 库(有 appmanifest 的活副本) > 提示目录 > Steam 默认路径。
        // 本机实测：C 盘库有 appmanifest_1665460.acf（StateFlags=4），D 盘库只剩 2024 年的死副本；
        // 只看"先枚举到哪个库/哪个提示"会把代理投给死副本。
        var candidates = new List<(string Exe, int Score)>();

        if (intl && LocateGameExeFromRunningProcess() is { } running)
            candidates.Add((running, 3000));

        if (intl)
        {
            foreach (var library in EnumerateSteamLibraries())
            {
                var found = FindGameExeNear(Path.Combine(library, "steamapps", "common", IntlGameDirName), intl);
                if (found is not null) candidates.Add((found, 1000 + ScoreSteamLibrary(library)));
            }
        }

        foreach (var dir in hintDirs)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var found = FindGameExeNear(dir, intl);
            if (found is not null) candidates.Add((found, 800));
        }

        if (intl)
        {
            var fallback = FindGameExeNear(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Steam", "steamapps", "common", IntlGameDirName), intl);
            if (fallback is not null) candidates.Add((fallback, 500));
        }

        if (candidates.Count == 0) return null;
        return candidates
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => SafeFileTime(c.Exe))
            .First().Exe;
    }

    /// <summary>
    /// 从 startDir 起向上逐级找游戏本体 exe，最多向上 6 级。
    /// 国际服的安装嵌套层级**不统一**：实测本机是
    /// {库}\steamapps\common\eFootball\eFootball\eFootball\Binaries\Win64\eFootball.exe
    /// （比文档里的常规布局多一层 eFootball），所以按候选表逐个试，最后再用限深递归兜底。
    /// 国服固定 {级}\eFootball\Binaries\Win64\eFootballOnline.exe。
    /// </summary>
    /// <summary>进程内代理 DLL 名（游戏只从自己的 Binaries\Win64 加载同名 DLL）。</summary>
    public const string ProxyDllName = "xinput1_3.dll";

    /// <summary>
    /// 定位游戏真正加载 DLL 的目录（国际服/国服都是 ...\Binaries\Win64）。
    /// 2026-11-19 反馈包需要它：代理没被游戏读取时，要能明确指出「哪个目录里缺 xinput1_3.dll」；
    /// 原来只有一句「请确认已投放」，用户无从核对，只能反复试。
    /// 顺序：运行中进程的映像目录 → 已保存/扫描到的 exe 目录（含 Steam 库与默认路径兜底）。
    /// </summary>
    public static string? LocateGameBinDir(bool intl)
    {
        string exeName = intl ? IntlGameExeName : CnGameExeName;

        // ① 运行中的游戏进程：读映像路径（跨提权可读）。这才是 DLL 真被加载的地方。
        if (GameProcessDir(exeName) is { Length: > 0 } fromProcess) return fromProcess;

        // ② 游戏没开：走「快速打开游戏」的定位链（国际服含 Steam 库打分 + 默认路径回退）
        return LocateGameExe(intl) is { Length: > 0 } exe
               && Path.GetDirectoryName(exe) is { Length: > 0 } dir
            ? dir
            : null;
    }

    /// <summary>按进程名读运行中游戏的映像目录（进程名与 exe 名一致：eFootball / eFootballOnline）。</summary>
    private static string? GameProcessDir(string processName)
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName(processName))
            {
                try
                {
                    var exe = GetProcessImagePath(proc.Id);
                    if (string.IsNullOrEmpty(exe)) continue;
                    if (!Path.GetFileName(exe).Equals(processName + ".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    return Path.GetDirectoryName(exe);
                }
                catch { /* 下一个进程 */ }
                finally { proc.Dispose(); }
            }
        }
        catch { /* 进程枚举失败走下一级 */ }
        return null;
    }

    /// <summary>指定目录里是否已有代理 DLL。目录为空/无权限一律按「没有」处理。</summary>
    public static bool HasProxyDll(string? binDir)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(binDir) && File.Exists(Path.Combine(binDir!, ProxyDllName));
        }
        catch { return false; }
    }

    public static string? FindGameExeNear(string? startDir, bool intl)
    {
        if (string.IsNullOrWhiteSpace(startDir)) return null;

        string[] relatives = intl
            ? new[]
            {
                @"eFootball.exe",
                @"Binaries\Win64\eFootball.exe",
                @"eFootball\Binaries\Win64\eFootball.exe",
                @"eFootball\eFootball\Binaries\Win64\eFootball.exe",
            }
            : new[]
            {
                @"eFootball\Binaries\Win64\" + CnGameExeName + ".exe",
                @"Binaries\Win64\" + CnGameExeName + ".exe",
            };
        string exeName = intl ? IntlGameExeName + ".exe" : CnGameExeName + ".exe";

        var dir = startDir;
        for (int depth = 0; dir is not null && depth < MaxUpwardLevels; depth++)
        {
            foreach (var relative in relatives)
            {
                var candidate = Path.Combine(dir, relative);
                if (File.Exists(candidate)) return candidate;
            }

            // 候选表全落空时（非常规嵌套）：在"像游戏根目录"的一级做限深递归
            if (intl && depth == 0 && Directory.Exists(dir) &&
                (Path.GetFileName(dir).Equals(IntlGameDirName, StringComparison.OrdinalIgnoreCase)
                 || File.Exists(Path.Combine(dir, "LauncherPatcher.exe"))))
            {
                var deep = SearchExeBounded(dir, exeName, maxDepth: 5);
                if (deep is not null) return deep;
            }

            var parent = Directory.GetParent(dir)?.FullName;
            if (parent is null || string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase))
                break;
            dir = parent;
        }
        return null;
    }

    /// <summary>限深广度搜索 exe（跳过 pak/cpk 资源目录）。Steam 库布局怪异时的最后兜底。</summary>
    private static string? SearchExeBounded(string root, string exeName, int maxDepth)
    {
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            try
            {
                var candidate = Path.Combine(dir, exeName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
            if (depth >= maxDepth) continue;
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (name.Equals("pak", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("cpk", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Paks", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Content", StringComparison.OrdinalIgnoreCase)) continue;
                    queue.Enqueue((sub, depth + 1));
                }
            }
            catch { }
        }
        return null;
    }

    /// <summary>Steam 库打分：有 eFootball 的 appmanifest 且 StateFlags=4 → 1000；否则 0。</summary>
    private static int ScoreSteamLibrary(string library)
    {
        try
        {
            var acf = Path.Combine(library, "steamapps", "appmanifest_" + IntlSteamAppId + ".acf");
            if (!File.Exists(acf)) return 0;
            var text = File.ReadAllText(acf);
            return text.Contains("\"StateFlags\"\t\t\"4\"") || text.Contains("\"StateFlags\"\t\t\"6\"")
                ? 900
                : 200;
        }
        catch { return 0; }
    }

    private static long SafeFileTime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path).Ticks; } catch { return 0; }
    }

    /// <summary>① 运行中的游戏进程 → 游戏本体 exe 路径（跨提权可读）。</summary>
    private static string? LocateGameExeFromRunningProcess()
    {
        foreach (var name in new[] { IntlGameExeName, "eFootball2024", "efootballsteam" })
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    try
                    {
                        var exe = GetProcessImagePath(proc.Id);
                        if (!string.IsNullOrEmpty(exe) && File.Exists(exe)
                            && Path.GetFileName(exe).Equals(IntlGameExeName + ".exe", StringComparison.OrdinalIgnoreCase))
                            return exe;
                    }
                    catch { }
                    finally { proc.Dispose(); }
                }
            }
            catch { }
        }
        return null;
    }

    /// <summary>① 运行中的游戏进程：读映像路径后向上找设置工具。
    /// 用 QueryFullProcessImageName（PROCESS_QUERY_LIMITED_INFORMATION）跨提权可读——
    /// 旧 MainModule 方案对提权进程抛拒绝访问（实机 2026-10-13：国服客户端以管理员运行，
    /// eFootballOnline.exe 在跑但 MainModule 读不到 → 国服定位恒失败），物理手柄禁用链同款修法。</summary>
    private static string? LocateFromRunningProcess(string gameProcessName)
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName(gameProcessName))
            {
                try
                {
                    var exe = GetProcessImagePath(proc.Id);
                    if (string.IsNullOrEmpty(exe)) continue;
                    var found = FindSettingsNear(Path.GetDirectoryName(exe));
                    if (found is not null) return found;
                }
                catch { /* 下一个进程 */ }
                finally { proc.Dispose(); }
            }
        }
        catch { /* 进程枚举失败走下一级 */ }
        return null;
    }

    private static string? GetProcessImagePath(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == nint.Zero) return null;
        try
        {
            var sb = new global::System.Text.StringBuilder(1024);
            uint size = 1024;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) && size > 0
                ? sb.ToString(0, (int)size)
                : null;
        }
        finally { CloseHandle(h); }
    }

    [global::System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [global::System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
    [global::System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = global::System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(nint hProcess, uint flags, global::System.Text.StringBuilder text, ref uint size);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>② Steam 库：注册表取 Steam 安装目录，解析 libraryfolders.vdf 登记的全部库。</summary>
    private static string? LocateFromSteamLibraries()
    {
        foreach (var library in EnumerateSteamLibraries())
        {
            var gameRoot = Path.Combine(library, "steamapps", "common", IntlGameDirName);
            var found = FindSettingsNear(gameRoot);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>③ Steam 默认安装路径。</summary>
    private static string? LocateFromDefaultSteamPath()
        => FindSettingsNear(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam", "steamapps", "common", IntlGameDirName));

    /// <summary>Steam 主目录 + libraryfolders.vdf 中登记的全部库目录（主目录总是最先）。</summary>
    private static IEnumerable<string> EnumerateSteamLibraries()
    {
        string? steamDir;
        try
        {
            steamDir = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        }
        catch { steamDir = null; }
        if (string.IsNullOrEmpty(steamDir)) yield break;

        // SteamPath 写成正斜杠（如 C:/Program Files (x86)/Steam），统一回反斜杠
        steamDir = steamDir.Replace('/', '\\');
        yield return steamDir;

        var vdf = Path.Combine(steamDir, "steamapps", "libraryfolders.vdf");
        string[] lines;
        try { lines = File.ReadAllLines(vdf); }
        catch { yield break; }

        foreach (var line in lines)
        {
            var value = ParseVdfPathLine(line);
            if (value is not null && Directory.Exists(value)) yield return value;
        }
    }

    /// <summary>解析 libraryfolders.vdf 中形如 <c>"path" "D:\\SteamLibrary"</c> 的行，提取第二个引号串值。</summary>
    internal static string? ParseVdfPathLine(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) return null;
        int keyEnd = trimmed.IndexOf('"', 1);
        if (keyEnd < 0) return null;
        int valStart = trimmed.IndexOf('"', keyEnd + 1);
        if (valStart < 0) return null;
        var sb = new global::System.Text.StringBuilder(trimmed.Length - valStart);
        for (int i = valStart + 1; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (c == '\\' && i + 1 < trimmed.Length)
            {
                char next = trimmed[i + 1];
                if (next is '\\' or '"') { sb.Append(next); i++; continue; }
            }
            if (c == '"') return sb.ToString();
            sb.Append(c);
        }
        return null;
    }
}