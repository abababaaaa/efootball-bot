namespace EfootballBot.Core.Input;

/// <summary>虚拟手柄后端种类（AppConfig.Pad.Backend）。ViGEmBus 两档（DualShock4/Xbox 360）已随 3.0 移除，
/// 仅剩 HIDMaestro 一个后端；旧配置里遗留的枚举值（0/1/2）在加载时一律归一化到 HidMaestro。</summary>
public enum PadBackendKind
{
    /// <summary>HIDMaestro：纯 HID 用户态驱动（ViGEmBus 退役后的继任者，SDK 按需下载）。
    /// XInput-only 的应用（绝大多数 PC 游戏、Steam 的 XInput 通道）根本看不到设备，
    /// 从设备类别上消除输入泄漏；HID 层再叠加 HidHide 白名单，最终仅 eFootball 可见。</summary>
    HidMaestro = 0,
}

/// <summary>后端元信息与策略纯逻辑（可单测）。单后端后仅存展示与兜底语义。</summary>
public static class PadBackendInfo
{
    /// <summary>设置页下拉的展示顺序。</summary>
    public static readonly PadBackendKind[] UiOrder = { PadBackendKind.HidMaestro };

    /// <summary>后端 → 下拉索引；未知值按默认（HidMaestro）处理。</summary>
    public static int UiIndexOf(PadBackendKind kind)
    {
        int i = Array.IndexOf(UiOrder, kind);
        return i >= 0 ? i : 0;
    }

    /// <summary>下拉索引 → 后端；越界按默认（HidMaestro）处理。</summary>
    public static PadBackendKind FromUiIndex(int index)
        => index >= 0 && index < UiOrder.Length ? UiOrder[index] : PadBackendKind.HidMaestro;

    public static string DisplayName(PadBackendKind kind) => kind switch
    {
        PadBackendKind.HidMaestro => "HIDMaestro 隔离",
        _ => kind.ToString(),
    };

    /// <summary>
    /// 该后端的手柄是否对 XInput 可见。HIDMaestro 为纯 HID——XInput 侧天然失明，
    /// 配合 HidHide（HID 层白名单）即「全系统只有 eFootball 看得见这只手柄」。
    /// （旧 Xbox 360 后端曾为 true：XInput 槽位全机共享且无法按进程过滤，是输入泄漏的根源。）
    /// </summary>
    public static bool IsXInputVisible(PadBackendKind kind) => false;

    /// <summary>该后端是否依赖「XInput 玩家 1 空闲」才生效。纯 HID 手柄没有 XInput 槽位概念，
    /// 真实手柄占用玩家 1 时不阻断启动。</summary>
    public static bool RequiresSlotZero(PadBackendKind kind) => false;

    /// <summary>连接失败时的回退顺序（请求的后端在首位）。单后端后链长恒为 1，无回退可言。</summary>
    public static PadBackendKind[] FallbackChain(PadBackendKind requested) => [PadBackendKind.HidMaestro];
}
