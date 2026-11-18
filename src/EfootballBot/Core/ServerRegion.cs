namespace EfootballBot.Core;

/// <summary>
/// 服务器分区：整个程序的一级分界（挂机模块与工具模块共用同一套取值）。
/// 国服（实况足球在线）与国际服（eFootball）的窗口、菜单、按钮语义都不同：
/// 挂机模块只在自己分区内创建/接力，工具（赛前设置/路径定位）也按分区各自记路径。
/// </summary>
public enum ServerRegion
{
    /// <summary>国际服（Steam / eFootball，窗口进程 eFootball.cs）。</summary>
    Intl,
    /// <summary>国服（实况足球在线，窗口进程 eFootballOnline.cs）。</summary>
    Cn,
}

public static class ServerRegionText
{
    /// <summary>界面/日志用中文名。</summary>
    public static string Display(this ServerRegion region)
        => region == ServerRegion.Cn ? "国服" : "国际服";

    /// <summary>分区短标识（日志前缀、配置键用，稳定不变）。</summary>
    public static string Key(this ServerRegion region)
        => region == ServerRegion.Cn ? "cn" : "intl";
}
