using EfootballBot.Core.Bot;


namespace EfootballBot.Core.Input;

/// <summary>
/// 槽位归属判定与「真实手柄让路」编排：判断 slot0 上跑的到底是<b>本程序的虚拟手柄</b>
/// 还是<b>真实手柄</b>，并在后者的情况下当场纠正（禁用真实手柄 → 重进虚拟手柄抢回玩家 1）。
///
/// 为什么需要：XInput 是按设备给的，一次只能"占/不占"，程序无法直接问出来"0 号槽是谁"。
/// 而「有真实手柄时 bot 一点输入都没有」正是这样发生的——真实手柄占着 slot0，虚拟手柄只能
/// 落到 slot1，而 eFootball 只认玩家 1（双控制器实测），于是 bot 按键全部被游戏无视。
/// 旧流程只在虚拟手柄<b>连上之前</b>禁用真实手柄；真实手柄在连上之后才被系统认作玩家 1
/// （蓝牙手柄重连、开机时序、或节点禁用后 XInput 仍报连接）时没有任何兜底，于是卡死。
///
/// 判定手法（<b>零副作用</b>）：让虚拟手柄在左摇杆上打一个只有我们知道的微码
/// （<see cref="VigemPad.IdentityCode"/>），然后在 slot0 读回来：
/// - 读回=微码 → slot0 就是我们的虚拟手柄，真实手柄已经让位；
/// - 读回≠微码 → slot0 被别的设备占着（真实手柄/别的虚拟手柄软件）。
/// 不用"按一下真键"探测：那会真的触发游戏操作（盲按 A 可能点掉弹窗）。
/// </summary>
public static class PadSlotWitness
{
    /// <summary>读回是否等于虚拟手柄微码（真实手柄读到的是它自己的摇杆值/0，几乎不可能相等）。</summary>
    public static bool IsVirtualPadInSlot0(PadBackendProbe.XINPUT_STATE? slot0, short code)
        => slot0 is { } s && s.sThumbLX == code && s.sThumbLY == code;

    /// <summary>
    /// 把槽位探测结果翻译成给用户的结论（纯函数便于单测）。
    /// <paramref name="virtualConnected"/>=false 表示虚拟手柄还没连上（此时 slot0 上必然是别的设备）。
    /// </summary>
    public static string Describe(bool slot0Occupied, bool virtualInSlot0, bool virtualConnected)
    {
        if (!slot0Occupied)
            return "玩家 1（slot 0）空闲";
        if (virtualInSlot0)
            return "玩家 1 是虚拟手柄（游戏会响应 bot 输入）";
        if (!virtualConnected)
            return "玩家 1 被真实手柄/其他手柄设备占用（虚拟手柄接不进来，bot 无法操作游戏）";
        return "玩家 1 被真实手柄占用（虚拟手柄落在后面的槽位，游戏不响应 bot 输入）";
    }

    /// <summary>
    /// 一次性探测：先读基线，再打微码并读回（保持 <see cref="WitnessDurationMs"/>），
    /// 最后把摇杆打回中位。<paramref name="stomp"/> 为"打微码"、<paramref name="clear"/> 为"打回中位"。
    /// </summary>
    public static WitnessResult Witness(short code, Action<short, short> stomp, Action clear)
    {
        var before = PadBackendProbe.TryRead(0);
        WitnessResult result;
        try
        {
            stomp(code, code);
            Thread.Sleep(WitnessDurationMs);
            var during = PadBackendProbe.TryRead(0);
            result = new WitnessResult(before is not null, during is not null && IsVirtualPadInSlot0(during, code), during);
        }
        catch
        {
            result = new WitnessResult(before is not null, false, null);
        }
        finally
        {
            try { clear(); } catch { }
        }
        return result;
    }

    /// <summary>微码保持时长：够 2 个轮询周期（系统 XInput 约 4~8ms 刷新一次状态）。</summary>
    public const int WitnessDurationMs = 120;

    public readonly record struct WitnessResult(bool Slot0Occupied, bool VirtualInSlot0,
        PadBackendProbe.XINPUT_STATE? Slot0State);

    /// <summary>日志行（把微码读回的具体数值打出来，便于排查是"没打进去"还是"被抢槽"）。</summary>
    public static string DescribeWitness(WitnessResult r, short code)
    {
        if (r.Slot0State is not { } s) return "玩家 1 探测：空闲";
        var verdict = r.VirtualInSlot0 ? "虚拟手柄在玩家 1" : "玩家 1 是别的设备";
        return $"玩家 1 探测：占用=是，摇杆微码读回=({s.sThumbLX},{s.sThumbLY}) 期望({code},{code}) → {verdict}";
    }

    /// <summary>真实手柄让路的结果。</summary>
    public enum YieldOutcome
    {
        /// <summary>虚拟手柄确实在玩家 1，可以开始操作。</summary>
        Ready,
        /// <summary>没有虚拟手柄参与（观察模式/关闭接管/影子模式）。</summary>
        NoPadMode,
        /// <summary>需要用户物理断开手柄（有线手柄禁用后仍占槽，或其他虚拟手柄软件占槽）。</summary>
        AbortAskUser,
        /// <summary>禁用真实手柄后重进成功。</summary>
        Recovered,
    }

    /// <summary>
    /// 让路编排（连接<b>之后</b>调用）：探测玩家 1 归属；不是我们的虚拟手柄就禁用真实手柄，
    /// 再重进虚拟手柄抢回玩家 1，并二次探测确认。全过程只读设备状态 + 提交摇杆值，不按任何键。
    /// </summary>
    /// <param name="pad">已连接的虚拟手柄（必须实现 <see cref="IVirtualPad"/>）。</param>
    /// <param name="takeoverEnabled">用户是否开启「挂机时接管手柄」。</param>
    /// <param name="dryRun">影子模式：只看不做，绝不动真实设备。</param>
    /// <param name="log">日志输出。</param>
    /// <returns>结论 + 给用户的消息（<see cref="YieldOutcome.AbortAskUser"/> 时非空）。</returns>
    public static (YieldOutcome Outcome, string Message) EnsureVirtualPadInSlot0(
        Func<IVirtualPad> getPad, bool takeoverEnabled, bool dryRun, Action<string, LogLevel> log)
    {
        var pad = getPad();
        if (dryRun || !takeoverEnabled)
            return (YieldOutcome.NoPadMode, "");
        if (!pad.IsConnected)
            return (YieldOutcome.NoPadMode, "");

        var r1 = WitnessPad(pad);
        log(DescribeWitness(r1, VigemPad.IdentityCode), LogLevel.Info);
        if (r1.VirtualInSlot0) return (YieldOutcome.Ready, "");

        // 玩家 1 不是我们：禁用真实手柄后重进虚拟手柄（重连才会重新参与槽位分配）
        log("真实手柄仍占着玩家 1，正在禁用它以便虚拟手柄抢回玩家 1（停止挂机时自动恢复）…",
            LogLevel.Warn);
        bool physicalPresent = HidDeviceEnumerator.FindPhysicalGamepadDevNodes().Count > 0;
        if (physicalPresent)
        {
            PhysicalPadControl.RequestPadHide();
            var deadline = DateTime.Now.AddSeconds(20);
            while (HidDeviceEnumerator.FindPhysicalGamepadDevNodes().Count > 0 && DateTime.Now < deadline)
                Thread.Sleep(500);
        }

        // 重进：先断开释放自己的槽位，再按"允许 slot0 被占"的宽松模式连回来
        try { pad.Disconnect(); } catch { }
        try
        {
            if (pad is VigemPad vg) vg.Connect(allowSlot0Occupied: true);
            else pad.Connect();
        }
        catch (Exception ex)
        {
            log($"重新连接虚拟手柄失败：{ex.Message}", LogLevel.Error);
        }

        var r2 = WitnessPad(pad);
        log(DescribeWitness(r2, VigemPad.IdentityCode), LogLevel.Info);
        if (r2.VirtualInSlot0)
        {
            log("已抢回玩家 1：虚拟手柄独占游戏输入", LogLevel.Success);
            return (YieldOutcome.Recovered, "");
        }

        bool stillPhysical = HidDeviceEnumerator.FindPhysicalGamepadDevNodes().Count > 0;
        var (_, message) = PhysicalPadYieldPolicy.AfterDisable(virtualInSlot0: false, physicalPadPresent: stillPhysical);
        return (YieldOutcome.AbortAskUser, message);
    }

    private static WitnessResult WitnessPad(IVirtualPad pad)
    {
        if (pad is VigemPad vg)
            return Witness(VigemPad.IdentityCode, (x, y) => vg.SetLeftStick(x, y), () => vg.SetLeftStick(0, 0));

        // 非 ViGEm 后端（未来扩展）：用通用的摇杆接口打/清微码
        return Witness(VigemPad.IdentityCode,
            (x, y) => pad.SetLeftStick(x, y),
            () => pad.SetLeftStick(0, 0));
    }
}

