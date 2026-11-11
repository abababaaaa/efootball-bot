namespace EfootballBot.Core.Input;

/// <summary>虚拟手柄后端种类（AppConfig.Pad.Backend）。
/// Vigem = ViGEmBus 虚拟 Xbox 360（占系统设备与 slot0）；XInputProxy = 目标游戏进程内的
/// xinput1_3 代理（不占设备、不串前台，v6.5 起正式构建可用）。
/// 旧配置里遗留的 HIDMaestro 枚举值（0）在加载时归一化到 Vigem（同为 0，天然兼容）。</summary>
public enum PadBackendKind
{
    /// <summary>ViGEmBus 虚拟 Xbox 360 手柄：XInput 玩家 1 全通道被 eFootball 消费。
    /// 占用 XInput 槽位——真实手柄由挂机自动禁用让位（全权接管），停止挂机自动恢复。</summary>
    Vigem = 0,

    /// <summary>目标游戏进程内的 xinput1_3 代理（共享内存喂状态，见 tools/xinput-proxy）。
    /// 不创建设备、不占槽位、不影响任何其他进程；真实手柄原地不动。</summary>
    XInputProxy = 1,
}

/// <summary>后端元信息与策略纯逻辑（可单测）。</summary>
public static class PadBackendInfo
{
    /// <summary>
    /// 进程内代理后端是否可用。v6.5 起正式构建默认开放（不再依赖测试版开关）。
    /// 保留这个开关是编译期急停位：真在线上发现问题时改成 false，
    /// 配置里的 XInputProxy 就会被 <see cref="Normalize"/> 归一化回 ViGEm。
    /// </summary>
    public static bool ProxyBackendEnabled => true;

    /// <summary>未知值一律归 Vigem；急停位关闭时代理也归 Vigem。</summary>
    public static PadBackendKind Normalize(PadBackendKind kind)
    {
        if (kind == PadBackendKind.XInputProxy && !ProxyBackendEnabled) return PadBackendKind.Vigem;
        return kind is PadBackendKind.Vigem or PadBackendKind.XInputProxy ? kind : PadBackendKind.Vigem;
    }

    /// <summary>设置页下拉的展示顺序（代理在前：更符合"不占设备、不串前台"的目标形态）。</summary>
    public static readonly PadBackendKind[] UiOrder = ProxyBackendEnabled
        ? [PadBackendKind.XInputProxy, PadBackendKind.Vigem]
        : [PadBackendKind.Vigem];

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
        PadBackendKind.XInputProxy => "进程内代理（不占设备）",
        PadBackendKind.Vigem => "ViGEmBus 接管",
        _ => kind.ToString(),
    };

    /// <summary>
    /// 该后端的手柄是否对 XInput 全局可见。ViGEm 是系统设备（所有进程可见）；
    /// 进程内代理只影响目标游戏自己，其他应用看不到任何东西。
    /// </summary>
    public static bool IsXInputVisible(PadBackendKind kind)
        => kind != PadBackendKind.XInputProxy;

    /// <summary>该后端是否依赖"XInput 玩家 1 空闲"才生效。代理自带 slot0 语义，不占槽位。</summary>
    public static bool RequiresSlotZero(PadBackendKind kind)
        => kind != PadBackendKind.XInputProxy;

    /// <summary>连接失败时的回退顺序（请求的后端在首位；代理失败退 ViGEm）。</summary>
    public static PadBackendKind[] FallbackChain(PadBackendKind requested) => requested switch
    {
        PadBackendKind.XInputProxy when ProxyBackendEnabled => [PadBackendKind.XInputProxy, PadBackendKind.Vigem],
        _ => [PadBackendKind.Vigem],
    };
}
