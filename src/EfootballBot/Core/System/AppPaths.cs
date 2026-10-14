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

    /// <summary>可写数据目录：exe 旁 data\（config、崩溃日志、停靠记录等，原 %APPDATA%\EfootballBot）。
    /// 全部收在程序文件夹内——拷贝整个文件夹即完整迁移，不在 C 盘用户目录留数据
    /// （2026-10-11 用户要求：所有操作/数据都放在程序目录内）。</summary>
    public static string DataDir => Path.Combine(ExeDir, "data");

    /// <summary>下载/安装临时目录：exe 旁 data\tmp\（原 %TEMP% 下的散落临时文件）。</summary>
    public static string TempDir => Path.Combine(DataDir, "tmp");

    /// <summary>崩溃/诊断日志统一放 data\ 下（原 %APPDATA%\EfootballBot\*.log）。</summary>
    public static string ErrorLogPath(string fileName) => Path.Combine(DataDir, fileName);

    /// <summary>老版数据目录 %APPDATA%\EfootballBot（迁移源，只读不再写入）。</summary>
    private static string LegacyAppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EfootballBot");

    /// <summary>一次性把老 %APPDATA%\EfootballBot 的持久数据搬到 exe 旁 data\
    /// （按文件"缺则拷"，幂等；两边同名时以新位置为准）。失败静默——迁移不成功时
    /// 新位置无配置文件，程序按默认配置运行，不影响功能。</summary>
    public static void MigrateLegacyData()
    {
        try
        {
            if (!Directory.Exists(LegacyAppDataDir)) return;
            Directory.CreateDirectory(DataDir);
            foreach (var file in Directory.GetFiles(LegacyAppDataDir))
            {
                var target = Path.Combine(DataDir, Path.GetFileName(file));
                if (!File.Exists(target))
                    File.Copy(file, target);
            }
        }
        catch { /* 迁移失败不影响使用 */ }
    }

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
