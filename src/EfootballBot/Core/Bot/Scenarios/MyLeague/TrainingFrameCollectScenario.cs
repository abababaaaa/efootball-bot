using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;

// ===========================================================================
// 架构锁 · 模块层 Core/Bot/Scenarios（v7.2）
// 本场景只借用共享层 MatchBrain / HubTileLocator 的判据与 Pad 的按键，
// 不引用任何别的 *Scenario 类型，符合「模块之间不许互相引用」约束。
// ===========================================================================

namespace EfootballBot.Core.Bot.Scenarios.MyLeague;

/// <summary>
/// 训练帧主动采集场景（v8.1.1，开发者选项专用）：
/// 从当前页收敛回主菜单，然后自己导航到模型优化最需要的几类主要页面，
/// 每页抓一张 JPG + reason 到 data\trainingframes\日期\，全部抓完自动 Stop。
///
/// 导航全部复用经过实机验证的共享能力：
///   · 回主页：连续 B（同 ReturnToHomeGuide 的收敛思路）
///   · 主菜单 → 比赛页：下×5 → HubTileLocator.FindMainMenuMatchOffset → 左×6 → 右×offset → A
///   · 比赛页 → 磁贴：HubTileLocator.FindTileOffset(名称) → 左×6 → 右×offset → A
/// 判页用 MatchBrain.CollectTrainingFrame（与被动收集同一套判据），抓图用
/// BotEngine.DumpTrainingFrame（不受可疑帧开关/节流限制，保证每目标必抓一张）。
///
/// 每个目标都有轮次超时 + 磁贴扫描预算，抓不到就记日志跳过下一个，绝不卡死；
/// 总时限 8 分钟兜底。tag 去重，已在训练集里的页不重复抓。
/// </summary>
public sealed class TrainingFrameCollectScenario : IScenario
{
    private readonly BotEngine _engine;
    private readonly Action<string, LogLevel> _log;
    /// <summary>已处理（抓到 或 未找到/超时跳过）的目标 tag——用于推进流程与去重。</summary>
    private readonly HashSet<string> _captured = new(StringComparer.Ordinal);
    /// <summary>真正抓到图的 tag——与 _captured 分开，避免结束日志把「跳过」谎报成「已采集」。</summary>
    private readonly HashSet<string> _collected = new(StringComparer.Ordinal);
    private readonly long _startTick = Environment.TickCount64;

    /// <summary>目标页清单：Tag = 判定/抓图 tag；Tile = 比赛页要点的磁贴名（null = 当前页直接抓）。</summary>
    private static readonly (string Tag, string? Tile, string Label)[] Targets =
    {
        ("train_main_menu", null, "主菜单"),
        ("train_match_hub", null, "比赛选择页"),
        ("train_league_home", "我的联赛", "我的联赛主页"),
        ("train_tournament_cards", "锦标赛", "锦标赛卡片页"),
        ("train_weekly_cards", "活动", "周活动卡片页"),
    };

    private enum Phase { ToHome, AtHome, ToHub, InHub, InSubPage, Done }
    private Phase _phase = Phase.ToHome;
    private int _rounds;
    private int _backAttempts;
    private int _tileScanMoves;
    private string? _pendingTag;
    private string? _pendingLabel;

    public string Name => "训练帧采集";

    public TrainingFrameCollectScenario(BotEngine engine, Action<string, LogLevel> log)
    {
        _engine = engine;
        _log = log;
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        // 正常不可达：进 Done 必走 Finish()，其中 engine.Stop() 取消 CTS 结束主循环。
        // 保留仅防重入（如 Stop 与下一轮竞态），不参与流程。
        if (_phase == Phase.Done) return 1000;

        // 总时限兜底：8 分钟还没抓完就停，避免无人值守时一直占用游戏
        if (Environment.TickCount64 - _startTick > 8 * 60 * 1000)
        {
            Finish("达到 8 分钟总时限");
            return 1000;
        }

        var obs = await _engine.ObserveAsync(ct);
        var ocr = obs.Ocr;
        var dets = obs.Detections;
        var screen = MatchBrain.Classify(dets, ocr);
        bool isMainMenu = screen == MatchScreen.MainMenu || MatchBrain.HasMainMenuNavBar(ocr);
        bool isHub = screen == MatchScreen.MatchHub;

        // 全部目标已抓（含跳过）→ 收工
        if (_captured.Count >= Targets.Length)
        {
            Finish("全部目标页已采集");
            return 1000;
        }

        switch (_phase)
        {
            // ---------- ① 收敛回主菜单 ----------
            case Phase.ToHome:
                if (isMainMenu)
                {
                    _log("[训练帧] 已回主菜单，开始采集", LogLevel.Success);
                    _phase = Phase.AtHome;
                    _rounds = 0;
                    return 400;
                }
                if (++_backAttempts > 8)
                {
                    // 不能再放进 AtHome 抓图——此时画面不是主菜单，会把任意页当「主菜单正样本」
                    // 写进训练集，与采集初衷相反。改为重置预算继续 B 退（与 ToHub 超时同策略），
                    // 并在日志提示；实在回不去由 8 分钟总时限兜底结束。
                    _log("[训练帧] 连续 B 退 8 次仍未回主菜单，重置预算继续尝试（不会抓非主菜单帧）", LogLevel.Warn);
                    _engine.DumpSuspiciousFrame(obs.Frame, "train_home_stuck",
                        $"B 退 8 次仍未回主菜单（当前 {screen}），继续尝试");
                    _backAttempts = 0;
                    return 1200;
                }
                _log($"[训练帧] 不在主菜单（{screen}），B 退收敛（{_backAttempts}/8）", LogLevel.Info);
                await _engine.Pad.Back();
                return 2400;

            // ---------- ② 主菜单：抓主菜单帧 → 导航进比赛页 ----------
            case Phase.AtHome:
                // 双重保险：只有判定确为主菜单才抓（ToHome 已保证，这里防御未来改动）。
                // 绝不把非主菜单帧写成「主菜单正样本」污染训练集。
                if (!isMainMenu)
                {
                    _phase = Phase.ToHome;
                    _backAttempts = 0;
                    return 400;
                }
                CaptureIf("train_main_menu", "主菜单", obs);

                // 主菜单 → 底部卡片行 → "比赛"磁贴 → A（复用饱和下×5 + OCR 磁贴定位）
                await _engine.Pad.DpadRepeat(PadDir.Down, 5);
                int matchOff = HubTileLocator.FindMainMenuMatchOffset(ocr) ?? 0;
                await _engine.Pad.DpadRepeat(PadDir.Left, 6);
                if (matchOff > 0)
                    await _engine.Pad.DpadRepeat(PadDir.Right, matchOff);
                _log(matchOff > 0
                    ? $"[训练帧] 主菜单导航：下×5 → 左×6 → 右×{matchOff} → A（「比赛」在第 {matchOff + 1} 格）"
                    : "[训练帧] 主菜单导航：下×5 → 左×6 → A（OCR 未读全底部磁贴，回退饱和归位）",
                    LogLevel.Info);
                await _engine.Pad.Confirm();
                _phase = Phase.ToHub;
                _rounds = 0;
                return 2800;

            // ---------- ③ 等比赛页：抓比赛选择页帧 ----------
            case Phase.ToHub:
                if (isHub)
                {
                    CaptureIf("train_match_hub", "比赛选择页", obs);
                    _pendingTag = null;
                    _tileScanMoves = 0;
                    _phase = Phase.InHub;
                    _rounds = 0;
                    return 600;
                }
                if (++_rounds > 10)
                {
                    _log($"[训练帧] 等待比赛页超时（当前 {screen}），B 退回重新回主页", LogLevel.Warn);
                    await _engine.Pad.Back();
                    _backAttempts = 0;
                    _phase = Phase.ToHome;
                    _rounds = 0;
                    return 2400;
                }
                return 1200;

            // ---------- ④ 比赛页：选一个还没抓的磁贴目标，定位并按 A ----------
            case Phase.InHub:
                if (!isHub)
                {
                    // 被弹窗或误入页打断：B 退回比赛页
                    await _engine.Pad.Back();
                    _phase = Phase.ToHub;
                    _rounds = 0;
                    return 2400;
                }
                if (_pendingTag is null)
                {
                    var next = Targets.FirstOrDefault(t => t.Tile is not null && !_captured.Contains(t.Tag));
                    if (next.Tag is null)
                    {
                        Finish("磁贴目标已处理完");
                        return 1000;
                    }
                    _pendingTag = next.Tag;
                    _pendingLabel = next.Label;
                    _tileScanMoves = 0;
                }
                string? tile = Targets.First(t => t.Tag == _pendingTag).Tile;
                int? tileOff = HubTileLocator.FindTileOffset(ocr, tile!);
                if (tileOff is int off)
                {
                    await _engine.Pad.DpadRepeat(PadDir.Left, 6);
                    await _engine.Pad.DpadRepeat(PadDir.Right, off);
                    await _engine.Pad.Confirm();
                    _log($"[训练帧] 比赛页 → {_pendingLabel}（「{tile}」第 {off + 1} 磁贴）", LogLevel.Info);
                    _phase = Phase.InSubPage;
                    _rounds = 0;
                    return 2800;
                }
                // 标题读不到：先归位最左，再逐格右移切焦点（卡排在焦点移到该卡时才滚入标题）
                if (_tileScanMoves == 0)
                    await _engine.Pad.DpadRepeat(PadDir.Left, 6);
                if (_tileScanMoves < 6)
                {
                    _tileScanMoves++;
                    await _engine.Pad.Dpad(PadDir.Right);
                    return 1500;
                }
                // 扫 6 格仍读不到：跳过这个目标，避免死循环
                _log($"[训练帧] 比赛页未找到「{tile}」磁贴，跳过 {_pendingLabel}", LogLevel.Warn);
                _engine.DumpSuspiciousFrame(obs.Frame, "train_tile_not_found",
                    $"比赛页右移 6 格仍未读到「{tile}」磁贴，跳过 {_pendingLabel}");
                _captured.Add(_pendingTag); // 标记跳过
                _pendingTag = null;
                _phase = Phase.InHub;
                _rounds = 0;
                return 500;

            // ---------- ⑤ 子页：判定命中就抓图，然后 B 回比赛页 ----------
            case Phase.InSubPage:
                // 这里独立复算判据、不复用 ObserveAsync 被动收集的结果：被动那次受 CollectSuspicious
                // 开关控制（默认关），结果也没进 Observation；主动采集必须无条件自己判一次。
                // CollectTrainingFrame 是纯函数，同帧双算成本可忽略。
                var hit = MatchBrain.CollectTrainingFrame(ocr, dets);
                if (hit is { } h && h.Tag == _pendingTag)
                    CaptureIf(_pendingTag!, _pendingLabel!, obs);

                if (_captured.Contains(_pendingTag!))
                {
                    _log($"[训练帧] {_pendingLabel} 处理完成，B 回比赛页", LogLevel.Info);
                    await _engine.Pad.Back();
                    _pendingTag = null;
                    _phase = Phase.ToHub;
                    _rounds = 0;
                    return 2600;
                }
                if (++_rounds > 12)
                {
                    _log($"[训练帧] 等待 {_pendingLabel} 超时（当前 {screen}），B 退回重选", LogLevel.Warn);
                    _engine.DumpSuspiciousFrame(obs.Frame, "train_subpage_timeout",
                        $"进入 {_pendingLabel} 后 12 轮未命中判据（当前 {screen}）");
                    await _engine.Pad.Back();
                    _captured.Add(_pendingTag!); // 超时也标记，避免反复卡在同一目标
                    _pendingTag = null;
                    _phase = Phase.ToHub;
                    _rounds = 0;
                    return 2600;
                }
                return 1200;
        }
        return 1000;
    }

    private void CaptureIf(string tag, string label, Observation obs)
    {
        if (_captured.Contains(tag)) return;
        _engine.DumpTrainingFrame(obs.Frame, tag, $"主动采集：{label}（{tag}）");
        _captured.Add(tag);
        _collected.Add(tag);
        _log($"[训练帧] 已采集：{label} [{tag}]  进度 {_collected.Count}/{Targets.Length}", LogLevel.Success);
    }

    private void Finish(string reason)
    {
        // 只报真正抓到的页；跳过/超时的目标另行提示，不混进「已采集」
        var got = string.Join("、", Targets.Where(t => _collected.Contains(t.Tag)).Select(t => t.Label));
        var skipped = string.Join("、", Targets.Where(t => _captured.Contains(t.Tag) && !_collected.Contains(t.Tag))
                                            .Select(t => t.Label));
        _log($"[训练帧] 采集结束（{reason}）：抓到 [{(_collected.Count == 0 ? "无" : got)}]" +
             $" {_collected.Count}/{Targets.Length}" +
             $"{(_captured.Count > _collected.Count ? $"；未抓到（跳过/超时）：{skipped}" : "")}。" +
             $"已保存到 data\\trainingframes\\日期\\", LogLevel.Success);
        _phase = Phase.Done;
        _engine.Stop(); // 场景自然结束，GUI 的 ScenarioRunning 轮询会自动复位按钮
    }
}
