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

    private static string _profile = "";

    /// <summary>
    /// 实例档案名（多开用，如 "A"/"B"；空 = 默认实例）。
    /// 每个档案有独立的 data\profile-{名}\ 与独立日志子目录，单实例锁/管道也按档案区分，
    /// 因此同一台机器可以并行挂两个账号。必须在任何配置/路径访问之前设置
    /// （由启动参数 --profile 完成）。
    /// </summary>
    public static string Profile => _profile;

    /// <summary>设置实例档案名（只保留字母/数字/下划线/短横，长度上限 24；非法字符直接丢弃）。</summary>
    public static void SetProfile(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            _profile = "";
            return;
        }
        // global::System.Text：本文件命名空间是 EfootballBot.Core.System，
        // 裸 System.Text 会被解析成 EfootballBot.Core.System.Text（本仓库已知遮蔽陷阱）
        var sb = new global::System.Text.StringBuilder(24);
        foreach (char c in name.Trim())
        {
            if (char.IsLetterOrDigit(c) || c is '_' or '-') sb.Append(c);
            if (sb.Length >= 24) break;
        }
        _profile = sb.ToString();
    }

    /// <summary>logs 目录：固定在 exe 旁，拷贝整个文件夹/单 exe 时日志跟着走。
    /// 多开时每个档案单独一个子目录，两个实例的日志不会混在一起排障。</summary>
    public static string LogDir => _profile.Length == 0
        ? Path.Combine(ExeDir, "logs")
        : Path.Combine(ExeDir, "logs", $"profile-{_profile}");

    /// <summary>可写数据目录：exe 旁 data\（config、崩溃日志、停靠记录等，原 %APPDATA%\EfootballBot）。
    /// 全部收在程序文件夹内——拷贝整个文件夹即完整迁移，不在 C 盘用户目录留数据
    /// （2026-10-11 用户要求：所有操作/数据都放在程序目录内）。
    /// 多开时按档案隔离到 data\profile-{名}\，各账号配置互不影响。</summary>
    public static string DataDir => _profile.Length == 0
        ? Path.Combine(ExeDir, "data")
        : Path.Combine(ExeDir, "data", $"profile-{_profile}");

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
    /// 按优先级查找随包资源文件：
    /// ① 单文件解压目录（BaseDirectory）→ ② exe 同级目录（模型外置发布时在这里）
    /// → ③ data\（首启按需下载的模型）。都不存在返回 null。
    /// </summary>
    public static string? FindBundledFile(params string[] relativeParts)
    {
        var rel = Path.Combine(relativeParts);
        var extracted = Path.Combine(AppContext.BaseDirectory, rel);
        if (File.Exists(extracted)) return extracted;
        var beside = Path.Combine(ExeDir, rel);
        if (File.Exists(beside)) return beside;
        var downloaded = Path.Combine(DataDir, rel);
        return File.Exists(downloaded) ? downloaded : null;
    }
}
