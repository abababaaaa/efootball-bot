using System.IO;
using EfootballBot.Core.Config;
using EfootballBot.Core.System;

// ===========================================================================
// 架构锁 · 工具层 Core/Tools（v7.2）
// 本层职责：本地工具（赛前设置 / 打开路径 / 窗口防卡死 …），与挂机模块完全解耦。
// 硬约束（构建期 tools/arch-guard/arch-guard.ps1 强制，违反即编译失败）：
//   · 不许引用挂机层（EfootballBot.Core.Bot）—— 共享件放 Core/**，不要借挂机的件
//   · 工具 id 字面量只许在 ToolIds.cs，具体工具只许在 ToolCatalog 里实例化
//   · 界面层（MainWindow*.cs）不许直接 Process.Start：起进程/打开路径一律走 ToolCatalog
// 新增工具：ToolIds 加常量 + ToolCatalog 加一条 + 本目录一个 *Tool.cs 实现。
// 除非用户明确要求更换写法，否则按本层约定写。
// ===========================================================================

namespace EfootballBot.Core.Tools;

/// <summary>
/// 工具层共享件：两服游戏路径的定位（Settings.exe / 游戏目录 / 游戏本体）。
/// 从 MainWindow 搬出来的——原来每段「打开设置工具/打开目录/启动游戏」都各写一遍
/// <c>intl ? … : …</c> 的定位逻辑，路径记忆写配置也散在界面里。
///
/// 分区隔离是硬规则：国服不走 Steam 回退——Steam 库里是国际服安装，
/// 国服游戏未启动时跌进去会打开国际服的设置工具（实机 2026-10-13 用户报告）。
/// </summary>
public static class ToolPaths
{
    /// <summary>一个分区的游戏画像（进程名 / Steam AppId / 本体 exe 名 / 界面用简称）。</summary>
    /// <param name="Region">分区。</param>
    /// <param name="Label">界面用简称（国服 / 国际服）。</param>
    /// <param name="ProcessName">运行时进程名（判断是否已在运行）。</param>
    /// <param name="SteamAppId">Steam AppId（仅国际服有；国服为 null = 不许走 Steam）。</param>
    /// <param name="GameExeName">游戏本体 exe 名（无扩展名比较）。</param>
    public sealed record Profile(ServerRegion Region, string Label, string ProcessName,
        string? SteamAppId, string GameExeName);

    /// <summary>取分区画像。</summary>
    public static Profile Of(ServerRegion region) => region == ServerRegion.Cn
        ? new Profile(region, "国服", GameProcesses.Cn, null, GameSettingsLocator.CnGameExeName)
        : new Profile(region, "国际服", GameProcesses.Intl, GameSettingsLocator.IntlSteamAppId,
            GameSettingsLocator.IntlGameExeName);

    /// <summary>上次定位记忆里该服保存的 Settings.exe 路径（可能为空/失效）。</summary>
    public static string SavedSettingsTool(AppConfig cfg, ServerRegion region)
        => region == ServerRegion.Cn ? cfg.GamePaths.CnSettingsTool : cfg.GamePaths.IntlSettingsTool;

    /// <summary>
    /// 定位该服的 Settings.exe：运行中的游戏进程锚定 → 上次定位记忆兜底，成功后写回记忆。
    /// allowSteamFallback 只在国际服为 true（见类型注释的分区隔离规则）。
    /// </summary>
    public static string? LocateSettingsTool(AppConfig cfg, ServerRegion region,
        Action<string, LogLevel>? log = null)
    {
        var profile = Of(region);
        string? path = GameSettingsLocator.Locate(profile.ProcessName, allowSteamFallback: profile.SteamAppId is not null);
        if (path is null)
        {
            string saved = SavedSettingsTool(cfg, region);
            if (saved.Length > 0 && File.Exists(saved)) path = saved;
        }
        if (path is not null)
        {
            if (region == ServerRegion.Cn) cfg.GamePaths.CnSettingsTool = path;
            else cfg.GamePaths.IntlSettingsTool = path;
            try { cfg.Save(); } catch (Exception ex) { log?.Invoke($"路径记忆保存失败（不影响使用）：{ex.Message}", LogLevel.Debug); }
        }
        return path;
    }

    /// <summary>
    /// 定位游戏本体 exe：以 Settings.exe 所在目录为提示向上找。
    /// 国服没有 Steam 回退，必须先点过「打开设置工具」形成路径记忆（提示文案由调用方给）。
    /// </summary>
    public static string? LocateGameExe(AppConfig cfg, ServerRegion region)
        => GameSettingsLocator.LocateGameExe(region != ServerRegion.Cn,
            Path.GetDirectoryName(SavedSettingsTool(cfg, region)));

    /// <summary>该服游戏安装目录（借 Settings.exe 定位）；null = 没定位到。</summary>
    public static string? GameDirectory(AppConfig cfg, ServerRegion region, Action<string, LogLevel>? log = null)
        => Path.GetDirectoryName(LocateSettingsTool(cfg, region, log));
}

