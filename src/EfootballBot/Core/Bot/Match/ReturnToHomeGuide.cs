namespace EfootballBot.Core.Bot.Match;

/// <summary>
/// 启动引导：挂机启动时若游戏停在其他模块的页面（不是主菜单、也不是本模块界面），
/// 先连续按 B 收敛回主菜单，再交给模块自己的主页导航流程。
///
/// 为什么需要：各模块原本只有"从主菜单出发"这一条导航链（主菜单→比赛页→磁贴定位→目标模块）。
/// 若启动时停在别的模块页（如在我的联赛页启动周活动），共享分类会把该页当成"前往比赛"类页面
/// 直接开赛，导致"互相都去不了，只有从主页这条路才通"。
///
/// 判定由调用方传入（本模块 Brain/共享分类的结果），本 helper 不引用任何 Scenarios 类型（架构锁）。
/// "本模块界面命中即跳过"保留了半途启动直接续接的既有能力，主页启动路径完全不进入引导。
/// </summary>
public sealed class ReturnToHomeGuide
{
    private readonly string _module;
    private readonly Action<string, Core.LogLevel> _log;
    private readonly Action<Core.Capture.FrameData, string, string> _dump; // (frame, tag, reason) 可疑帧收集
    private int _escapeAttempts;
    private bool _guiding;

    public ReturnToHomeGuide(string module, Action<string, Core.LogLevel> log, Action<Core.Capture.FrameData, string, string> dump)
    {
        _module = module;
        _log = log;
        _dump = dump;
    }

    /// <summary>
    /// 每轮在 RunOnceAsync 开头调用。
    /// </summary>
    /// <param name="isMainMenu">当前是否主菜单（含各服主页）——到主页即结束引导。</param>
    /// <param name="isOwnModulePage">当前是否本模块自己的界面——命中则跳过引导（半途续接）。</param>
    /// <param name="isOtherModulePage">当前是否其他模块的残留页——命中则进入/继续引导。</param>
    /// <param name="back">按 B 的动作。</param>
    /// <returns>本轮等待毫秒；null = 不接管，继续模块正常流程。</returns>
    public async Task<int?> StepAsync(Core.Capture.FrameData frame, bool isMainMenu, bool isOwnModulePage, bool isOtherModulePage, Func<Task> back)
    {
        if (isMainMenu)
        {
            if (_guiding) { _log($"[{_module}] 已收敛回主页，开始正常导航", Core.LogLevel.Success); _guiding = false; }
            _escapeAttempts = 0;
            return null;
        }
        if (isOwnModulePage)
        {
            // 半途启动落在自己模块页：保留既有续接能力，不踢回主页
            if (_guiding) { _guiding = false; _escapeAttempts = 0; }
            return null;
        }
        if (!isOtherModulePage && !_guiding)
            return null; // 既非主页也非残留页（如比赛中/加载页）：不接管，交原流程

        // 进入引导：连续 B 退，带预算防死循环
        _guiding = true;
        if (++_escapeAttempts > 6)
        {
            _log($"[{_module}] 残留页多次未退出，暂停 8s 后重试（若反复出现请回报日志）", Core.LogLevel.Error);
            _dump(frame, "home_guide_stuck", "残留页 B 退主页 6 次仍未离开");
            _escapeAttempts = 0;
            return 8000;
        }
        _dump(frame, "other_module_page", "启动时停在其他模块页，B 退主页");
        _log($"[{_module}] 其他模块残留页面，B 退回主页（{_escapeAttempts}/6）", Core.LogLevel.Info);
        await back();
        return 2400;
    }

    public void Reset() { _guiding = false; _escapeAttempts = 0; }
}
