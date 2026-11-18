namespace EfootballBot.Core;

/// <summary>
/// 日志级别：全程序共用（挂机引擎、工具层、界面、Worker 管道）。
/// 放在 Core 而不是某个模块里——工具层不许依赖挂机模块，日志级别是公共词汇。
/// </summary>
public enum LogLevel
{
    Info,
    Success,
    Warn,
    Error,
    Debug,
}
