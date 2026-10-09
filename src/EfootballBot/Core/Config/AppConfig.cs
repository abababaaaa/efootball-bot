using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Config;

/// <summary>画面捕获后端模式。</summary>
public enum CaptureMode
{
    /// <summary>DXGI Desktop Duplication：直接读桌面合成画面，性能高，但窗口不能被遮挡/最小化（无法后台挂机）。</summary>
    Dxgi,
    /// <summary>Windows Graphics Capture：窗口专属捕获，被遮挡/后台也能抓（后台挂机默认）。</summary>
    Wgc,
}

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

/// <summary>ONNX Runtime 推理执行提供者配置。</summary>
public sealed class InferenceConfig
{
    /// <summary>优先执行提供者：Auto（CUDA→DirectML→CPU 依次探测）/ CUDA / DML / CPU。</summary>
    public string PreferredProvider { get; set; } = "Auto";
}

/// <summary>YOLO 目标检测配置。</summary>
public sealed class YoloConfig
{
    /// <summary>模型就位（models/yolo/yolo26n.onnx）时是否启用目标检测。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>检测置信度阈值。</summary>
    public double ScoreThreshold { get; set; } = 0.25;

    /// <summary>NMS IoU 阈值（仅非端到端导出格式使用）。</summary>
    public double NmsIou { get; set; } = 0.45;
}

/// <summary>训练数据采集配置：把观察帧按间隔存为 JPG（captures\日期\），供 YOLO 标注训练用。</summary>
public sealed class FrameDumpConfig
{
    /// <summary>是否开启自动存帧。</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>存帧间隔毫秒（≥2000，过密会产生大量近似重复帧）。</summary>
    public int IntervalMs { get; set; } = 10000;
}

/// <summary>全局停止热键配置。</summary>
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

/// <summary>手柄隔离（HidHide）配置。驱动侧持久化到注册表，此处仅存 UI 快照与回滚清单。</summary>
public sealed class HidHideConfig
{
    /// <summary>已应用隔离（虚拟手柄已加入黑名单）。默认开启：首次由提权子进程自动完成配置。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>同时隐藏真实手柄（默认开；开启期间 HID 层应用看不到真实手柄，可随时关闭）。</summary>
    public bool HidePhysicalGamepad { get; set; } = true;

    /// <summary>白名单游戏的完整镜像路径快照（应用隔离时快照，游戏未运行时无法更新）。</summary>
    public string GameImagePath { get; set; } = "";

    /// <summary>本次隔离实际加入的黑名单条目快照（解除隔离时精确回滚）。</summary>
    public List<string> AppliedBlacklist { get; set; } = new();

    /// <summary>
    /// 老默认迁移：从未成功应用过隔离（AppliedBlacklist 为空，即旧出厂 Enabled=false 配置）
    /// → 翻成新默认（隔离开 + 真实手柄隐藏开）。成功应用/解除过一次后不再干预，尊重用户选择。
    /// 只改内存，首次 Save 时落盘（同 TimingConfig 迁移模式）。
    /// </summary>
    public void MigrateDefaults()
    {
        if (AppliedBlacklist.Count == 0)
        {
            Enabled = true;
            HidePhysicalGamepad = true;
        }
    }
}

public sealed class AppConfig
{
    public string Theme { get; set; } = "Dark"; // Dark / Light / DeepBlue
    public string? GameExePath { get; set; }
    public string? OcrLanguage { get; set; }
    /// <summary>OCR 识别引擎：RapidV6（PP-OCRv6 深度学习，默认）或 Windows（内置 OCR 回退）。下次启动挂机生效。</summary>
    public OcrEngineKind OcrEngine { get; set; } = OcrEngineKind.RapidV6;
    public bool AutoLaunchGame { get; set; }
    public bool KeepGameOnTop { get; set; } = false; // 兜底模式：游戏窗口置顶（不激活）
    /// <summary>画面捕获后端：WGC 窗口捕获（默认，支持后台挂机/窗口被遮挡）或 DXGI（高性能但窗口须可见）。</summary>
    public CaptureMode CaptureMode { get; set; } = CaptureMode.Wgc;
    public TimingConfig Timing { get; set; } = new();

    // —— 推理（ONNX Runtime EP 降级 + YOLO 目标检测）——
    public InferenceConfig Inference { get; set; } = new();
    public YoloConfig Yolo { get; set; } = new();

    // —— 训练数据采集 ——
    public FrameDumpConfig FrameDump { get; set; } = new();

    // —— 设置面板：结束行为 ——
    public bool AutoCloseWhenDone { get; set; } = false;

    // —— 设置面板：性能 / 内存 ——
    public int PreviewIntervalMs { get; set; } = 300;

    // —— 设置面板：日志 ——
    public bool FileLogEnabled { get; set; } = true;
    public int MaxLogFiles { get; set; } = 10;
    /// <summary>日志保留天数：启动时删除超过该天数的旧日志。</summary>
    public int LogRetentionDays { get; set; } = 7;
    public string FileLogLevel { get; set; } = "Debug";

    // —— UI 偏好 ——
    public bool ShowRuntimeLog { get; set; } = true;

    // —— 全局停止热键 ——
    public HotkeyConfig Hotkey { get; set; } = new();
    public UpdateConfig Update { get; set; } = new();

    // —— 手柄隔离（HidHide）——
    public HidHideConfig HidHide { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = global::System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string ConfigDir { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "\\EfootballBot";

    public static string ConfigPath => ConfigDir + "\\config.json";

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOpts) ?? new AppConfig();
                cfg.Timing.MigrateIdleTapDefaults();
                cfg.HidHide.MigrateDefaults();
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
