using System.IO;

namespace EfootballBot.Core.System;

/// <summary>
/// 路径解析：兼容普通文件夹发布与单文件自解压发布。
/// 单文件发布时 AppContext.BaseDirectory 指向 %TEMP%\.net\&lt;App&gt;\&lt;hash&gt; 解压目录，
/// 而 Environment.ProcessPath 才是 exe 真实位置——日志等需要随身携带的文件写在 exe 旁，
/// 随包资源（models）优先在解压目录找、找不到再回退 exe 同级目录。
/// </summary>
public static class AppPaths
{
    /// <summary>exe 真实所在目录（单文件发布时不是临时解压目录）。</summary>
    public static string ExeDir { get; } =
        Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) is { Length: > 0 } dir
            ? dir
            : AppContext.BaseDirectory;

    /// <summary>logs 目录：固定在 exe 旁，拷贝整个文件夹/单 exe 时日志跟着走。</summary>
    public static string LogDir => Path.Combine(ExeDir, "logs");

    /// <summary>
    /// 按优先级查找随包资源文件：先看单文件解压目录（BaseDirectory），再看 exe 同级目录。
    /// 都不存在返回 null。
    /// </summary>
    public static string? FindBundledFile(params string[] relativeParts)
    {
        var rel = Path.Combine(relativeParts);
        var extracted = Path.Combine(AppContext.BaseDirectory, rel);
        if (File.Exists(extracted)) return extracted;
        var beside = Path.Combine(ExeDir, rel);
        return File.Exists(beside) ? beside : null;
    }
}
