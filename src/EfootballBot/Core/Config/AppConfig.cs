using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Config;

public sealed class TimingConfig
{
    public int TapHoldMs { get; set; } = 130;       // 单键保持时长
    public int TapGapMs { get; set; } = 220;        // 按键间隔
    public int DpadHoldMs { get; set; } = 140;
    public int OcrIntervalMs { get; set; } = 900;   // 屏幕识别间隔
    public int PageTimeoutS { get; set; } = 45;     // 单页面最长等待
    public int LoadingTimeoutS { get; set; } = 120;
    public int MatchTimeoutMin { get; set; } = 30;  // 单场比赛最长等待（实测整场约20~25分钟）
    public int ResultAKeyCount { get; set; } = 24;  // 结算页连按 A 的次数
    public int StuckRetry { get; set; } = 3;        // 卡死重试次数
    public int HumanJitterMs { get; set; } = 90;    // 随机人为抖动
    // 非必须立即的连点（比赛中挂机按 A、赛后弹窗确认）使用闭区间随机等待，降低行为机械感
    public int IdleTapMinMs { get; set; } = 1000;
    public int IdleTapMaxMs { get; set; } = 4000;

    /// <summary>
    /// 解析秒数文本（支持小数）为毫秒并校验范围。
    /// 规则：0.2s ≤ min ≤ max ≤ 30s。先转毫秒整数再比较，规避 0.2 等二进制浮点边界误差。
    /// </summary>
    public static bool TryParseSeconds(
        string? minText, string? maxText,
        out int minMs, out int maxMs, out string error)
    {
        minMs = 0;
        maxMs = 0;
        error = "";

        if (!double.TryParse(minText, NumberStyles.Float, CultureInfo.InvariantCulture, out double minSec) ||
            !double.TryParse(maxText, NumberStyles.Float, CultureInfo.InvariantCulture, out double maxSec))
        {
            error = "请输入有效数字";
            return false;
        }

        int minRaw = (int)Math.Round(minSec * 1000);
        int maxRaw = (int)Math.Round(maxSec * 1000);

        if (minRaw < 200)
        {
            error = "最小不能小于 0.2 秒";
            return false;
        }
        if (maxRaw > 30000)
        {
            error = "最大不能大于 30 秒";
            return false;
        }
        if (minRaw > maxRaw)
        {
            error = "最小不能大于最大";
            return false;
        }

        minMs = minRaw;
        maxMs = maxRaw;
        return true;
    }

    /// <summary>
    /// 一次性迁移：旧版出厂默认 1~10s（此前无 UI，普通用户配置必为此值）收紧为 1~4s。
    /// 任何非旧默认对的值（用户手改过 json）保持不动。
    /// </summary>
    public void MigrateIdleTapDefaults()
    {
        if (IdleTapMinMs == 1000 && IdleTapMaxMs == 10000)
        {
            IdleTapMinMs = 1000;
            IdleTapMaxMs = 4000;
        }
    }
}

public sealed class MyLeagueConfig
{
    public bool Enabled { get; set; } = true;
    public bool Buy4xExp { get; set; } = false;
    public bool BuyExcellentCondition { get; set; } = false;
    public bool BuyManagerBoost { get; set; } = false;
    public bool OnlyKeyMatches { get; set; } = false;  // 普通比赛自动弃权，只踢关键比赛
    public int TargetMatches { get; set; } = 0;        // 0 = 不限
    public bool SetAiControl { get; set; } = true;     // 赛前确认选手操作为 AI
    public string Difficulty { get; set; } = "传奇";   // 新手 / 业余 / 职业 / 超级球星 / 传奇
}

public sealed class DailyConfig
{
    public bool Enabled { get; set; }
    public bool ClaimLoginRewards { get; set; } = true; // 进入时清扫弹窗奖励
    public bool PlayAiEvents { get; set; } = true;      // 循环打对战 AI 活动
    public bool PlayDailyMinigame { get; set; }         // 每日小游戏
    public int TargetMatches { get; set; } = 0;
}

/// <summary>国际服周活动全清配置（比赛 → 活动 → 对阵AI 全部活动）。</summary>
public sealed class WeeklyConfig
{
    public bool Enabled { get; set; } = true;
    public string Difficulty { get; set; } = "传奇";   // 巡回活动设置中的赛事等级：新手/业余/职业/顶尖球员/超级球星/传奇
    public int MaxEvents { get; set; } = 12;          // 单次遍历活动卡数安全上限
}

/// <summary>国服（网易 eFootballOnline）配置。</summary>
public sealed class CnConfig
{
    /// <summary>国服游戏进程名（窗口标题：实况足球在线）。</summary>
    public const string ProcessName = "eFootballOnline";

    public bool Enabled { get; set; } = true;
    public bool ClaimLoginRewards { get; set; } = true; // 进入时清扫登录/弹窗奖励
    public int DailyLoopLimit { get; set; } = 0;        // 日常活动循环场次上限，0 = 不限
}

/// <summary>国际服排队执行配置：日常跑完后是否自动接我的联赛。</summary>
public sealed class QueueConfig
{
    public bool AutoChainMyLeague { get; set; } = false; // 日常跑完是否自动接联赛
    public int DailyLoopLimit { get; set; } = 0;         // 日常活动最多跑几轮，0 = 不限
}

/// <summary>用户向导学习到的屏幕识别数据（补充默认锚点）。</summary>
public sealed class HotkeyConfig
{
    /// <summary>虚拟键码（VK），默认 Q。</summary>
    public int VirtualKey { get; set; } = 0x51; // VK_Q

    /// <summary>修饰键位：位掩码，Alt=1 Ctrl=2 Shift=4 Win=8。默认 Ctrl+Shift。</summary>
    public int Modifiers { get; set; } = 2 | 4; // Ctrl + Shift

    /// <summary>是否启用全局停止热键。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>把配置格式化为可读字符串，如「Ctrl+Shift+Q」。</summary>
    public string DisplayString()
    {
        // 用 System.Windows.Input.KeyInterop.VirtualKeyFromKey 对应的反向映射
        // 我们只存储虚拟键码，显示时硬编码常用字符
        var sb = new global::System.Text.StringBuilder();
        if ((Modifiers & 8) != 0) sb.Append("Win+");
        if ((Modifiers & 2) != 0) sb.Append("Ctrl+");
        if ((Modifiers & 4) != 0) sb.Append("Shift+");
        if ((Modifiers & 1) != 0) sb.Append("Alt+");

        string keyName = VirtualKey switch
        {
            >= 0x41 and <= 0x5A => ((char)VirtualKey).ToString(), // A-Z
            >= 0x30 and <= 0x39 => ((char)VirtualKey).ToString(), // 0-9
            0x0D => "Enter",
            0x1B => "Esc",
            0x09 => "Tab",
            0x73 => "F4",
            0x74 => "F5",
            0x75 => "F6",
            0x76 => "F7",
            0x77 => "F8",
            0x78 => "F9",
            0x79 => "F10",
            0x7A => "F11",
            0x7B => "F12",
            0x70 => "F1",
            0x71 => "F2",
            0x72 => "F3",
            _ => $"0x{VirtualKey:X2}",
        };
        sb.Append(keyName);
        return sb.ToString();
    }
}

/// <summary>自动更新配置（GitHub Releases）。</summary>
public sealed class UpdateConfig
{
    /// <summary>GitHub 仓库 owner/repo 格式（如 "myname/efootball-bot"）。</summary>
    public string Repository { get; set; } = "";

    /// <summary>启动时自动检查更新。</summary>
    public bool CheckOnStartup { get; set; } = true;

    /// <summary>使用 pre-release tag。</summary>
    public bool IncludePrerelease { get; set; } = false;

    /// <summary>发现新版本后自动下载并安装（无需手动点立即更新）。</summary>
    public bool AutoDownloadInstall { get; set; } = false;
}

public sealed class ScreenCalibration
{
    public List<string> LearnedKeywords { get; set; } = new();
    public string? SnapshotFile { get; set; }
}

public sealed class AppConfig
{
    public string Theme { get; set; } = "Dark"; // Dark / Light / DeepBlue
    public string? GameExePath { get; set; }
    public string? OcrLanguage { get; set; }
    public bool AutoLaunchGame { get; set; }
    public bool KeepGameOnTop { get; set; } = false; // 兜底模式：游戏窗口置顶（不激活）
    public TimingConfig Timing { get; set; } = new();
    public MyLeagueConfig MyLeague { get; set; } = new();
    public DailyConfig Daily { get; set; } = new();
    public WeeklyConfig Weekly { get; set; } = new();
    public CnConfig Cn { get; set; } = new();
    public QueueConfig Queue { get; set; } = new();
    public Dictionary<string, ScreenCalibration> Calibration { get; set; } = new();

    // —— 设置面板：结束行为 ——
    public bool AutoCloseWhenDone { get; set; } = false;

    // —— 设置面板：性能 / 内存 ——
    public bool PeriodicGc { get; set; } = true;
    public int PreviewIntervalMs { get; set; } = 300;
    /// <summary>挂机开始时自动把游戏窗口化为 1280×720（实测该游戏独占全屏抵抗一切外部窗口修改，默认关闭）。</summary>
    public bool AutoWindowizeOnStart { get; set; } = false;

    // —— 设置面板：日志 ——
    public bool FileLogEnabled { get; set; } = true;
    public int MaxLogFiles { get; set; } = 10;
    public string FileLogLevel { get; set; } = "Debug";

    // —— UI 偏好 ——
    public bool ShowRuntimeLog { get; set; } = true;

    // —— 全局停止热键 ——
    public HotkeyConfig Hotkey { get; set; } = new();
    public UpdateConfig Update { get; set; } = new();

    public ScreenCalibration? Calib(GameScreen s)
        => Calibration.TryGetValue(s.ToString(), out var c) ? c : null;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = global::System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string ConfigDir { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "\\EfootballBot";

    public static string ConfigPath => ConfigDir + "\\config.json";
    public static string CalibDir => ConfigDir + "\\calibration";

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOpts) ?? new AppConfig();
                cfg.Timing.MigrateIdleTapDefaults();
                return cfg;
            }
        }
        catch { /* 配置损坏时回退默认 */ }
        return new AppConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
    }
}
