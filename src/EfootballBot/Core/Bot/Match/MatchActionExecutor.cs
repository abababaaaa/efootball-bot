using EfootballBot.Core.Input;

// ===========================================================================
// 架构锁 · 共享层 Core/Bot/Match（v7.2）
// 本层职责：所有模块共用的能力 —— 界面分类、共享决策、赛中匹配 MatchPlay、流程记忆、像素探针、弹窗判定。
// 硬约束（构建期 tools/arch-guard/arch-guard.ps1 强制，违反即编译失败）：
//   · 本层不许 using / 引用 Core/Bot/Scenarios.*（共享层绝不能反向依赖模块）
//   · 场次记账（MatchesDone/KeyMatches）与比赛内挂机节奏只在本层实现，模块不许自己抄一份
// ===========================================================================

namespace EfootballBot.Core.Bot.Match;

/// <summary>
/// 共享手柄动作分发与主菜单进「比赛」磁贴导航：
/// 把此前在 5 个模块里逐字复制的 <c>switch (action.Kind)</c> 与主菜单定位逻辑收敛到此一处，
/// 改一次全模块生效，避免修补一处漏改四处。
/// </summary>
public static class MatchActionExecutor
{
    /// <summary>
    /// 主菜单进「比赛」磁贴：下×5 进底部卡行 → OCR 定位「比赛」格位（区分第 1 格 0 与未读全 null）
    /// → 饱和左移归位 → 按需右移 → A。
    /// </summary>
    public static async Task EnterMatchFromMainMenuAsync(
        BotEngine engine, Observation obs, Action<string, LogLevel> log, string module)
    {
        await engine.Pad.DpadRepeat(PadDir.Down, 5); // 饱和下按到底部卡行（到底停住，防下少了按错）
        if (HubTileLocator.FindMainMenuMatchOffset(obs.Ocr) is int matchOff)
        {
            string rightPart = matchOff > 0 ? $"→右×{matchOff}" : "";
            log($"[{module}] 主菜单导航：下×5→归位最左{rightPart}→A（OCR 定位比赛在第 {matchOff + 1} 格）", LogLevel.Info);
            await engine.Pad.DpadRepeat(PadDir.Left, 6);
            if (matchOff > 0) await engine.Pad.DpadRepeat(PadDir.Right, matchOff);
        }
        else
        {
            log($"[{module}] 主菜单导航：下×5→左×6→A（OCR 未读全底部磁贴，回退饱和归位）", LogLevel.Info);
            engine.DumpSuspiciousFrame(obs.Frame, "menu_tile_ocr_incomplete", "主菜单底部磁贴 OCR 读不全，回退固定左×6 导航");
            await engine.Pad.DpadRepeat(PadDir.Left, 6);
        }
        await engine.Pad.Confirm();
    }

    /// <summary>
    /// 执行 <see cref="MatchBrain.Decide"/> 返回的共享动作；支持场景通过委托覆盖
    /// <see cref="MatchActionKind.PadEnterMatch"/> 与 <see cref="MatchActionKind.PadConfirm"/>
    /// （如国服 RT→A→鼠标、我的联赛盲归位等）。委托返回非 null 时直接作为本轮延时返回，
    /// 返回 null 时继续返回 <see cref="MatchAction.WaitMs"/>。
    /// </summary>
    public static async Task<int> ExecuteAsync(
        BotEngine engine,
        MatchPlay matchPlay,
        MatchAction action,
        Observation obs,
        Action<string, LogLevel> log,
        string module,
        CancellationToken ct,
        Func<Task<int?>>? onEnterMatch = null,
        Func<Task<int?>>? onConfirm = null,
        string? backLogOverride = null,
        string? moveRightLog = null)
    {
        switch (action.Kind)
        {
            case MatchActionKind.PadEnterMatch:
                if (onEnterMatch is not null)
                {
                    if (await onEnterMatch() is int enterWait) return enterWait;
                    break;
                }
                await EnterMatchFromMainMenuAsync(engine, obs, log, module);
                break;

            case MatchActionKind.PadConfirm:
                if (onConfirm is not null)
                {
                    if (await onConfirm() is int confirmWait) return confirmWait;
                    break;
                }
                log($"[{module}] 手柄 A：{action.Target}", LogLevel.Info);
                await engine.Pad.Confirm();
                break;

            case MatchActionKind.PadUpConfirm:
                log($"[{module}] 弹窗上移确认：{action.Target}", LogLevel.Info);
                await engine.Pad.Dpad(PadDir.Up);
                await Task.Delay(250, ct); // 上移后留时给游戏刷新焦点，否则 A 落在旧焦点(取消)
                await engine.Pad.Confirm();
                break;

            case MatchActionKind.PadDownConfirm:
                log($"[{module}] 弹窗下移确认：{action.Target}", LogLevel.Info);
                await engine.Pad.Dpad(PadDir.Down);
                await engine.Pad.Confirm();
                break;

            case MatchActionKind.PadStartPress:
                log($"[{module}] {action.Target}", LogLevel.Info);
                await engine.Pad.Start();
                break;

            case MatchActionKind.PadXPress:
                log($"[{module}] {action.Target}", LogLevel.Info);
                await engine.Pad.X();
                break;

            case MatchActionKind.PadLoginNav:
                // eFootball 2027 标题页与未登录页共用登录序列：下→右→A（A 落在「登录」上）
                log($"[{module}] {action.Target}", LogLevel.Info);
                await engine.Pad.Dpad(PadDir.Down);
                await engine.Pad.Dpad(PadDir.Right);
                await engine.Pad.Confirm();
                break;

            case MatchActionKind.GamepadBack:
                log($"[{module}] {backLogOverride ?? action.Target}", LogLevel.Warn);
                await engine.Pad.Back();
                break;

            case MatchActionKind.GamepadBackAfterConfirm:
                log($"[{module}] {action.Target}", LogLevel.Warn);
                await engine.Pad.Dpad(PadDir.Up); // B 多次无效：先上移把焦点从「取消」移到确认键
                await engine.Pad.Confirm(); // 再 A 确认（直接 A 可能落在取消键上继续死循环）
                await engine.Pad.Back(); // 最后 B 返回
                break;

            case MatchActionKind.MatchIdleTap:
                // 比赛中挂机点击：按 A + 用户「挂机节奏」区间随机间隔（共享层 MatchPlay，拟人化）
                return await matchPlay.IdleTapAsync(ct);

            case MatchActionKind.PadMoveRight:
                if (!string.IsNullOrEmpty(moveRightLog))
                    log($"[{module}] {moveRightLog}", LogLevel.Info);
                await engine.Pad.Dpad(PadDir.Right);
                break;
        }
        return action.WaitMs;
    }
}
