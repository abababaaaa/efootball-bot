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
        foreach (var dir in hintDirs)
        {
            var found = FindGameExeNear(dir, intl);
            if (found is not null) return found;
        }
        if (!intl) return null;
        foreach (var library in EnumerateSteamLibraries())
        {
            var found = FindGameExeNear(Path.Combine(library, "steamapps", "common", IntlGameDirName), intl);
            if (found is not null) return found;
        }
        return FindGameExeNear(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam", "steamapps", "common", IntlGameDirName), intl);
    }

    /// <summary>从 startDir 起向上逐级找游戏本体 exe：国际服 {级}\eFootball.exe（根目录），
    /// 国服 {级}\eFootball\Binaries\Win64\eFootballOnline.exe，最多向上 6 级。</summary>
    public static string? FindGameExeNear(string? startDir, bool intl)
    {
        string relative = intl
            ? IntlGameExeName + ".exe"
            : Path.Combine("eFootball", "Binaries", "Win64", CnGameExeName + ".exe");
        var dir = startDir;
        for (int depth = 0; dir is not null && depth < MaxUpwardLevels; depth++)
        {
            var candidate = Path.Combine(dir, relative);
            if (File.Exists(candidate)) return candidate;

            var parent = Directory.GetParent(dir)?.FullName;
            if (parent is null || string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase))
                break;
            dir = parent;
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
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) continue;
            // 形如 "path"		"D:\\SteamLibrary" → 取行内首尾引号之间的值
            int first = trimmed.IndexOf('"');
            int last = trimmed.LastIndexOf('"');
            if (first < 0 || last <= first + 1) continue;
            var value = trimmed[(first + 1)..last].Replace("\\\\", "\\");
            if (Directory.Exists(value)) yield return value;
        }
    }
}
