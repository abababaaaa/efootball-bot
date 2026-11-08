using EfootballBot.Core.Capture;
using EfootballBot.Core.Config;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Bot.Scenarios.MyLeague;

namespace EfootballBot.Core.Bot.Scenarios.Stud;

/// <summary>
/// 种马球员培训（购买）状态机：一帧一步、屏幕验证后再按下一键。
/// <para>购买：主菜单 → 合约 → 普通球员 → X 排序（价格·升序）→ 选第 1（门将）/第 2（非门将）张
/// → 签下 → 付款确认上移 + A → 获得弹窗 A → 详情 A → 回列表重复。</para>
/// <para>培训：主菜单 → 我的球队 → 球员 → X 动作 → 筛选（最佳位置除 GK 外 12 类、
/// 培育→未完成培育、球员评价→最后一档）→ 完成 → 逐名球员 → X 球员动作 → 技巧训练 →
/// 确认当前空槽为蓝色加号 → 随机道具 → 执行训练课 → 记录结果大字 → B/B 回列表，循环到设定数量。</para>
/// </summary>
public sealed class StudFlowController
{
    private enum Phase
    {
        Idle,
        // 购买
        BuyGoContract,
        BuyGoNormal,
        BuySortOpen,
        BuySortNavigate,
        BuySortConfirm,
        BuySortClose,
        BuyPick,
        BuyDetail,
        BuyPayment,
        BuyAcquired,
        BuyPostAcquire,
        BuyAfterList,
        BuyDone,
        // 培训（筛选）
        TrainGoMyTeam,
        TrainGoPlayers,
        TrainOpenAction,
        TrainOpenFilter,
        TrainFilterReset,
        TrainFilterPosition,
        TrainPositionSelect,
        TrainFilterTraining,
        TrainTrainingSelect,
        TrainTrainingConfirm,
        TrainFilterRating,
        TrainRatingSelect,
        TrainFilterComplete,
        TrainApplied,
        TrainPickPlayer,
        TrainPlayerDetail,
        TrainPlayerActions,
        TrainSkillPage,
        TrainSkillItem,
        TrainSkillConfirm,
        TrainSkillResult,
        TrainSkillBackPage,
        TrainSkillBackDetail,
        TrainSkillBackList,
        TrainStopped,
    }

    private readonly BotEngine _engine;
    private readonly Action<string, LogLevel> _log;
    private readonly StudTrainingConfig _cfg;

    private Phase _phase;
    private StudScreen _lastScreen = StudScreen.Unknown;
    private int _stall;
    private int _rounds;
    private int _gkLeft;
    private int _outfieldLeft;
    private bool _buyingGk;
    private bool _acquiredCounted;
    private int _sortFlips;
    private bool _filterReset;
    private bool _positionStarted;
    private int _positionIndex;
    private bool _trainingSeekStarted;
    private int _trainingSeekSteps;
    private bool _ratingStarted;
    private bool _ratingMoved;
    private bool _ratingSelected;
    private int _trainRemaining;
    private bool _trainAll;
    private int _trainedRuns;
    private int _playerIndex;
    private bool _skillCompleted;
    private bool _skillResultRecorded;
    private bool _skillSlotEmpty;
    private int _slotProbeTries;
    private readonly Dictionary<string, int> _skillResults = new(StringComparer.Ordinal);

    private const int MaxRounds = 1500;
    private const int MaxStall = 45;

    public StudFlowController(BotEngine engine, Action<string, LogLevel> log, bool training)
    {
        _engine = engine;
        _log = log;
        _cfg = engine.Config.StudTraining;
        _gkLeft = Math.Clamp(_cfg.BuyGoalkeeperCount, 0, 999);
        _outfieldLeft = Math.Clamp(_cfg.BuyOutfieldCount, 0, 999);
        _trainAll = _cfg.SkillCardAll;
        _trainRemaining = _trainAll ? 0 : Math.Clamp(_cfg.SkillCardCount, 0, 999);
        _phase = training ? Phase.TrainGoMyTeam : Phase.BuyGoContract;
        _log(training
            ? "[种马培训] 开始：我的球队 → 球员 → 筛选（非门将/未完成培育/最低评价）"
            : $"[种马购买] 开始：门将 {_gkLeft} 个、非门将 {_outfieldLeft} 个", LogLevel.Info);
    }

    public async Task<int> StepAsync(Observation obs, CancellationToken ct)
    {
        if (++_rounds > MaxRounds)
            return Abort("流程轮数超限");

        var screen = StudTrainingBrain.Classify(obs.Ocr);
        if (screen != _lastScreen)
        {
            _lastScreen = screen;
            _stall = 0;
            if (screen != StudScreen.Unknown)
                _log($"[种马] 界面：{Describe(screen)}", LogLevel.Debug);
        }
        else if (++_stall > MaxStall)
        {
            return Abort($"在「{Describe(screen)}」停留过久");
        }

        return _phase switch
        {
            Phase.BuyGoContract => await BuyGoContractAsync(screen, ct),
            Phase.BuyGoNormal => await BuyGoNormalAsync(screen, ct),
            Phase.BuySortOpen => await BuySortOpenAsync(screen, ct),
            Phase.BuySortNavigate => await BuySortNavigateAsync(screen, ct),
            Phase.BuySortConfirm => await BuySortConfirmAsync(screen, obs.Frame, ct),
            Phase.BuySortClose => await BuySortCloseAsync(screen, ct),
            Phase.BuyPick => await BuyPickAsync(screen, ct),
            Phase.BuyDetail => await BuyDetailAsync(screen, ct),
            Phase.BuyPayment => await BuyPaymentAsync(screen, ct),
            Phase.BuyAcquired => await BuyAcquiredAsync(screen, ct),
            Phase.BuyPostAcquire => await BuyPostAcquireAsync(screen, ct),
            Phase.BuyAfterList => await BuyAfterListAsync(screen, ct),
            Phase.BuyDone => 0,

            Phase.TrainGoMyTeam => await TrainGoMyTeamAsync(screen, ct),
            Phase.TrainGoPlayers => await TrainGoPlayersAsync(screen, ct),
            Phase.TrainOpenAction => await TrainOpenActionAsync(screen, ct),
            Phase.TrainOpenFilter => await TrainOpenFilterAsync(screen, ct),
            Phase.TrainFilterReset => await TrainFilterResetAsync(screen, ct),
            Phase.TrainFilterPosition => await TrainFilterPositionAsync(screen, ct),
            Phase.TrainPositionSelect => await TrainPositionSelectAsync(screen, ct),
            Phase.TrainFilterTraining => await TrainFilterTrainingAsync(screen, ct),
            Phase.TrainTrainingSelect => await TrainTrainingSelectAsync(screen, obs, ct),
            Phase.TrainTrainingConfirm => await TrainTrainingConfirmAsync(screen, ct),
            Phase.TrainFilterRating => await TrainFilterRatingAsync(screen, ct),
            Phase.TrainRatingSelect => await TrainRatingSelectAsync(screen, ct),
            Phase.TrainFilterComplete => await TrainFilterCompleteAsync(screen, ct),
            Phase.TrainApplied => await TrainAppliedAsync(screen, obs, ct),
            Phase.TrainPickPlayer => await TrainPickPlayerAsync(screen, ct),
            Phase.TrainPlayerDetail => await TrainPlayerDetailAsync(screen, ct),
            Phase.TrainPlayerActions => await TrainPlayerActionsAsync(screen, ct),
            Phase.TrainSkillPage => await TrainSkillPageAsync(screen, obs.Frame, ct),
            Phase.TrainSkillItem => await TrainSkillItemAsync(screen, ct),
            Phase.TrainSkillConfirm => await TrainSkillConfirmAsync(screen, ct),
            Phase.TrainSkillResult => await TrainSkillResultAsync(screen, obs.Ocr, ct),
            Phase.TrainSkillBackPage => await TrainSkillBackPageAsync(screen, ct),
            Phase.TrainSkillBackDetail => await TrainSkillBackDetailAsync(screen, ct),
            Phase.TrainSkillBackList => await TrainSkillBackListAsync(screen, ct),
            Phase.TrainStopped => 0,
            _ => 800,
        };
    }

    // ================= 购买 =================

    private async Task<int> BuyGoContractAsync(StudScreen screen, CancellationToken ct)
    {
        if (screen == StudScreen.ContractHome)
        {
            _stall = 0;
            _phase = Phase.BuyGoNormal;
            return 300;
        }
        if (screen == StudScreen.ContractPlayerList)
        {
            _stall = 0;
            _phase = Phase.BuySortOpen;
            return 300;
        }
        if (screen != StudScreen.MainMenu)
            return await WaitAsync("等待游戏主菜单");

        _stall = 0;
        _log("[种马购买] 主菜单：下×2 → 左×6（比赛）→ 右×2（合约）→ A", LogLevel.Info);
        var pad = _engine.Pad;
        await pad.Dpad(PadDir.Down);
        await pad.Dpad(PadDir.Down);
        await pad.DpadRepeatFast(PadDir.Left, 6);
        await pad.DpadRepeatFast(PadDir.Right, 2);
        await pad.Confirm();
        return 1800;
    }

    private async Task<int> BuyGoNormalAsync(StudScreen screen, CancellationToken ct)
    {
        if (screen == StudScreen.ContractPlayerList)
        {
            _stall = 0;
            _phase = Phase.BuySortOpen;
            return 300;
        }
        if (screen == StudScreen.ContractHome)
        {
            _stall = 0;
            _log("[种马购买] 合约：左×9 归位 → 右×3（普通球员）→ A", LogLevel.Info);
            var pad = _engine.Pad;
            await pad.DpadRepeatFast(PadDir.Left, 9);
            await pad.DpadRepeatFast(PadDir.Right, 3);
            await pad.Confirm();
            return 1600;
        }
        if (screen == StudScreen.MainMenu)
        {
            _phase = Phase.BuyGoContract;
            return 200;
        }
        return await WaitAsync("等待合约首页");
    }

    private async Task<int> BuySortOpenAsync(StudScreen screen, CancellationToken ct)
    {
        if (screen == StudScreen.SortPopup)
        {
            _stall = 0;
            _phase = Phase.BuySortNavigate;
            return 300;
        }
        if (screen != StudScreen.ContractPlayerList)
            return await WaitAsync("等待普通球员列表");

        _stall = 0;
        _log("[种马购买] 普通球员列表：X 打开排序", LogLevel.Info);
        await _engine.Pad.X();
        _phase = Phase.BuySortNavigate;
        return 1400;
    }

    private async Task<int> BuySortNavigateAsync(StudScreen screen, CancellationToken ct)
    {
        if (screen == StudScreen.ContractPlayerList)
        {
            // 排序弹窗没打开/已经关掉：下一帧重新按 X。
            _phase = Phase.BuySortOpen;
            return 700;
        }
        if (screen != StudScreen.SortPopup)
            return await WaitAsync("等待排序弹窗");

        _stall = 0;
        _sortFlips = 0;
        _log("[种马购买] 排序：上×9 归位 → 下×3 选「价格」", LogLevel.Info);
        var pad = _engine.Pad;
        await pad.DpadRepeatFast(PadDir.Up, 9);
        await pad.DpadRepeatFast(PadDir.Down, 3);
        _phase = Phase.BuySortConfirm;
        return 900;
    }

    private async Task<int> BuySortConfirmAsync(StudScreen screen, FrameData frame, CancellationToken ct)
    {
        if (screen == StudScreen.ContractPlayerList)
        {
            _phase = Phase.BuyPick;
            return 300;
        }
        if (screen != StudScreen.SortPopup)
            return await WaitAsync("等待排序弹窗");

        _stall = 0;
        bool? up = MaxLevelSwapBrain.IsSortArrowUp(frame);
        if (up == false && _sortFlips < 2)
        {
            _sortFlips++;
            _log("[种马购买] 价格排序箭头朝下：按 Y 切为升序", LogLevel.Info);
            await _engine.Pad.Y();
            return 900;
        }
        if (up is null)
            _log("[种马购买] 排序箭头读不出：按升序继续（如实际为降序请反馈）", LogLevel.Warn);
        else if (up == false)
            _log("[种马购买] 翻方向两次后箭头仍朝下：按升序继续", LogLevel.Warn);

        _log("[种马购买] 已选「价格」+ 升序，A 应用", LogLevel.Info);
        await _engine.Pad.Confirm();
        _phase = Phase.BuySortClose;
        return 1000;
    }

    private async Task<int> BuySortCloseAsync(StudScreen screen, CancellationToken ct)
    {
        if (screen == StudScreen.ContractPlayerList)
        {
            _stall = 0;
            _phase = Phase.BuyPick;
            return 400;
        }
        if (screen == StudScreen.SortPopup)
        {
            _stall = 0;
            _log("[种马购买] 排序弹窗仍在：B 返回列表", LogLevel.Info);
            await _engine.Pad.Back();
            _phase = Phase.BuyPick;
            return 1000;
        }
        return await WaitAsync("等待排序弹窗关闭");
    }

    private async Task<int> BuyPickAsync(StudScreen screen, CancellationToken ct)
    {
        if (screen == StudScreen.SortPopup)
        {
            _phase = Phase.BuySortConfirm;
            return 300;
        }
        if (screen == StudScreen.ContractHome)
        {
            _phase = Phase.BuyGoNormal;
            return 300;
        }
        if (screen != StudScreen.ContractPlayerList)
            return await WaitAsync("等待普通球员列表");

        _stall = 0;
        if (!PrepareNextPurchase())
            return await BuyFinishAsync();

        _log(_buyingGk
            ? $"[种马购买] 买入门将种马（剩余 {_gkLeft} 个）：归顶 → 第 1 张 → A"
            : $"[种马购买] 买入非门将种马（剩余 {_outfieldLeft} 个）：归顶 → 下 1 格 → 第 2 张 → A", LogLevel.Info);

        var pad = _engine.Pad;
        await pad.DpadRepeatFast(PadDir.Up, 9);
        if (!_buyingGk)
            await pad.Dpad(PadDir.Down);
        await pad.Confirm();
        _acquiredCounted = false;
        _phase = Phase.BuyDetail;
        return 1500;
    }

    private async Task<int> BuyDetailAsync(StudScreen screen, CancellationToken ct)
    {
        if (screen is StudScreen.ContractDetail)
        {
            _stall = 0;
            _log("[种马购买] 商品详情：A 签下", LogLevel.Info);
            await _engine.Pad.Confirm();
            _phase = Phase.BuyPayment;
            return 1500;
        }
        if (screen == StudScreen.PaymentConfirm)
        {
            _phase = Phase.BuyPayment;
            return 300;
        }
        if (screen == StudScreen.AcquiredPopup)
        {
            _phase = Phase.BuyAcquired;
            return 300;
        }
        if (screen == StudScreen.ContractPlayerList)
        {
            // 签下链路已结束（可能是上一轮动画延迟）：回列表继续。
            _phase = Phase.BuyAfterList;
            return 300;
        }
        return await WaitAsync("等待球员详情");
    }

    private async Task<int> BuyPaymentAsync(StudScreen screen, CancellationToken ct)
    {
        if (screen == StudScreen.AcquiredPopup)
        {
            _phase = Phase.BuyAcquired;
            return 300;
        }
        if (screen == StudScreen.ContractDetail)
        {
            _phase = Phase.BuyDetail;
            return 300;
        }
        if (screen != StudScreen.PaymentConfirm)
            return await WaitAsync("等待付款确认弹窗");

        _stall = 0;
        _log("[种马购买] 付款确认：上移焦点（取消 → 0GP 确认）→ A", LogLevel.Info);
        var pad = _engine.Pad;
        await pad.Dpad(PadDir.Up);
        await pad.Confirm();
        _phase = Phase.BuyAcquired;
        return 1500;
    }

    private async Task<int> BuyAcquiredAsync(StudScreen screen, CancellationToken ct)
    {
        if (screen == StudScreen.PostAcquireDetail)
        {
            _phase = Phase.BuyPostAcquire;
            return 300;
        }
        if (screen == StudScreen.ContractPlayerList)
        {
            _phase = Phase.BuyAfterList;
            return 300;
        }
        if (screen != StudScreen.AcquiredPopup)
            return await WaitAsync("等待获得球员弹窗");

        _stall = 0;
        if (!_acquiredCounted)
        {
            _acquiredCounted = true;
            if (_buyingGk) _gkLeft--;
            else _outfieldLeft--;
            _log(_buyingGk
                ? $"[种马购买] 门将种马 +1（剩余 {_gkLeft} 个）"
                : $"[种马购买] 非门将种马 +1（剩余 {_outfieldLeft} 个）", LogLevel.Success);
        }
        _log("[种马购买] 获得弹窗：A 跳过", LogLevel.Info);
        await _engine.Pad.Confirm();
        _phase = Phase.BuyPostAcquire;
        return 1600;
    }

    private async Task<int> BuyPostAcquireAsync(StudScreen screen, CancellationToken ct)
    {
        if (screen == StudScreen.ContractPlayerList)
        {
            _stall = 0;
            _phase = Phase.BuyAfterList;
            return 300;
        }
        if (screen == StudScreen.AcquiredPopup)
        {
            _phase = Phase.BuyAcquired;
            return 300;
        }
        if (screen != StudScreen.PostAcquireDetail)
            return await WaitAsync("等待签下后的详情页");

        _stall = 0;
        _log("[种马购买] 签下后详情：A 下一步返回列表", LogLevel.Info);
        await _engine.Pad.Confirm();
        _phase = Phase.BuyAfterList;
        return 1800;
    }

    private async Task<int> BuyAfterListAsync(StudScreen screen, CancellationToken ct)
    {
        if (screen == StudScreen.ContractHome)
        {
            _phase = Phase.BuyGoNormal;
            return 300;
        }
        if (screen == StudScreen.ContractPlayerList)
        {
            _stall = 0;
            if (!PrepareNextPurchase())
                return await BuyFinishAsync();
            _phase = Phase.BuyPick;
            return 600;
        }
        if (screen == StudScreen.PostAcquireDetail)
        {
            _phase = Phase.BuyPostAcquire;
            return 300;
        }
        if (screen == StudScreen.AcquiredPopup)
        {
            _phase = Phase.BuyAcquired;
            return 300;
        }
        return await WaitAsync("等待返回普通球员列表");
    }

    private bool PrepareNextPurchase()
    {
        if (_gkLeft > 0)
        {
            _buyingGk = true;
            return true;
        }
        if (_outfieldLeft > 0)
        {
            _buyingGk = false;
            return true;
        }
        return false;
    }

    private async Task<int> BuyFinishAsync()
    {
        _phase = Phase.BuyDone;
        _log("[种马购买] 全部买入完成，停止挂机", LogLevel.Success);
        _engine.Stop();
        await Task.CompletedTask;
        return 0;
    }

    // ================= 培训（筛选） =================

    private async Task<int> TrainGoMyTeamAsync(StudScreen screen, CancellationToken ct)
    {
        if (screen == StudScreen.MyTeamHome)
        {
            _phase = Phase.TrainGoPlayers;
            return 300;
        }
        if (screen == StudScreen.SquadList)
        {
            _phase = Phase.TrainOpenAction;
            return 300;
        }
        if (screen == StudScreen.ActionMenu)
        {
            _phase = Phase.TrainOpenFilter;
            return 300;
        }
        if (screen == StudScreen.Filter)
        {
            _phase = Phase.TrainFilterReset;
            return 300;
        }
        if (screen != StudScreen.MainMenu)
            return await WaitAsync("等待游戏主菜单");

        _stall = 0;
        _log("[种马培训] 主菜单：下×2 → 左×6（比赛）→ 右×1（我的球队）→ A", LogLevel.Info);
        var pad = _engine.Pad;
        await pad.Dpad(PadDir.Down);
        await pad.Dpad(PadDir.Down);
        await pad.DpadRepeatFast(PadDir.Left, 6);
        await pad.DpadRepeatFast(PadDir.Right, 1);
        await pad.Confirm();
        return 1800;
    }

    private async Task<int> TrainGoPlayersAsync(StudScreen screen, CancellationToken ct)
    {
        if (screen == StudScreen.SquadList)
        {
            _phase = Phase.TrainOpenAction;
            return 300;
        }
        if (screen == StudScreen.ActionMenu)
        {
            _phase = Phase.TrainOpenFilter;
            return 300;
        }
        if (screen == StudScreen.Filter)
        {
            _phase = Phase.TrainFilterReset;
            return 300;
        }
        if (screen != StudScreen.MyTeamHome)
            return await WaitAsync("等待我的球队首页");

        _stall = 0;
        _log("[种马培训] 我的球队：左×9 归位 → 右×1（球员）→ A", LogLevel.Info);
        var pad = _engine.Pad;
        await pad.DpadRepeatFast(PadDir.Left, 9);
        await pad.DpadRepeatFast(PadDir.Right, 1);
        await pad.Confirm();
        return 1600;
    }

    private async Task<int> TrainOpenActionAsync(StudScreen screen, CancellationToken ct)
    {
        if (screen == StudScreen.ActionMenu)
        {
            _phase = Phase.TrainOpenFilter;
            return 300;
        }
        if (screen == StudScreen.Filter)
        {
            _phase = Phase.TrainFilterReset;
            return 300;
        }
        if (screen != StudScreen.SquadList)
            return await WaitAsync("等待全部球员列表");

        _stall = 0;
        _log("[种马培训] 全部球员：X 打开动作", LogLevel.Info);
        await _engine.Pad.X();
        _phase = Phase.TrainOpenFilter;
        return 1300;
    }

    private async Task<int> TrainOpenFilterAsync(StudScreen screen, CancellationToken ct)
    {
        if (screen == StudScreen.Filter)
        {
            _phase = Phase.TrainFilterReset;
            return 300;
        }
        if (screen != StudScreen.ActionMenu)
            return await WaitAsync("等待动作侧栏");

        _stall = 0;
        _log("[种马培训] 动作：下×2（筛选）→ A", LogLevel.Info);
        var pad = _engine.Pad;
        await pad.Dpad(PadDir.Down);
        await pad.Dpad(PadDir.Down);
        await pad.Confirm();
        _phase = Phase.TrainFilterReset;
        return 1500;
    }

    private async Task<int> TrainFilterResetAsync(StudScreen screen, CancellationToken ct)
    {
        if (screen != StudScreen.Filter)
        {
            if (screen == StudScreen.PositionFilter) { _phase = Phase.TrainPositionSelect; return 300; }
            if (screen == StudScreen.TrainingFilter) { _phase = Phase.TrainTrainingSelect; return 300; }
            if (screen == StudScreen.RatingFilter) { _phase = Phase.TrainRatingSelect; return 300; }
            return await WaitAsync("等待筛选页");
        }

        _stall = 0;
        if (!_filterReset)
        {
            _filterReset = true;
            _log("[种马培训] 筛选页：Y 重设，确保本轮从空筛选开始", LogLevel.Info);
            await _engine.Pad.Y();
            return 1100;
        }
        _phase = _cfg.TrainGoalkeepers ? Phase.TrainFilterTraining : Phase.TrainFilterPosition;
        return 300;
    }

    private async Task<int> TrainFilterPositionAsync(StudScreen screen, CancellationToken ct)
    {
        if (screen == StudScreen.PositionFilter)
        {
            _phase = Phase.TrainPositionSelect;
            return 300;
        }
        if (screen != StudScreen.Filter)
            return await WaitAsync("等待筛选页/最佳位置");

        _stall = 0;
        _log("[种马培训] 筛选：上×9 归位 → 下×3（最佳位置）→ A", LogLevel.Info);
        var pad = _engine.Pad;
        await pad.DpadRepeatFast(PadDir.Up, 9);
        await pad.DpadRepeatFast(PadDir.Down, 3);
        await pad.Confirm();
        _phase = Phase.TrainPositionSelect;
        return 1400;
    }

    private async Task<int> TrainPositionSelectAsync(StudScreen screen, CancellationToken ct)
    {
        if (screen == StudScreen.Filter)
        {
            // A 已应用并返回筛选：直接进入下一项。
            _positionStarted = false;
            _phase = Phase.TrainFilterTraining;
            return 500;
        }
        if (screen != StudScreen.PositionFilter)
            return await WaitAsync("等待最佳位置子页");

        _stall = 0;
        var pad = _engine.Pad;
        if (!_positionStarted)
        {
            _positionStarted = true;
            _positionIndex = 0;
            _log("[种马培训] 最佳位置：归顶到 GK（不选 GK），随后逐项选择其余 12 类", LogLevel.Info);
            await pad.DpadRepeatFast(PadDir.Up, 9);
            return 700;
        }

        if (_positionIndex < 12)
        {
            await pad.Dpad(PadDir.Down);
            await pad.Confirm();
            _positionIndex++;
            if (_positionIndex == 12)
                _log("[种马培训] 最佳位置：已选择 12 类（GK 除外），B 返回筛选", LogLevel.Info);
            return 800;
        }

        await pad.Back();
        _positionStarted = false;
        _phase = Phase.TrainFilterTraining;
        return 1300;
    }

    private async Task<int> TrainFilterTrainingAsync(StudScreen screen, CancellationToken ct)
    {
        if (screen == StudScreen.TrainingFilter)
        {
            _phase = Phase.TrainTrainingSelect;
            return 300;
        }
        if (screen != StudScreen.Filter)
            return await WaitAsync("等待筛选页/培育");

        _stall = 0;
        _log("[种马培训] 筛选：上×9 归位 → 下×9（培育）→ A", LogLevel.Info);
        var pad = _engine.Pad;
        await pad.DpadRepeatFast(PadDir.Up, 9);
        await pad.DpadRepeatFast(PadDir.Down, 9);
        await pad.Confirm();
        _phase = Phase.TrainTrainingSelect;
        return 1400;
    }

    private async Task<int> TrainTrainingSelectAsync(StudScreen screen, Observation obs, CancellationToken ct)
    {
        if (screen == StudScreen.Filter)
        {
            _trainingSeekStarted = false;
            _phase = Phase.TrainFilterRating;
            return 500;
        }
        if (screen != StudScreen.TrainingFilter)
            return await WaitAsync("等待培育子页");

        _stall = 0;
        var pad = _engine.Pad;
        if (!_trainingSeekStarted)
        {
            _trainingSeekStarted = true;
            _trainingSeekSteps = 0;
            _log("[种马培训] 培育：归顶后逐行寻找「未完成培育」", LogLevel.Info);
            await pad.DpadRepeatFast(PadDir.Up, 9);
            return 700;
        }

        string? row = StudTrainingBrain.SelectedRightRowText(obs.Ocr, obs.Frame);
        if (row is not null && row.Contains("未完成培育", StringComparison.Ordinal))
        {
            _log("[种马培训] 培育：已选中「未完成培育」→ A", LogLevel.Info);
            await pad.Confirm();
            _phase = Phase.TrainTrainingConfirm;
            return 1000;
        }

        if (_trainingSeekSteps++ >= 15)
        {
            _log("[种马培训] 培育：未找到「未完成培育」，B 返回并跳过该项（请反馈实机筛选截图）", LogLevel.Warn);
            await pad.Back();
            _trainingSeekStarted = false;
            _phase = Phase.TrainFilterRating;
            return 1200;
        }

        await pad.Dpad(PadDir.Down);
        return 700;
    }

    private async Task<int> TrainTrainingConfirmAsync(StudScreen screen, CancellationToken ct)
    {
        if (screen == StudScreen.Filter)
        {
            _trainingSeekStarted = false;
            _phase = Phase.TrainFilterRating;
            return 300;
        }
        if (screen != StudScreen.TrainingFilter)
            return await WaitAsync("等待培育子页关闭");

        _stall = 0;
        await _engine.Pad.Back();
        _trainingSeekStarted = false;
        _phase = Phase.TrainFilterRating;
        return 1200;
    }

    private async Task<int> TrainFilterRatingAsync(StudScreen screen, CancellationToken ct)
    {
        if (screen == StudScreen.RatingFilter)
        {
            _phase = Phase.TrainRatingSelect;
            return 300;
        }
        if (screen != StudScreen.Filter)
            return await WaitAsync("等待筛选页/球员评价");

        _stall = 0;
        _log("[种马培训] 筛选：上×9 归位 → 下×10（球员评价）→ A", LogLevel.Info);
        var pad = _engine.Pad;
        await pad.DpadRepeatFast(PadDir.Up, 9);
        await pad.DpadRepeatFast(PadDir.Down, 10);
        await pad.Confirm();
        _phase = Phase.TrainRatingSelect;
        return 1400;
    }

    private async Task<int> TrainRatingSelectAsync(StudScreen screen, CancellationToken ct)
    {
        if (screen == StudScreen.Filter)
        {
            _ratingStarted = false;
            _phase = Phase.TrainFilterComplete;
            return 500;
        }
        if (screen != StudScreen.RatingFilter)
            return await WaitAsync("等待球员评价子页");

        _stall = 0;
        var pad = _engine.Pad;
        if (!_ratingStarted)
        {
            _ratingStarted = true;
            _ratingMoved = false;
            _ratingSelected = false;
            _log("[种马培训] 球员评价：归顶 → 下×4（最后一档，一颗星）", LogLevel.Info);
            await pad.DpadRepeatFast(PadDir.Up, 9);
            return 700;
        }
        if (!_ratingMoved)
        {
            _ratingMoved = true;
            await pad.DpadRepeatFast(PadDir.Down, 4);
            return 700;
        }
        if (!_ratingSelected)
        {
            _ratingSelected = true;
            await pad.Confirm();
            return 900;
        }

        await pad.Back();
        _ratingStarted = false;
        _phase = Phase.TrainFilterComplete;
        return 1200;
    }

    private async Task<int> TrainFilterCompleteAsync(StudScreen screen, CancellationToken ct)
    {
        if (screen == StudScreen.SquadList)
        {
            _phase = Phase.TrainApplied;
            return 300;
        }
        if (screen != StudScreen.Filter)
            return await WaitAsync("等待筛选页完成");

        _stall = 0;
        _log("[种马培训] 筛选完成：下移光标至「完成」→ A 应用", LogLevel.Info);
        var pad = _engine.Pad;
        await pad.DpadRepeatFast(PadDir.Down, 20);
        await pad.Confirm();
        _phase = Phase.TrainApplied;
        return 1600;
    }

    private async Task<int> TrainAppliedAsync(StudScreen screen, Observation obs, CancellationToken ct)
    {
        if (screen == StudScreen.Filter)
        {
            // A 没点到「完成」：再下移饱和后按一次。
            _stall = 0;
            await _engine.Pad.DpadRepeatFast(PadDir.Down, 8);
            await _engine.Pad.Confirm();
            return 1400;
        }
        if (screen != StudScreen.SquadList)
            return await WaitAsync("等待筛选后的全部球员列表");

        if (_trainRemaining == 0 && !_trainAll)
        {
            _phase = Phase.TrainStopped;
            _log("[种马培训] 筛选已应用：非门将 + 未完成培育 + 最低评价。" +
                 "技能卡数量为 0，仅筛选不培训，本次运行结束。", LogLevel.Success);
            _engine.Stop();
            return 0;
        }

        if (_trainAll)
        {
            int visible = StudTrainingBrain.ReadVisiblePlayerCount(obs.Ocr) ?? 50;
            _trainRemaining = Math.Clamp(visible, 0, 50);
            if (_trainRemaining == 0)
            {
                _phase = Phase.TrainStopped;
                _log("[种马培训] 筛选后没有可培训球员，本次运行结束。", LogLevel.Warn);
                _engine.Stop();
                return 0;
            }
            _log($"[种马培训] 技能卡选择「全部」：本次将处理筛选后的 {_trainRemaining} 名球员", LogLevel.Info);
        }
        else
        {
            // 设定数量大于筛选结果时按实际人数收敛，避免列表走到头还在按 A。
            int visible = StudTrainingBrain.ReadVisiblePlayerCount(obs.Ocr) ?? int.MaxValue;
            if (visible < _trainRemaining)
            {
                _trainRemaining = Math.Max(0, visible);
                _log($"[种马培训] 筛选结果只有 {_trainRemaining} 名，按实际人数培训", LogLevel.Info);
            }
            if (_trainRemaining == 0)
            {
                _phase = Phase.TrainStopped;
                _log("[种马培训] 筛选后没有可培训球员，本次运行结束。", LogLevel.Warn);
                _engine.Stop();
                return 0;
            }
        }

        _phase = Phase.TrainPickPlayer;
        return 500;
    }

    private async Task<int> TrainPickPlayerAsync(StudScreen screen, CancellationToken ct)
    {
        if (screen == StudScreen.TrainingPlayerDetail)
        {
            _phase = Phase.TrainPlayerDetail;
            return 300;
        }
        if (screen != StudScreen.SquadList)
        {
            if (screen == StudScreen.Filter) { _phase = Phase.TrainApplied; return 300; }
            return await WaitAsync("等待筛选后的全部球员列表");
        }

        _stall = 0;
        _skillResultRecorded = false;
        _skillSlotEmpty = false;
        _skillCompleted = false;
        _slotProbeTries = 0;
        _log(_trainRemaining == int.MaxValue
            ? $"[种马培训] 打开第 {_playerIndex + 1} 名球员（全部）"
            : $"[种马培训] 打开第 {_playerIndex + 1}/{_playerIndex + _trainRemaining} 名球员", LogLevel.Info);

        var pad = _engine.Pad;
        await pad.DpadRepeatFast(PadDir.Up, 9);
        if (_playerIndex > 0)
            await pad.DpadRepeatFast(PadDir.Down, _playerIndex);
        await pad.Confirm();
        _phase = Phase.TrainPlayerDetail;
        return 1500;
    }

    private async Task<int> TrainPlayerDetailAsync(StudScreen screen, CancellationToken ct)
    {
        if (screen == StudScreen.PlayerActions)
        {
            _phase = Phase.TrainPlayerActions;
            return 300;
        }
        if (screen != StudScreen.TrainingPlayerDetail)
        {
            if (screen == StudScreen.SquadList)
            {
                // A 没进详情：回选人重试。
                _phase = Phase.TrainPickPlayer;
                return 500;
            }
            return await WaitAsync("等待培训球员详情");
        }

        _stall = 0;
        _log("[种马培训] 球员详情：X 打开球员动作", LogLevel.Info);
        await _engine.Pad.X();
        _phase = Phase.TrainPlayerActions;
        return 1200;
    }

    private async Task<int> TrainPlayerActionsAsync(StudScreen screen, CancellationToken ct)
    {
        if (screen == StudScreen.SkillTraining)
        {
            _phase = Phase.TrainSkillPage;
            return 300;
        }
        if (screen != StudScreen.PlayerActions)
        {
            if (screen == StudScreen.TrainingPlayerDetail)
            {
                _phase = Phase.TrainPlayerDetail;
                return 300;
            }
            return await WaitAsync("等待球员动作侧栏");
        }

        _stall = 0;
        _log("[种马培训] 球员动作：上×9 归位 → 下×2（技巧训练）→ A", LogLevel.Info);
        var pad = _engine.Pad;
        await pad.DpadRepeatFast(PadDir.Up, 9);
        await pad.DpadRepeatFast(PadDir.Down, 2);
        await pad.Confirm();
        _phase = Phase.TrainSkillPage;
        return 1300;
    }

    private async Task<int> TrainSkillPageAsync(StudScreen screen, FrameData frame, CancellationToken ct)
    {
        if (screen == StudScreen.SkillItemSelect)
        {
            _phase = Phase.TrainSkillItem;
            return 300;
        }
        if (screen != StudScreen.SkillTraining)
        {
            if (screen == StudScreen.PlayerActions)
            {
                _phase = Phase.TrainPlayerActions;
                return 300;
            }
            return await WaitAsync("等待技巧训练页");
        }

        _stall = 0;
        if (!_skillSlotEmpty)
        {
            _skillSlotEmpty = StudTrainingBrain.HasEmptySkillSlot(frame);
            if (!_skillSlotEmpty && ++_slotProbeTries <= 3)
                return 500; // 过场帧未渲染完，重探
            if (!_skillSlotEmpty)
            {
                _log("[种马培训] 5 个附加技巧槽都没有蓝色加号（已满/已学）：B 退出，换下一名；" +
                     $"蓝色像素={string.Join("/", StudTrainingBrain.SkillSlotBlueCounts(frame))}", LogLevel.Warn);
                await _engine.Pad.Back();
                _phase = Phase.TrainSkillBackDetail;
                return 1200;
            }
        }

        _log("[种马培训] 技巧训练：当前空槽为蓝色加号，A 下一步", LogLevel.Info);
        await _engine.Pad.Confirm();
        _phase = Phase.TrainSkillItem;
        return 1200;
    }

    private async Task<int> TrainSkillItemAsync(StudScreen screen, CancellationToken ct)
    {
        if (screen == StudScreen.SkillConfirm)
        {
            _phase = Phase.TrainSkillConfirm;
            return 300;
        }
        if (screen != StudScreen.SkillItemSelect)
        {
            if (screen == StudScreen.SkillTraining)
            {
                _phase = Phase.TrainSkillPage;
                return 300;
            }
            return await WaitAsync("等待选择要使用的物品");
        }

        _stall = 0;
        _log("[种马培训] 选择物品：上 → 右（随机）→ A", LogLevel.Info);
        var pad = _engine.Pad;
        await pad.Dpad(PadDir.Up);
        await pad.Dpad(PadDir.Right);
        await pad.Confirm();
        _phase = Phase.TrainSkillConfirm;
        return 1300;
    }

    private async Task<int> TrainSkillConfirmAsync(StudScreen screen, CancellationToken ct)
    {
        if (screen == StudScreen.SkillResult)
        {
            _phase = Phase.TrainSkillResult;
            return 300;
        }
        if (screen != StudScreen.SkillConfirm)
        {
            if (screen == StudScreen.SkillItemSelect)
            {
                _phase = Phase.TrainSkillItem;
                return 300;
            }
            return await WaitAsync("等待执行训练课弹窗");
        }

        _stall = 0;
        _log("[种马培训] 执行训练课：上移焦点 → A 确认", LogLevel.Info);
        var pad = _engine.Pad;
        await pad.Dpad(PadDir.Up);
        await pad.Confirm();
        _phase = Phase.TrainSkillResult;
        return 1500;
    }

    private async Task<int> TrainSkillResultAsync(StudScreen screen, OcrResult ocr, CancellationToken ct)
    {
        if (screen == StudScreen.SkillTraining)
        {
            _phase = Phase.TrainSkillBackPage;
            return 300;
        }
        if (screen == StudScreen.SkillConfirm)
        {
            _phase = Phase.TrainSkillConfirm;
            return 300;
        }
        if (screen == StudScreen.SkillItemSelect)
        {
            _phase = Phase.TrainSkillItem;
            return 300;
        }
        if (screen != StudScreen.SkillResult)
            return await WaitAsync("等待训练结果弹窗");

        _stall = 0;
        if (!_skillResultRecorded)
        {
            _skillResultRecorded = true;
            _skillCompleted = true;
            string name = StudTrainingBrain.ReadSkillResultName(ocr) ?? "未知技巧";
            _skillResults[name] = _skillResults.GetValueOrDefault(name) + 1;
            _log($"[种马培训] 训练结果：{name}（累计消耗随机道具 {_trainedRuns + 1} 个）", LogLevel.Success);
        }
        _log("[种马培训] 结果弹窗：A 关闭", LogLevel.Info);
        await _engine.Pad.Confirm();
        _phase = Phase.TrainSkillBackPage;
        return 1600;
    }

    private async Task<int> TrainSkillBackPageAsync(StudScreen screen, CancellationToken ct)
    {
        if (screen == StudScreen.TrainingPlayerDetail)
        {
            _phase = Phase.TrainSkillBackDetail;
            return 300;
        }
        if (screen == StudScreen.SkillResult)
        {
            _phase = Phase.TrainSkillResult;
            return 300;
        }
        if (screen == StudScreen.PlayerActions)
        {
            _log("[种马培训] B 先退回球员详情", LogLevel.Info);
            await _engine.Pad.Back();
            _phase = Phase.TrainSkillBackDetail;
            return 1000;
        }
        if (screen != StudScreen.SkillTraining)
            return await WaitAsync("等待技巧训练页（训练后）");

        _stall = 0;
        _log("[种马培训] 技巧训练完成，附加技巧已写入：B 返回球员详情", LogLevel.Info);
        await _engine.Pad.Back();
        _phase = Phase.TrainSkillBackDetail;
        return 1200;
    }

    private async Task<int> TrainSkillBackDetailAsync(StudScreen screen, CancellationToken ct)
    {
        if (screen == StudScreen.SquadList)
        {
            _phase = Phase.TrainSkillBackList;
            return 300;
        }
        if (screen != StudScreen.TrainingPlayerDetail)
        {
            if (screen == StudScreen.SkillTraining)
            {
                _phase = Phase.TrainSkillBackPage;
                return 300;
            }
            if (screen == StudScreen.PlayerActions)
            {
                await _engine.Pad.Back();
                return 1000;
            }
            return await WaitAsync("等待返回球员详情");
        }

        _stall = 0;
        _log("[种马培训] 球员详情：B 返回全部球员列表", LogLevel.Info);
        await _engine.Pad.Back();
        _phase = Phase.TrainSkillBackList;
        return 1200;
    }

    private async Task<int> TrainSkillBackListAsync(StudScreen screen, CancellationToken ct)
    {
        if (screen != StudScreen.SquadList)
        {
            if (screen == StudScreen.TrainingPlayerDetail)
            {
                _phase = Phase.TrainSkillBackDetail;
                return 300;
            }
            if (screen == StudScreen.SkillTraining)
            {
                _phase = Phase.TrainSkillBackPage;
                return 300;
            }
            return await WaitAsync("等待返回全部球员列表");
        }

        _stall = 0;
        _playerIndex++;
        if (_skillCompleted)
        {
            _trainedRuns++;
            if (_trainRemaining > 0) _trainRemaining--;
            _log($"[种马培训] 已完成 {_trainedRuns} 名球员的技巧训练" +
                 (_trainRemaining > 0 ? $"，剩余 {_trainRemaining} 名" : ""), LogLevel.Success);
        }
        else
        {
            _log("[种马培训] 本名球员未执行训练（已跳过），继续下一名", LogLevel.Warn);
        }
        _skillCompleted = false;

        if (_trainRemaining <= 0)
            return await FinishTrainingAsync();

        _phase = Phase.TrainPickPlayer;
        return 800;
    }

    private async Task<int> FinishTrainingAsync()
    {
        _phase = Phase.TrainStopped;
        string detail = _skillResults.Count == 0
            ? "未识别到技巧名"
            : string.Join("；", _skillResults.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} ×{kv.Value}"));
        _log($"[种马培训] 本次培训结束：共消耗随机道具 {_trainedRuns} 个；{detail}", LogLevel.Success);
        _engine.Stop();
        await Task.CompletedTask;
        return 0;
    }

    // ================= 公共 =================

    private async Task<int> WaitAsync(string what)
    {
        if (_stall == 0)
            _log($"[种马] {what}…", LogLevel.Debug);
        await Task.CompletedTask;
        return 900;
    }

    private int Abort(string reason)
    {
        _log($"[种马] {reason}，已停止挂机；请把当前画面截图与日志发到反馈群", LogLevel.Error);
        _engine.Stop();
        return 0;
    }

    private static string Describe(StudScreen s) => s switch
    {
        StudScreen.MainMenu => "游戏主菜单",
        StudScreen.ContractHome => "合约首页",
        StudScreen.ContractPlayerList => "普通球员列表",
        StudScreen.SortPopup => "排序弹窗",
        StudScreen.ContractDetail => "普通球员详情",
        StudScreen.PaymentConfirm => "付款确认",
        StudScreen.AcquiredPopup => "获得球员弹窗",
        StudScreen.PostAcquireDetail => "签下后详情",
        StudScreen.MyTeamHome => "我的球队",
        StudScreen.SquadList => "全部球员列表",
        StudScreen.ActionMenu => "动作侧栏",
        StudScreen.Filter => "筛选页",
        StudScreen.PositionFilter => "最佳位置子页",
        StudScreen.TrainingFilter => "培育子页",
        StudScreen.RatingFilter => "球员评价子页",
        StudScreen.TrainingPlayerDetail => "培训球员详情",
        StudScreen.PlayerActions => "球员动作侧栏",
        StudScreen.SkillTraining => "技巧训练页",
        StudScreen.SkillItemSelect => "选择要使用的物品",
        StudScreen.SkillConfirm => "执行训练课弹窗",
        StudScreen.SkillResult => "训练结果弹窗",
        _ => "未知界面",
    };
}
