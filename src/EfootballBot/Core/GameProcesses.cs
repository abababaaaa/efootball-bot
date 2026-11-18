namespace EfootballBot.Core;

/// <summary>
/// 两服游戏进程名的唯一来源（挂机引擎与工具层共用）。
/// 以前国服的 eFootballOnline 写在 BotEngine 上、国际服的写在 GameWindow 上，
/// 工具层要判断「游戏是否已在运行」就得引用挂机引擎——那就是工具依赖挂机的口子。
/// </summary>
public static class GameProcesses
{
    /// <summary>国际服（Steam 版 eFootball）。</summary>
    public const string Intl = "eFootball";

    /// <summary>国服（实况足球在线）。</summary>
    public const string Cn = "eFootballOnline";

    /// <summary>按分区取进程名。</summary>
    public static string For(ServerRegion region) => region == ServerRegion.Cn ? Cn : Intl;
}
