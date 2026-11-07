using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EfootballBot.Core.Bot;
using EfootballBot.Core.System;

namespace EfootballBot;

/// <summary>
/// 「挂机时静音游戏」：只把游戏进程的音频会话静音（Windows 音量合成器里单独给游戏静音的效果），
/// 系统音量与其他软件完全不受影响。
/// <para>
/// 各游戏模块页的「本次运行」栏各有一个开关（共用同一配置 <see cref="Core.Config.AudioConfig"/>），
/// 点它随时开关游戏声音：挂机中点一下就立刻生效，不用停止再开始。
/// 挂机开始自动静音，停止自动恢复；游戏中途重启会新建音频会话（静音不继承），
/// 挂机期间按秒级巡检重放静音。
/// </para>
/// </summary>
public partial class MainWindow
{
    /// <summary>挂机中重放静音的巡检间隔（游戏重启 → 新会话，需要重新静音）。</summary>
    private readonly DispatcherTimer _muteTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    /// <summary>当前是否由本程序把游戏静音（用于日志去重与停止时恢复）。</summary>
    private bool _muteApplied;

    /// <summary>批量回填/同步所有模块的复选框时屏蔽事件，避免互相触发写配置。</summary>
    private bool _syncingMuteUi;

    private CheckBox[] MuteCheckboxes() => new[]
    {
        ChkMuteMyLeague, ChkMuteWeekly, ChkMuteAiLadder, ChkMuteStud,
        ChkMuteCn, ChkMuteCnWeekly, ChkMuteCnMyLeague,
    };

    /// <summary>
    /// 启动期初始化：复选框回填配置 + 巡检计时器。
    /// 巡检同时负责「挂机已结束但还没恢复声音」（场景自己跑完/异常退出）的兜底恢复。
    /// </summary>
    private void InitGameMute()
    {
        _syncingMuteUi = true;
        foreach (var c in MuteCheckboxes()) c.IsChecked = _cfg.Audio.MuteGame;
        _syncingMuteUi = false;

        _muteTimer.Tick += (_, _) =>
        {
            bool running = _workerHost is { ScenarioRunning: true };
            if (running && _cfg.Audio.MuteGame)
                SetGameMute(true, userAction: false);   // 幂等重放：游戏重启后的新会话也会被静音
            else if (!running && _muteApplied)
                SetGameMute(false, userAction: false);  // 场景自行结束（跑完/异常）也要恢复声音
        };
        _muteTimer.Start();
    }

    /// <summary>任一模块页的开关变动：写配置 + 同步其余 5 个复选框 + 立刻生效。</summary>
    private void ChkMuteGame_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingMuteUi || _cfg is null) return;
        bool on = sender is CheckBox { IsChecked: true };
        _cfg.Audio.MuteGame = on;
        _cfg.Save();

        _syncingMuteUi = true;
        foreach (var c in MuteCheckboxes())
            if ((c.IsChecked == true) != on) c.IsChecked = on;
        _syncingMuteUi = false;

        SetGameMute(on, userAction: true);
    }

    /// <summary>挂机启动成功后调用（配置开着就立刻静音）。</summary>
    private void MuteGameOnScenarioStarted()
    {
        if (_cfg.Audio.MuteGame) SetGameMute(true, userAction: false);
    }

    /// <summary>挂机停止时调用：恢复游戏声音（用户关掉开关时已经恢复过，这里幂等）。</summary>
    private void MuteGameOnScenarioStopped()
    {
        if (_muteApplied) SetGameMute(false, userAction: false);
    }

    /// <summary>
    /// 设置游戏静音。<paramref name="userAction"/>=true 表示用户手动点开关，
    /// 失败/无会话时给出可读日志；自动调用只静默重试，不刷屏。
    /// </summary>
    private void SetGameMute(bool mute, bool userAction)
    {
        if (!mute && !_muteApplied && !userAction) return;
        int sessions = GameAudioMute.Apply(mute, out string error);
        if (!string.IsNullOrEmpty(error))
        {
            if (userAction) AppendLog($"静音游戏失败：{error}", LogLevel.Warn);
            return;
        }
        if (mute)
        {
            if (sessions > 0)
            {
                if (!_muteApplied)
                    AppendLog($"已静音游戏（{sessions} 个音频会话），系统音量与其他软件不受影响", LogLevel.Success);
                _muteApplied = true;
            }
            else if (userAction)
            {
                AppendLog("没找到游戏的音频会话（游戏没启动或还没出过声）——挂机中会自动重试", LogLevel.Warn);
            }
        }
        else
        {
            if (_muteApplied || sessions > 0) AppendLog("已恢复游戏声音", LogLevel.Info);
            _muteApplied = false;
        }
    }
}
