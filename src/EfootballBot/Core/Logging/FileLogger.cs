using System.IO;
using System.Text;
using EfootballBot.Core.Bot;
using EfootballBot.Core.System;

namespace EfootballBot.Core.Logging;

/// <summary>
/// 单例文件日志写入器。启动时创建 logs\bot-YYYYMMDD-HHmmss-PID.yml（YAML / UTF-8 带 BOM，
/// 记事本可直接双击打开），接收 BotEngine.Log 事件写入（含 Debug 级），UI 实时日志仍保持跳过 Debug。
/// 旧日志在启动时按「保留天数 + 保留数量」双重条件清理（兼容清理历史 .log/.txt）。
/// </summary>
public sealed class FileLogger : IDisposable
{
    public static FileLogger Instance { get; } = new();

    /// <summary>当前活跃日志文件完整路径。</summary>
    public string CurrentLogPath { get; private set; } = "";

    /// <summary>logs 目录（exe 同级 logs\；单文件发布时 exe 在临时解压目录之外，日志不写进 %TEMP%）。</summary>
    public static string LogDir => AppPaths.LogDir;

    private StreamWriter? _writer;
    private readonly object _lock = new();
    private LogLevel _minLevel = LogLevel.Debug;
    private bool _enabled = true;

    private FileLogger() { }

    /// <summary>
    /// 初始化：根据配置创建/不创建日志文件 + 清理旧日志。
    /// 在 MainWindow 构造中调用，早于 BotEngine 创建。
    /// </summary>
    /// <param name="enabled">是否启用文件日志</param>
    /// <param name="minLevel">最低写入级别，低于此级别的跳过</param>
    /// <param name="maxFiles">保留的最大日志文件数（按创建时间倒序）</param>
    /// <param name="retentionDays">保留天数，早于该天数的日志一律删除</param>
    public void Initialize(bool enabled, string minLevel, int maxFiles, int retentionDays)
    {
        _enabled = enabled;
        _minLevel = ParseLevel(minLevel);

        lock (_lock)
        {
            _writer?.Flush();
            _writer?.Close();
            _writer = null;
        }

        if (!enabled)
        {
            CurrentLogPath = "";
            return;
        }

        Directory.CreateDirectory(LogDir);
        CleanupOldLogs(maxFiles, retentionDays);

        // 文件名带 PID：GUI 与 Worker 子进程同秒启动时不会撞同名文件
        string filename = $"bot-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.yml";
        CurrentLogPath = Path.Combine(LogDir, filename);
        try
        {
            // UTF-8 带 BOM：任何版本的记事本双击打开中文都不乱码
            _writer = new StreamWriter(CurrentLogPath, append: false,
                    encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: true))
            {
                AutoFlush = true
            };
        }
        catch
        {
            // 文件创建失败（占用/权限/路径）→ 降级为不写文件，绝不让日志问题炸掉进程
            CurrentLogPath = "";
            _writer = null;
            return;
        }

        lock (_lock)
        {
            try
            {
                _writer.WriteLine("# eFootball 挂机助手运行日志（YAML / UTF-8，记事本可直接打开）");
                _writer.WriteLine($"session: \"{filename}\"");
                _writer.WriteLine($"pid: {Environment.ProcessId}");
                _writer.WriteLine($"started: \"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\"");
                // 环境自述：用户回传日志时一眼定位版本与系统，不再靠猜
                //（2026-10-13 logs.rar 全是空白会话，连用户跑的哪个版本都无法确认）
                _writer.WriteLine(EscapeYamlScalar("version", Updater.FormatVersion(typeof(FileLogger).Assembly.GetName().Version)));
                _writer.WriteLine(EscapeYamlScalar("os", Environment.OSVersion.VersionString));
                _writer.WriteLine(EscapeYamlScalar("exe", Environment.ProcessPath ?? ""));
                _writer.WriteLine(EscapeYamlScalar("cmd", Environment.CommandLine));
                _writer.WriteLine("logs:");
                WriteYamlLine(LogLevel.Info, $"=== 日志会话开始：{filename} ===");
                _writer.Flush();
            }
            catch { }
        }
    }

    /// <summary>YAML 双引号标量转义（反斜杠/引号；路径与命令行必含反斜杠）。</summary>
    private static string EscapeYamlScalar(string key, string value) =>
        $"{key}: \"{value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ")}\"";

    /// <summary>写一条日志，级别低于 minLevel 的跳过。线程安全。</summary>
    public void Write(string message, LogLevel level)
    {
        if (!_enabled) return;
        if (_writer is null) return;
        // 严重度排序（与枚举数值顺序不一致，不能直接用 level > minLevel）：
        // Debug 最轻 → Info/Success → Warn → Error。minLevel 语义：只写严重度 ≥ minLevel 的，
        // 例如 Info 级别必须保留 Warn/Error（旧写法把它们误过滤，故障日志全丢）
        if (Severity(level) < Severity(_minLevel)) return;

        // BotEngine.LogInternal 传入的 message 已带 [HH:mm:ss] 前缀，前面补完整日期
        string fullMsg = message;
        if (fullMsg.Length > 11 && fullMsg.StartsWith("[") && fullMsg[9] == ']')
        {
            fullMsg = fullMsg[11..].TrimStart();
        }

        lock (_lock)
        {
            try { WriteYamlLine(level, fullMsg); }
            catch { /* 磁盘满/句柄关闭等异常静默 */ }
        }
    }

    private void WriteYamlLine(LogLevel level, string msg)
    {
        string levelTag = level switch
        {
            LogLevel.Debug => "DEBUG",
            LogLevel.Info => "INFO",
            LogLevel.Warn => "WARN",
            LogLevel.Error => "ERROR",
            LogLevel.Success => "OK",
            _ => "?????",
        };
        // flow mapping，一行一条；msg 做 YAML 双引号转义
        string escaped = msg.Replace("\\", "\\\\").Replace("\"", "\\\"")
                            .Replace("\r", " ").Replace("\n", " ");
        _writer?.WriteLine($"- {{time: \"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\", level: \"{levelTag}\", msg: \"{escaped}\"}}");
    }

    /// <summary>严重度等级：数字越大越严重（Debug=0 最详细最轻）。</summary>
    private static int Severity(LogLevel level) => level switch
    {
        LogLevel.Debug => 0,
        LogLevel.Info or LogLevel.Success => 1,
        LogLevel.Warn => 2,
        LogLevel.Error => 3,
        _ => 1,
    };

    private static LogLevel ParseLevel(string name) => name?.Trim().ToLowerInvariant() switch
    {
        "debug" => LogLevel.Debug,
        "info" => LogLevel.Info,
        "warn" or "warning" => LogLevel.Warn,
        "error" => LogLevel.Error,
        _ => LogLevel.Debug,
    };

    /// <summary>
    /// 双重清理：早于 retentionDays 的删除；其余按创建时间倒序只保留 maxFiles 个。
    /// 同时清理新版 .yml 与历史 .txt/.log。
    /// </summary>
    private static void CleanupOldLogs(int maxFiles, int retentionDays)
    {
        try
        {
            if (!Directory.Exists(LogDir)) return;

            var files = new[] { "*.yml", "*.txt", "*.log" }
                .SelectMany(p => Directory.GetFiles(LogDir, p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                // 只清理本程序产生的 bot- 日志，绝不动 logs 目录里的其他文件
                .Where(p => Path.GetFileName(p).StartsWith("bot-", StringComparison.OrdinalIgnoreCase))
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTime)
                .ToList();

            DateTime cutoff = DateTime.Now.AddDays(-Math.Max(1, retentionDays));

            // 超期的直接删；未超期的按数量上限保留最新的 maxFiles 个
            var fresh = new List<FileInfo>();
            foreach (var f in files)
            {
                bool expired = f.CreationTime < cutoff;
                bool overflow = fresh.Count >= Math.Max(1, maxFiles);
                if (expired || overflow)
                {
                    try { f.Delete(); } catch { }
                }
                else
                {
                    fresh.Add(f);
                }
            }
        }
        catch { /* 目录不存在/权限问题等静默 */ }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try
            {
                if (_writer is not null)
                {
                    _writer.Flush();
                    _writer.Close();
                }
            }
            catch { }
            _writer = null;
        }
        GC.SuppressFinalize(this);
    }
}
