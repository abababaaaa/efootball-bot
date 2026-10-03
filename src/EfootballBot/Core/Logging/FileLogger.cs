using System.IO;
using System.Text;
using EfootballBot.Core.Bot;

namespace EfootballBot.Core.Logging;

/// <summary>
/// 单例文件日志写入器。启动时创建 logs\bot-YYYYMMDD-HHmmss.log，
/// 接收 BotEngine.Log 事件写入（含 Debug 级），UI 实时日志仍保持跳过 Debug。
/// 旧日志在启动时按 MaxLogFiles 清理。
/// </summary>
public sealed class FileLogger : IDisposable
{
    public static FileLogger Instance { get; } = new();

    /// <summary>当前活跃日志文件完整路径。</summary>
    public string CurrentLogPath { get; private set; } = "";

    /// <summary>logs 目录（AppContext.BaseDirectory\logs）。</summary>
    public static string LogDir => Path.Combine(AppContext.BaseDirectory, "logs");

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
    public void Initialize(bool enabled, string minLevel, int maxFiles)
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
        CleanupOldLogs(maxFiles);

        string filename = $"bot-{DateTime.Now:yyyyMMdd-HHmmss}.log";
        CurrentLogPath = Path.Combine(LogDir, filename);
        _writer = new StreamWriter(CurrentLogPath, append: false, encoding: Encoding.UTF8)
        {
            AutoFlush = true
        };

        lock (_lock)
        {
            _writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [INFO]   === 日志会话开始：{filename} ===");
            _writer.Flush();
        }
    }

    /// <summary>写一条日志，级别低于 minLevel 的跳过。线程安全。</summary>
    public void Write(string message, LogLevel level)
    {
        if (!_enabled) return;
        if (_writer is null) return;
        // LogLevel: Info=0, Success=1, Warn=2, Error=3, Debug=4（数值越大越详细）
        // minLevel 语义：等于/小于它的写，大于它（更详细）的跳过
        if (level > _minLevel) return;

        // BotEngine.LogInternal 传入的 message 已带 [HH:mm:ss] 前缀，前面补完整日期
        string fullMsg = message;
        if (fullMsg.Length > 11 && fullMsg.StartsWith("[") && fullMsg[9] == ']')
        {
            fullMsg = fullMsg[11..].TrimStart();
        }

        string levelTag = level switch
        {
            LogLevel.Debug => "[DEBUG] ",
            LogLevel.Info => "[INFO]  ",
            LogLevel.Warn => "[WARN]  ",
            LogLevel.Error => "[ERROR] ",
            LogLevel.Success => "[OK]    ",
            _ => "[?????] ",
        };

        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {levelTag}{fullMsg}";

        lock (_lock)
        {
            try { _writer.WriteLine(line); }
            catch { /* 磁盘满/句柄关闭等异常静默 */ }
        }
    }

    private static LogLevel ParseLevel(string name) => name?.Trim().ToLowerInvariant() switch
    {
        "debug" => LogLevel.Debug,
        "info" => LogLevel.Info,
        "warn" or "warning" => LogLevel.Warn,
        "error" => LogLevel.Error,
        _ => LogLevel.Debug,
    };

    private static void CleanupOldLogs(int maxFiles)
    {
        try
        {
            if (!Directory.Exists(LogDir)) return;
            var files = Directory.GetFiles(LogDir, "*.log")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTime)
                .Skip(Math.Max(0, maxFiles))
                .ToList();
            foreach (var f in files)
            {
                try { f.Delete(); } catch { }
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
