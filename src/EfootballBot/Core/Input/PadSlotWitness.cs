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
    /// 一次性探测：先读基线，再打微码并多次轮询读回（40ms 间隔，最长 480ms），
    /// 一旦确认在 slot0 立即返回，最后把摇杆打回中位。
    /// </summary>
    public static WitnessResult Witness(short code, Action<short, short> stomp, Action clear)
    {
        var before = PadBackendProbe.TryRead(0);
        WitnessResult result = new(before is not null, false, null);
        try
        {
            stomp(code, code);
            int waited = 0;
            while (waited < WitnessMaxWaitMs)
            {
                Thread.Sleep(WitnessPollIntervalMs);
                waited += WitnessPollIntervalMs;
                var during = PadBackendProbe.TryRead(0);
                bool inSlot0 = during is not null && IsVirtualPadInSlot0(during, code);
                result = new WitnessResult(before is not null || during is not null, inSlot0, during);
                if (inSlot0) break;
            }
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

    /// <summary>单轮微码保持基准时长（保留供诊断/兼容性参考）。</summary>
    public const int WitnessDurationMs = 120;
    /// <summary>微码轮询步长（ms）。</summary>
    public const int WitnessPollIntervalMs = 40;
    /// <summary>单次探测最长等待微码回读窗口（ms）。</summary>
    public const int WitnessMaxWaitMs = 480;

    /// <summary>纯逻辑：slot0 实际空闲但虚拟手柄尚未落入时，允许断开重连再试一次。</summary>
    public static bool ShouldRetryEmptySlot0(WitnessResult r, int attempt)
        => !r.VirtualInSlot0 && r.Slot0State is null && attempt < 1;

    /// <summary>
    /// 纯逻辑：等待禁用真实手柄生效时何时可结束轮询。
    /// slot0 空闲立即结束；若设备节点全已禁用且宽限满 6s（父设备/驱动注销窗口）也可结束，
    /// 不得在物理节点刚变为禁用、slot0 尚未释放的那一毫秒提前跳出。
    /// </summary>
    public static bool ShouldExitHideWait(bool slot0Occupied, int enabledPhysicalNodes, long elapsedSinceNodesZeroMs)
    {
        if (!slot0Occupied) return true;
        return enabledPhysicalNodes == 0 && elapsedSinceNodesZeroMs >= 6000;
    }

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
    public static async Task<(YieldOutcome Outcome, string Message)> EnsureVirtualPadInSlot0Async(
        Func<IVirtualPad> getPad, bool takeoverEnabled, bool dryRun, Action<string, LogLevel> log)
    {
        var pad = getPad();
        if (dryRun || !takeoverEnabled)
            return (YieldOutcome.NoPadMode, "");
        if (!pad.IsConnected)
            return (YieldOutcome.NoPadMode, "");

        var r1 = WitnessPad(pad);
        if (!r1.VirtualInSlot0)
        {
            // 上电后系统/驱动可能还在枚举新槽位，宽限 400ms 复核一次避免误判拆线重连
            await Task.Delay(400);
            r1 = WitnessPad(pad);
        }
        log(DescribeWitness(r1, VigemPad.IdentityCode), LogLevel.Info);
        if (r1.VirtualInSlot0) return (YieldOutcome.Ready, "");

        // 玩家 1 不是我们：禁用真实手柄后重进虚拟手柄（重连才会重新参与槽位分配）
        log("真实手柄仍占着玩家 1，正在禁用它以便虚拟手柄抢回玩家 1（停止挂机时自动恢复）…",
            LogLevel.Warn);
        var activePhysical = HidDeviceEnumerator.FindConnectedPhysicalGamepadDevNodes();
        if (activePhysical.Count == 0 && PadBackendProbe.IsSlot0Occupied())
            activePhysical = HidDeviceEnumerator.FindPhysicalGamepadDevNodes();
        if (activePhysical.Count > 0)
        {
            var (inProcess, _, hideMsg) = await Task.Run(PhysicalPadControl.HidePhysicalPads);
            if (inProcess)
            {
                log($"[pad-hide] {hideMsg}", LogLevel.Info);
                var settleDeadline = Environment.TickCount64 + 2000;
                while (PadBackendProbe.IsSlot0Occupied() && Environment.TickCount64 < settleDeadline)
                    await Task.Delay(200);
            }
            else
            {
                var sw = global::System.Diagnostics.Stopwatch.StartNew();
                long? nodesZeroAt = null;
                while (sw.ElapsedMilliseconds < 20_000)
                {
                    bool slot0 = PadBackendProbe.IsSlot0Occupied();
                    int enabled = HidDeviceEnumerator.FindPhysicalGamepadDevNodes().Count;
                    if (enabled == 0) nodesZeroAt ??= sw.ElapsedMilliseconds;
                    else nodesZeroAt = null;
                    long zeroMs = nodesZeroAt is { } t ? sw.ElapsedMilliseconds - t : 0;
                    if (ShouldExitHideWait(slot0, enabled, zeroMs)) break;
                    await Task.Delay(250);
                }
            }
        }

        async Task<WitnessResult> ReconnectAndWitnessAsync()
        {
            try { pad.Disconnect(); } catch { }
            // 等旧虚拟手柄从内核/XInput 彻底卸载，再按宽松模式重建抢槽
            await Task.Delay(500);
            var freeDeadline = Environment.TickCount64 + 2500;
            while (PadBackendProbe.IsSlot0Occupied() && Environment.TickCount64 < freeDeadline)
                await Task.Delay(100);

            try
            {
                if (pad is VigemPad vg) vg.Connect(allowSlot0Occupied: true);
                else pad.Connect();
            }
            catch (Exception ex)
            {
                log($"重新连接虚拟手柄失败：{ex.Message}", LogLevel.Error);
            }

            WitnessResult latest = default;
            for (int probe = 0; probe < 3; probe++)
            {
                await Task.Delay(400);
                latest = WitnessPad(pad);
                if (latest.VirtualInSlot0) return latest;
            }
            return latest;
        }

        var r2 = await ReconnectAndWitnessAsync();
        if (ShouldRetryEmptySlot0(r2, attempt: 0))
        {
            log("检测到玩家 1 实际空闲（虚拟手柄落入后序槽位），正在重新分配玩家 1…", LogLevel.Warn);
            await Task.Delay(500);
            r2 = await ReconnectAndWitnessAsync();
        }
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

    /// <summary>
    /// 只读探测（不重连、不禁用任何设备）：虚拟手柄当前是否仍占着玩家 1。
    /// 运行期巡检用——"改正"动作一律留给调用方决定，绝不在游戏运行中擅自惊动设备
    /// （手柄断开→重连会让 eFootball 把焦点挪到顶部的「手柄归属」图标，反而更糟）。
    /// </summary>
    public static WitnessResult Probe(IVirtualPad pad) => WitnessPad(pad);

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

