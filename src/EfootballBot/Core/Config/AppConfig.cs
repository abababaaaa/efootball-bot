using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using EfootballBot.Core.Input;
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

/// <summary>
/// Unlimited-OCR（百度 2026 文档解析 VLM）客户端配置。模型只以 Python 服务形态存在
/// （vLLM / SGLang 均为 OpenAI 兼容接口），本程序通过 HTTP 逐帧请求。
/// </summary>
public sealed class UnlimitedOcrConfig
{
    /// <summary>OpenAI 兼容 chat/completions 地址（vLLM/SGLang 官方默认端口 10000）。</summary>
    public string Endpoint { get; set; } = "http://127.0.0.1:10000/v1/chat/completions";

    /// <summary>服务端模型名（--served-model-name）。</summary>
    public string Model { get; set; } = "Unlimited-OCR";

    /// <summary>解析提示词（官方 gundam 单图模式为 document parsing.）。</summary>
    public string Prompt { get; set; } = "document parsing.";

    /// <summary>单帧请求超时（CPU 推理实测可达数十秒；GPU 服务 1~3 秒）。</summary>
    public int TimeoutSeconds { get; set; } = 120;
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

/// <summary>虚拟手柄后端配置（唯一后端 ViGEm Xbox 360）。</summary>
public sealed class PadConfig
{
    /// <summary>
    /// 虚拟手柄后端。唯一后端 Vigem（ViGEmBus 虚拟 Xbox 360，XInput 玩家 1 全通道被
    /// eFootball 消费）；旧配置遗留的 HIDMaestro 枚举值加载时归一化（同为 0，天然兼容）。
    /// </summary>
    public PadBackendKind Backend { get; set; } = PadBackendKind.Vigem;

    /// <summary>
    /// 旧版后端值归一化：一律归 Vigem（唯一后端；HIDMaestro 遗留枚举值同为 0 天然兼容，
    /// 其余遗留值强归，防止损坏/未知值流入）。
    /// </summary>
    public void MigrateDefaults()
    {
        if (Backend != PadBackendKind.Vigem)
            Backend = PadBackendKind.Vigem;
    }
}

/// <summary>真实手柄接管配置（v3.3.2 起 HidHide 隔离整链剔除，仅剩挂机禁用真实手柄；
/// JSON 键名保留 "HidHide" 以兼容旧配置——其中的 DisabledPadDevs 恢复清单不能丢，
/// 丢失会把用户被禁用的真实手柄卡在禁用态）。设备节点禁用清单由 PhysicalPadControl 维护。</summary>
public sealed class PadTakeoverConfig
{
    /// <summary>
    /// 挂机期间自动禁用真实手柄以释放 XInput 玩家 1（全权接管模型）。
    /// 虚拟手柄为 Xbox 360 形态，必须拿到 slot0 才被 eFootball 消费；
    /// 停止挂机自动恢复真实手柄。
    /// </summary>
    public bool HidePhysicalGamepad { get; set; } = true;

    /// <summary>被禁用的真实手柄节点恢复清单（先记录后禁用，崩溃也能按清单恢复；
    /// 由 pad-hide/pad-restore 提权任务与 GUI 启动兜底消费）。</summary>
    public List<string> DisabledPadDevs { get; set; } = new();

    /// <summary>
    /// 老默认迁移：3.2.x 曾把 HidePhysicalGamepad 压回 false（共存模式）；
    /// v3.3.0 起全权接管模型，一律翻回 true（挂机禁用真实手柄、停止恢复）。
    /// 只改内存，首次 Save 时落盘（同 TimingConfig 迁移模式）。
    /// </summary>
    public void MigrateDefaults() => HidePhysicalGamepad = true;
}

/// <summary>
/// 赛后活动任务页（全屏活动地图，随服务器推送轮换）配置。仅国际服独享。
/// 该页固定出现在比赛结束后 / 比赛开始前：右上「奖励」胶囊 + 右下「下一步」+
/// 底部「X 详情 / Y 当前位置」；活动名与小图标均为可变内容，不作判定点。
/// </summary>
public sealed class PostMatchActivityConfig
{
    /// <summary>
    /// 自动完成（推进）周任务：在活动地图上按 Y 回初始位置 →（↑ → 下一步 → 弹窗 ↑ → 下一步）
    /// 循环左侧小图标旁的数字次数 → 退出。默认关闭（关闭时只执行退出：→ 下一步）。
    /// </summary>
    public bool AutoAdvanceWeeklyTasks { get; set; } = false;
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

    /// <summary>
    /// 满级自动换人：每 <see cref="AutoSwapEveryMatches"/> 场比赛结束后回到联赛主页（无弹窗）时，
    /// 自动按 Y 打开比赛计划巡检首发 11 人；满级（等级 x/x 且能力点未用完）球员先分配能力点
    /// 再从后备球员换入最低等级球员。巡检优先级高于道具核验。
    /// </summary>
    public bool AutoSwapMaxLevel { get; set; } = false;

    /// <summary>自动换人间隔场次（每 N 场比赛结束后触发一次巡检，1~50）。</summary>
    public int AutoSwapEveryMatches { get; set; } = 5;

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

    /// <summary>
    /// 全部活动刷完后的接力模块：空 = 停止挂机；intl = 我的联赛；intlladder = AI天梯联赛。
    /// 接力在同一个挂机会话内完成（虚拟手柄与捕获会话不重建），场次计数从 0 重新开始。
    /// </summary>
    public string AfterCompleteModule { get; set; } = "";
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

    /// <summary>
    /// 全部活动刷完后的接力模块：空 = 停止挂机；cnmyleague = 我的联赛（国服）；cndaily = 天梯巅峰赛。
    /// 接力在同一个挂机会话内完成（虚拟手柄与捕获会话不重建），场次计数从 0 重新开始。
    /// </summary>
    public string AfterCompleteModule { get; set; } = "";
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
/// 虚拟显示器后台（测试版）：挂机时把游戏窗口停靠到一块虚拟显示器上——游戏跑在
/// 控制台会话（非 RDP，eFootball 不拒绝），物理屏幕完全不可见，也没有停靠缩窗问题。
/// 仅测试版构建（ChildSessionBeta=true）生效；正式版构建读到此配置也会忽略。
/// </summary>
public sealed class VirtualDisplayParkConfig
{
    /// <summary>启用虚拟显示器停靠（替代「移出屏幕」式后台停靠）。</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>虚拟显示器分辨率（默认显示器分辨率，写入驱动注册表后对新激活生效）。</summary>
    public int DesktopWidth { get; set; } = 1920;

    /// <summary>虚拟显示器高度。</summary>
    public int DesktopHeight { get; set; } = 1080;
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

    /// <summary>
    /// 随机整日休息概率（0~100，0 = 关闭）。每天按「日期 + 启动盐」确定性抽样：
    /// 抽中则当天完全不挂机。防的是「每天都挂满同样的时长」这一长期行为指纹——
    /// 单一账号天天 6 小时比偶尔休息更不像真人。
    /// </summary>
    public int RestDayChancePercent { get; set; } = 0;
}

/// <summary>
/// 完成/异常通知（Webhook）：POST 一段 JSON 到指定 URL。
/// 兼容 Bark / 飞书 / 钉钉群机器人 / Telegram Bot / ntfy 等常见「一个 URL 搞定」的服务。
/// 默认关闭且 URL 为空 —— 属于用户主动开启的外发行为，绝不自作主张联网。
/// </summary>
public sealed class NotifyConfig
{
    /// <summary>完整 Webhook URL（含 token/路径）。空 = 不发送。</summary>
    public string WebhookUrl { get; set; } = "";

    /// <summary>挂机正常停止（用户点停止 / 打完场次）时通知。</summary>
    public bool OnStop { get; set; } = true;

    /// <summary>异常终止（捕获熔断、窗口消失、崩溃兜底停止）时通知。建议保持开启。</summary>
    public bool OnError { get; set; } = true;

    /// <summary>每完成一场比赛推送一条（与下一条间隔小于合并窗口时自动合并，防刷屏）。</summary>
    public bool OnMatchDone { get; set; } = true;

    /// <summary>检出满级球员时推送（比赛中的满级提示与满级换人巡检都算）。</summary>
    public bool OnMaxLevel { get; set; } = true;

    /// <summary>转发警告/错误级日志（错误立即推，警告走合并窗口）。</summary>
    public bool OnWarnLog { get; set; } = true;

    /// <summary>挂机运行中按间隔推一条摘要（场次/满级/用时），适合免费渠道条数紧张时只留这一路。</summary>
    public bool OnRunningSummary { get; set; } = false;

    /// <summary>运行中摘要推送间隔（分钟）。</summary>
    public int SummaryIntervalMinutes { get; set; } = 60;

    /// <summary>每日推送总条数上限（0 = 不限制）。免费渠道（如 Server酱免费版每天 5 条）务必设置。</summary>
    public int DailySendLimit { get; set; } = 50;

    /// <summary>同类事件合并窗口（分钟）：窗口内多起事件并成一条推送，避免刷爆条数。</summary>
    public int MergeMinutes { get; set; } = 5;

    /// <summary>通知内容里带的实例名（多开时区分是哪个号）。</summary>
    public string InstanceName { get; set; } = "";
}

/// <summary>
/// 局域网监控页（默认关闭）：手机/平板同 WiFi 打开即可查看运行状态并暂停/继续。
/// 只监听本机网卡，页面需要 token 才能操作；不会上传任何数据。
/// </summary>
public sealed class MonitorConfig
{
    public bool Enabled { get; set; } = false;

    /// <summary>监听端口（http://本机IP:端口/）。</summary>
    public int Port { get; set; } = 18321;

    /// <summary>访问 token（URL 里 ?t=... 或页面输入）；空 = 只允许查看、禁止操作。</summary>
    public string Token { get; set; } = "";
}

public sealed class AppConfig
{
    /// <summary>
    /// 配置结构版本。用于「删功能后不再靠枚举兼容 hack 养老」：
    /// 读取旧版本时由 <see cref="Migrate"/> 逐级升级（升到当前版本后立刻回写）。
    /// 1 = 隐式老版本（在引入本字段之前写出的配置）。
    /// </summary>
    public const int CurrentSchemaVersion = 3;

    [JsonPropertyName("SchemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string Theme { get; set; } = "Dark"; // Dark / Light / DeepBlue
    /// <summary>OCR 识别引擎：SimdPaddle（纯托管 PP-OCR，默认）/ RapidV6（ONNX Runtime CPU）/
    /// UnlimitedOcr（外部 VLM 服务，HTTP 接入）。旧值 Windows(1)=已删除的 Windows 内置 OCR，
    /// 读取后按 SimdPaddle 处理。下次启动挂机生效。</summary>
    public OcrEngineKind OcrEngine { get; set; } = OcrEngineKind.SimdPaddle;

    /// <summary>Unlimited-OCR 外部服务配置（仅 OcrEngine=UnlimitedOcr 时使用）。</summary>
    public UnlimitedOcrConfig UnlimitedOcr { get; set; } = new();
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

    // —— 真实手柄接管（JSON 键名 "HidHide" 为旧配置兼容保留）——
    public PadTakeoverConfig HidHide { get; set; } = new();

    // —— 虚拟手柄后端 ——
    public PadConfig Pad { get; set; } = new();

    // —— 我的联赛道具 ——
    public MyLeagueConfig MyLeague { get; set; } = new();

    // —— 赛后活动任务页（全屏活动地图，仅国际服）——
    public PostMatchActivityConfig PostMatchActivity { get; set; } = new();

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
    public VirtualDisplayParkConfig VirtualDisplayPark { get; set; } = new();
    public ScheduleConfig Schedule { get; set; } = new();

    // —— 影子模式：不实际按键，只把「本来会按什么」写进日志 ——
    /// <summary>影子模式（dry-run）：感知/判定照常跑，手柄输出被吞掉并记日志。
    /// 用于新 UI 版本上零风险验证决策、以及产出「Bot 想做什么」的排障证据。</summary>
    public bool DryRun { get; set; } = false;

    // —— 完成/异常通知 与 局域网监控 ——
    public NotifyConfig Notify { get; set; } = new();
    public MonitorConfig Monitor { get; set; } = new();

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
                string text = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(text, JsonOpts) ?? new AppConfig();
                // 老配置没有 SchemaVersion 字段：反序列化会保留属性默认值（=当前版本），
                // 单看属性无法区分「新配置」与「老配置」。以原始文本为准判定。
                if (!text.Contains("\"SchemaVersion\"", StringComparison.Ordinal))
                    cfg.SchemaVersion = 1;
                bool upgraded = cfg.SchemaVersion < CurrentSchemaVersion;
                cfg.Migrate();
                cfg.Timing.MigrateIdleTapDefaults();
                cfg.HidHide.MigrateDefaults();
                cfg.Pad.MigrateDefaults();
                if (upgraded)
                {
                    // 升级结果立即回写：下次启动不再重复走迁移，也让用户能看到新增字段
                    try { cfg.Save(); } catch { /* 回写失败不影响本次运行 */ }
                }
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

    /// <summary>
    /// 旧配置逐级升级到 <see cref="CurrentSchemaVersion"/>。
    /// 原则：只做「结构性补全 + 已删除功能的等价替换」，绝不重置用户已有取值。
    /// </summary>
    public void Migrate()
    {
        if (SchemaVersion >= CurrentSchemaVersion) return;

        if (SchemaVersion < 2)
        {
            // v1 → v2：无结构变更。Windows 内置 OCR 引擎已在 OcrEngine 读取处静默迁移为 SimdPaddle，
            // HidHide 键名兼容等历史兼容逻辑各自保留在对应配置类里。
        }
        if (SchemaVersion < 3)
        {
            // v2 → v3：新增 DryRun / Notify / Monitor 段，默认值即正确行为（全关），无需搬运数据。
        }

        SchemaVersion = CurrentSchemaVersion;
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
