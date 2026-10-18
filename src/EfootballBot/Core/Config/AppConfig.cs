using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using EfootballBot.Core.System;
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
    public int HumanJitterMs { get; set; } = 90;    // 随机人为抖动
    // 非必须立即的连点（比赛中挂机按 A、赛后弹窗确认）使用闭区间随机等待，降低行为机械感
    public int IdleTapMinMs { get; set; } = 1000;
    public int IdleTapMaxMs { get; set; } = 4000;

    /// <summary>
    /// 拟人节奏：按键时长/间隔改用对数正态分布随机化（真人反应时间的经典分布），
    /// 并按小概率插入「犹豫」停顿。关闭时退回固定值 ± HumanJitterMs 的均匀抖动。
    /// </summary>
    public bool HumanPacingEnabled { get; set; } = true;

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

/// <summary>全局热键配置（低级钩子实现：支持键盘任意键与鼠标键，可不带修饰键）。
/// 主热键语义：第一次按=暂停，暂停中再按=彻底停止；恢复热键：从暂停恢复。</summary>
public sealed class HotkeyConfig
{
    /// <summary>虚拟键码（VK），默认 Q。</summary>
    public int VirtualKey { get; set; } = 0x51; // VK_Q

    /// <summary>主热键是否为鼠标键（true 时 VirtualKey 为鼠标键码：1=左 2=右 4=中 5=X1 6=X2）。</summary>
    public bool IsMouse { get; set; } = false;

    /// <summary>修饰键位：位掩码，Alt=1 Ctrl=2 Shift=4 Win=8。默认 Ctrl+Shift。</summary>
    public int Modifiers { get; set; } = 2 | 4; // Ctrl + Shift

    /// <summary>是否启用全局热键。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>恢复热键虚拟键码，默认 R。</summary>
    public int ResumeVirtualKey { get; set; } = 0x52; // VK_R

    /// <summary>恢复热键是否为鼠标键。</summary>
    public bool ResumeIsMouse { get; set; } = false;

    /// <summary>恢复热键修饰键，默认 Ctrl+Shift。</summary>
    public int ResumeModifiers { get; set; } = 2 | 4;

    /// <summary>把一组键规格格式化为可读字符串，如「Ctrl+Shift+Q」「鼠标左键」。</summary>
    public static string SpecDisplay(int code, int modifiers, bool isMouse)
    {
        if (isMouse)
        {
            var name = code switch { 1 => "鼠标左键", 2 => "鼠标右键", 4 => "鼠标中键", 5 => "鼠标侧键1", 6 => "鼠标侧键2", _ => $"鼠标键{code}" };
            var sb = new global::System.Text.StringBuilder();
            if ((modifiers & 8) != 0) sb.Append("Win+");
            if ((modifiers & 2) != 0) sb.Append("Ctrl+");
            if ((modifiers & 4) != 0) sb.Append("Shift+");
            if ((modifiers & 1) != 0) sb.Append("Alt+");
            sb.Append(name);
            return sb.ToString();
        }
        var sb2 = new global::System.Text.StringBuilder();
        if ((modifiers & 8) != 0) sb2.Append("Win+");
        if ((modifiers & 2) != 0) sb2.Append("Ctrl+");
        if ((modifiers & 4) != 0) sb2.Append("Shift+");
        if ((modifiers & 1) != 0) sb2.Append("Alt+");
        sb2.Append(KeyName(code));
        return sb2.ToString();
    }

    /// <summary>主热键（暂停/停止）显示。</summary>
    public string DisplayString() => SpecDisplay(VirtualKey, Modifiers, IsMouse);

    /// <summary>恢复热键显示。</summary>
    public string ResumeDisplayString() => SpecDisplay(ResumeVirtualKey, ResumeModifiers, ResumeIsMouse);

    private static string KeyName(int vk) => vk switch
    {
        >= 0x41 and <= 0x5A => ((char)vk).ToString(), // A-Z
        >= 0x30 and <= 0x39 => ((char)vk).ToString(), // 0-9
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",       // F1-F24
        0x0D => "Enter", 0x1B => "Esc", 0x09 => "Tab", 0x20 => "空格",
        0x21 => "PageUp", 0x22 => "PageDown", 0x23 => "End", 0x24 => "Home",
        0x2D => "Insert", 0x2E => "Delete",
        _ => $"0x{vk:X2}",
    };
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

/// <summary>
/// 模块回退开关：每个升级过的内部模块保留一条手动退路，全部默认走新版实现。
/// 更改在下次开始挂机时生效（Worker 每次 start 用最新配置重建引擎，不做运行中热切换）。
/// </summary>
public sealed class ModuleFallbackConfig
{
    /// <summary>true = 画面捕获退回旧 SharpDX 实现（新 Vortice 实现出现黑屏/黑画面时勾选）。</summary>
    public bool UseSharpDxCapture { get; set; } = false;

    /// <summary>true = OCR 用原版 fp32 模型（默认用 int8 提速版；int8 模型缺失或读取异常时同样自动回退 fp32）。</summary>
    public bool UseFp32Ocr { get; set; } = false;

    /// <summary>true = OCR 关闭高分辨率降采样（4K 级画面默认压缩到 1080p 再识别；高分屏识别变差时勾选）。</summary>
    public bool DisableOcrDownscale { get; set; } = false;

    /// <summary>true = 虚拟手柄改用 HIDMaestro 驱动（实验功能）；默认 false = ViGEmBus。
    /// 注意与其它项语义相反：这是「启用新版」，勾上才算切换，取消勾选即回 ViGEmBus。</summary>
    public bool EnableHidMaestro { get; set; } = false;

    /// <summary>true = 鼠标点击关闭拟人移动轨迹（回退单步跳变到目标位置）。</summary>
    public bool DisableMouseTrajectory { get; set; } = false;

    /// <summary>true = 挂机异常停止时不自动保存画面截图留档。</summary>
    public bool DisableFailureDumps { get; set; } = false;
}

/// <summary>手柄隔离（HidHide）配置。驱动侧持久化到注册表，此处仅存 UI 快照与回滚清单。</summary>
public sealed class HidHideConfig
{
    /// <summary>已应用隔离（虚拟手柄已加入黑名单）。默认开启：首次由提权子进程自动完成配置。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 挂机启动时自动禁用真实手柄以释放 XInput 玩家 1（设备管理器级禁用，
    /// 关闭本程序后自动恢复）。注意 HidHide 黑名单路线对 XInput 无效（见 PhysicalPadControl）。
    /// </summary>
    public bool HidePhysicalGamepad { get; set; } = true;

    /// <summary>被禁用的真实手柄设备节点实例路径（pad-hide 提权任务先记录后禁用；恢复依据）。</summary>
    public List<string> DisabledPadDevs { get; set; } = new();

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

/// <summary>
/// 我的联赛道具自动购买配置（名称与游戏内「我的联赛物品」页一一对应）。
/// 经验值加成档位 2x/3x/4x；状态加成档位 普通+/好+/杰出。
/// 每次挂机运行中满足一次即不再购买（道具有效场数覆盖多场比赛）。
/// </summary>
public sealed class MyLeagueConfig
{
    /// <summary>购买经验值加成。</summary>
    public bool BuyExpBoost { get; set; } = false;

    /// <summary>经验值加成档位：2x / 3x / 4x。</summary>
    public string ExpBoostLevel { get; set; } = "2x";

    /// <summary>购买教练强化。</summary>
    public bool BuyCoachBoost { get; set; } = false;

    /// <summary>购买状态加成。</summary>
    public bool BuyConditionBoost { get; set; } = false;

    /// <summary>状态加成档位：普通+ / 好+ / 杰出。</summary>
    public string ConditionLevel { get; set; } = "普通+";

    /// <summary>
    /// 比赛难度（必选项，与游戏内「选择赛事等级」一一对应）：
    /// 新手 / 业余 / 标准 / 职业 / 顶尖球员 / 超级球星 / 传奇。
    /// </summary>
    public string Difficulty { get; set; } = "新手";

    /// <summary>球员满级提示开关：开启时检测"满级"字样，计数并在日志提醒换人。</summary>
    public bool NotifyPlayerMaxLevel { get; set; } = true;

    /// <summary>完成场次后自动停止（本次挂机累计，含关键比赛）。0 = 无限跑。</summary>
    public int StopAfterMatches { get; set; } = 0;

    /// <summary>完成关键比赛场次后自动停止（本次挂机累计）。0 = 无限跑。</summary>
    public int StopAfterKeyMatches { get; set; } = 0;
}

/// <summary>游戏路径记忆（赛前快捷设置）：各服 Settings.exe 上次定位结果。
/// 国服不走 Steam 回退，游戏未启动时靠这条记忆兜底（首次定位成功后落盘）。</summary>
public sealed class GamePathsConfig
{
    /// <summary>国际服 Settings.exe 上次定位到的完整路径。</summary>
    public string IntlSettingsTool { get; set; } = "";

    /// <summary>国服 Settings.exe 上次定位到的完整路径。</summary>
    public string CnSettingsTool { get; set; } = "";
}

/// <summary>
/// 国际服 AI 天梯联赛（eFootball™联赛·对阵AI类别）挂机配置。
/// 组别 10 最低、1 最高；「达到组别停止」按主页左上角组别数字判定（数字 ≤ 设定值即达标）。
/// </summary>
public sealed class AiLadderConfig
{
    /// <summary>达到该组别后自动停止（1~10；0 = 不启用，一直打到完成场次或手动停止）。</summary>
    public int StopAtDivision { get; set; } = 0;

    /// <summary>完成场次后自动停止（本次挂机累计，无关键比赛概念）。0 = 无限跑。</summary>
    public int StopAfterMatches { get; set; } = 0;
}

/// <summary>
/// 周活动挂机配置（国际服）。赛事等级与自动控制是活动设置浮层的核验项；
/// 自动控制强制开、弹性自动控制强制关（硬编码，不设配置）。
/// </summary>
public sealed class WeeklyConfig
{
    /// <summary>赛事等级（7 档与游戏内「活动设置」下拉一致）。默认传奇（加成最高）。</summary>
    public string EventLevel { get; set; } = "传奇";

    /// <summary>完成场次后自动停止（本次挂机累计）。0 = 无限跑（默认：刷到主要奖励完成）。</summary>
    public int StopAfterMatches { get; set; } = 0;
}

/// <summary>
/// 国服周活动挂机配置（实况足球在线「比赛活动」）。活动每周轮换多种形态：
/// 杯赛型（冠军赛，全程固定 7 场：小组赛×3 + 1/8决赛 + 1/4决赛 + 半决赛 + 决赛）、
/// 巡回赛型（球星训练等，场次不定刷积分）与挑战型（挑战清单逐卡挑战）。
/// 前往比赛后的「选择比赛难度」页按 Difficulty 自动核对改选。
/// </summary>
public sealed class CnWeeklyConfig
{
    /// <summary>完成场次后自动停止（本次挂机累计）。0 = 无限跑（默认；巡回赛型刷到主要奖励打满为宜）。</summary>
    public int StopAfterMatches { get; set; } = 0;

    /// <summary>比赛难度（前往比赛后的「选择比赛难度」页自动核对）：与我的联赛同表
    /// 新手 / 业余 / 标准 / 职业 / 顶尖球员 / 超级球星 / 传奇。读不到当前所选时按游戏默认直接确认。</summary>
    public string Difficulty { get; set; } = "标准";
}

/// <summary>
/// 后台停靠配置：挂机时把游戏窗口整体移到所有显示器之外（纯窗口坐标操作，
/// 不碰游戏进程），主屏幕完全看不到、点击其他软件也不会跳出；停止挂机自动还原。
/// 仅兼容 WGC/GDI 窗口捕获（DXGI 抓桌面区域，窗口不在屏幕上即失效）。
/// </summary>
public sealed class BackgroundParkingConfig
{
    /// <summary>是否启用后台停靠（挂机开始时停靠，停止时还原原位置）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>看门狗校验间隔毫秒：游戏自行挪回窗口时重新移出屏幕。</summary>
    public int WatchdogIntervalMs { get; set; } = 2500;
}

/// <summary>
/// 作息调度配置：模拟真人作息——每天只在活跃时段挂机、每日累计时长上限、
/// 启停时间每日随机漂移、运行中偶尔短暂离开。服务端行为检测的最强信号是
/// 7×24 在线与恒定节奏，本调度器直接消除前者。
/// </summary>
public sealed class ScheduleConfig
{
    /// <summary>是否启用作息调度（关闭 = 引擎持续运行）。</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>每日活跃时段开始（HH:mm，本地时间）。支持跨午夜（如 22:00–02:00）。</summary>
    public string StartTime { get; set; } = "09:00";

    /// <summary>每日活跃时段结束（HH:mm）。</summary>
    public string EndTime { get; set; } = "23:30";

    /// <summary>每日累计挂机时长上限（小时，0 = 不限制）。</summary>
    public double DailyCapHours { get; set; } = 6;

    /// <summary>启停时间每日随机漂移幅度（分钟）：真人不会每天同一秒上线。</summary>
    public int DriftMinutes { get; set; } = 20;
}

public sealed class AppConfig
{
    public string Theme { get; set; } = "Dark"; // Dark / Light / DeepBlue
    public string? OcrLanguage { get; set; }
    /// <summary>OCR 识别引擎：RapidV6（PP-OCRv6 深度学习，默认）或 Windows（内置 OCR 回退）。下次启动挂机生效。</summary>
    public OcrEngineKind OcrEngine { get; set; } = OcrEngineKind.RapidV6;
    public bool AutoLaunchGame { get; set; }
    /// <summary>画面捕获后端：WGC 窗口捕获（默认，支持后台挂机/窗口被遮挡；已绑定真实 GPU 适配器）或 DXGI 桌面复制（高性能但窗口须可见）。</summary>
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

    /// <summary>应用内运行日志显示过滤：All=全部 / Key=仅关键流程（Success 绿字）/ Warn=仅报错与提醒（Warn+Error）。仅影响 UI 显示，文件日志始终全量。</summary>
    public string RuntimeLogFilter { get; set; } = "All";

    /// <summary>日志历史查看器显示过滤：取值同 RuntimeLogFilter。</summary>
    public string HistoryLogFilter { get; set; } = "All";

    // —— 全局停止热键 ——
    public HotkeyConfig Hotkey { get; set; } = new();
    public UpdateConfig Update { get; set; } = new();

    // —— 模块回退（新版实现出问题时的手动退路，更新页可改）——
    public ModuleFallbackConfig ModuleFallback { get; set; } = new();

    // —— 手柄隔离（HidHide）——
    public HidHideConfig HidHide { get; set; } = new();

    // —— 我的联赛道具 ——
    public MyLeagueConfig MyLeague { get; set; } = new();

    // —— 周活动（国际服）——
    public WeeklyConfig Weekly { get; set; } = new();

    // —— AI天梯联赛（国际服，eFootball™联赛·对阵AI类别）——
    public AiLadderConfig AiLadder { get; set; } = new();

    // —— 周活动（国服，比赛活动冠军赛）——
    public CnWeeklyConfig CnWeekly { get; set; } = new();

    // —— 我的联赛（国服，与国际服同款玩法、独立配置段互不影响）——
    public MyLeagueConfig CnMyLeague { get; set; } = new();

    // —— 游戏路径记忆（赛前快捷设置）——
    public GamePathsConfig GamePaths { get; set; } = new();

    // —— 防检测（后台停靠 / 作息调度）——
    public BackgroundParkingConfig BackgroundParking { get; set; } = new();
    public ScheduleConfig Schedule { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = global::System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>配置目录：exe 旁 data\（原 %APPDATA%\EfootballBot，AppPaths.MigrateLegacyData 已迁移老数据）。
    /// 配置跟程序文件夹走，拷贝文件夹即完整迁移，不在 C 盘用户目录留数据。</summary>
    public static string ConfigDir { get; } = AppPaths.DataDir;

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
        catch
        {
            // 配置损坏（写入中途崩溃/被杀软占用截断）：把坏文件改名留档再回退默认启动，
            // 防止「静默回退默认值 → 运行中任何一次 Save 用默认值覆盖用户全部配置」后无从排查
            try
            {
                var bad = ConfigPath + ".bad";
                if (File.Exists(bad)) File.Delete(bad);
                File.Move(ConfigPath, bad);
            }
            catch { /* 留档失败不阻塞启动 */ }
        }
        return new AppConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        // 原子替换：先写临时文件再 Move 覆盖，绝不产生写了一半的截断 JSON
        //（截断 → 下次启动静默回退默认 → 用户配置被默认值覆盖，数据丢失级故障）
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
        File.Move(tmp, ConfigPath, overwrite: true);
    }
}
