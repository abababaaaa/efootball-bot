namespace EfootballBot.Core.Input;

/// <summary>虚拟手柄后端种类（AppConfig.Pad.Backend）。</summary>
public enum PadBackendKind
{
    /// <summary>ViGEm DualShock4（默认）：纯 HID 设备、无 XInput/XUSB 通道，
    /// XInput-only 的应用（绝大多数 PC 游戏、Steam 的 XInput 通道）根本看不到设备，
    /// 从设备类别上消除输入泄漏；HID 层再叠加 HidHide 白名单，最终仅 eFootball 可见。</summary>
    HidDs4 = 0,

    /// <summary>HIDMaestro：纯 HID（ViGEmBus 退役后的继任驱动，SDK 按需下载，实验）。</summary>
    HidMaestro = 1,

    /// <summary>ViGEm Xbox 360（旧版）：XInput 系统级设备，所有游戏都能看到并响应其输入。
    /// 仅作兼容回退：游戏不认纯 HID 手柄时切回这一档（泄漏随之回来）。</summary>
    Xbox360 = 2,
}

/// <summary>后端元信息与策略纯逻辑（可单测）。</summary>
public static class PadBackendInfo
{
    public static string DisplayName(PadBackendKind kind) => kind switch
    {
        PadBackendKind.HidDs4 => "DualShock4 隔离（默认）",
        PadBackendKind.HidMaestro => "HIDMaestro 隔离（实验）",
        PadBackendKind.Xbox360 => "Xbox 360 旧版（兼容回退）",
        _ => kind.ToString(),
    };

    /// <summary>
    /// 该后端的手柄是否对 XInput 可见。纯 HID 后端（DS4/HIDMaestro）为 false——
    /// XInput 侧天然失明，配合 HidHide（HID 层白名单）即「全系统只有 eFootball 看得见这只手柄」。
    /// Xbox 360 后端为 true：XInput 槽位全机共享且无法按进程过滤（HidHide 对 XInput 无效，实测），
    /// 这是旧后端输入泄漏的根源。
    /// </summary>
    public static bool IsXInputVisible(PadBackendKind kind) => kind == PadBackendKind.Xbox360;

    /// <summary>该后端是否依赖「XInput 玩家 1 空闲」才生效（仅 Xbox 360 旧版需要）。
    /// 纯 HID 手柄没有 XInput 槽位概念，真实手柄占用玩家 1 时不阻断启动。</summary>
    public static bool RequiresSlotZero(PadBackendKind kind) => kind == PadBackendKind.Xbox360;

    /// <summary>连接失败时的回退顺序（请求的后端在首位）。纯 HID 后端互为回退，
    /// 最终才落到 Xbox 360 旧版（功能保底、隔离降级，回退必告警）。</summary>
    public static PadBackendKind[] FallbackChain(PadBackendKind requested) => requested switch
    {
        PadBackendKind.HidMaestro => [PadBackendKind.HidMaestro, PadBackendKind.HidDs4, PadBackendKind.Xbox360],
        PadBackendKind.Xbox360 => [PadBackendKind.Xbox360],
        _ => [PadBackendKind.HidDs4, PadBackendKind.Xbox360],
    };
}
