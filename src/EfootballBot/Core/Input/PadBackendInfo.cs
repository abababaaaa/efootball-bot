namespace EfootballBot.Core.Input;

/// <summary>虚拟手柄后端种类（AppConfig.Pad.Backend）。v3.3.0 回退定案：ViGEmBus Xbox 360
/// 重新成为唯一后端（HIDMaestro 全系档位轴通道不被 eFootball 消费，2026-10-21 探针实锤）；
/// 旧配置里遗留的 HIDMaestro 枚举值（0）在加载时归一化到 Vigem（同为 0，天然兼容）。</summary>
public enum PadBackendKind
{
    /// <summary>ViGEmBus 虚拟 Xbox 360 手柄：XInput 玩家 1 全通道被 eFootball 消费。
    /// 占用 XInput 槽位——真实手柄由挂机自动禁用让位（全权接管），停止挂机自动恢复；
    /// HidHide 白名单保证其他应用看不到虚拟手柄。</summary>
    Vigem = 0,
}

/// <summary>后端元信息与策略纯逻辑（可单测）。单后端后仅存展示与兜底语义。</summary>
public static class PadBackendInfo
{
    /// <summary>设置页下拉的展示顺序。</summary>
    public static readonly PadBackendKind[] UiOrder = { PadBackendKind.Vigem };

    /// <summary>后端 → 下拉索引；未知值按默认（Vigem）处理。</summary>
    public static int UiIndexOf(PadBackendKind kind)
    {
        int i = Array.IndexOf(UiOrder, kind);
        return i >= 0 ? i : 0;
    }

    /// <summary>下拉索引 → 后端；越界按默认（Vigem）处理。</summary>
    public static PadBackendKind FromUiIndex(int index)
        => index >= 0 && index < UiOrder.Length ? UiOrder[index] : PadBackendKind.Vigem;

    public static string DisplayName(PadBackendKind kind) => kind switch
    {
        PadBackendKind.Vigem => "ViGEmBus 接管",
        _ => kind.ToString(),
    };

    /// <summary>
    /// 该后端的手柄是否对 XInput 可见。Xbox 360 形态天然占 XInput 槽位（玩家 1），
    /// 真实手柄需由设备节点禁用让位——这是「全权接管」模型的一部分。
    /// 其他应用侧的不可见性由 HidHide 白名单保证（XInput 不吃黑名单，见 HidHideManager）。
    /// </summary>
    public static bool IsXInputVisible(PadBackendKind kind) => true;

    /// <summary>该后端是否依赖「XInput 玩家 1 空闲」才生效。Xbox 360 形态必须拿到 slot0
    /// 才被 eFootball 消费，真实手柄未成功让位时阻断启动。</summary>
    public static bool RequiresSlotZero(PadBackendKind kind) => true;

    /// <summary>连接失败时的回退顺序（请求的后端在首位）。单后端后链长恒为 1，无回退可言。</summary>
    public static PadBackendKind[] FallbackChain(PadBackendKind requested) => [PadBackendKind.Vigem];
}
