using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EfootballBot.Core.System;

/// <summary>
/// 只针对游戏的静音（相当于系统音量合成器里单独把游戏静音）：走 Windows 核心音频
/// （WASAPI）的「音频会话」级静音，不动系统音量，也不影响音乐/语音等其他软件。
/// <para>
/// 会话随进程的音频流创建：游戏重启后会得到新会话，静音不会自动继承——调用方按秒级
/// 周期重放本调用即可（幂等，重复设置无副作用）。
/// </para>
/// 会话还没建立（游戏启动后尚未发声）时返回 0 并不报错；等它第一次出声后会命中。
/// </summary>
public static class GameAudioMute
{
    /// <summary>游戏进程名白名单（国际服 eFootball / 国服 eFootballOnline 及其历史别名）。</summary>
    public static readonly string[] GameProcessNames =
        { "eFootball", "eFootball2024", "efootballsteam", "eFootballOnline" };

    /// <summary>
    /// 把当前所有游戏进程的音频会话设为静音（<paramref name="mute"/>=false 则取消静音）。
    /// 返回命中的会话数；0 = 游戏没在跑或还没建立音频会话。异常以 <paramref name="error"/> 返回，不抛出。
    /// </summary>
    public static int Apply(bool mute, out string error)
        => ApplyToPids(mute, FindGameProcessIds(), out error);

    /// <summary>逐个音频会话探针（进程 ID / 会话名 / 当前是否静音）。诊断与测试用，不修改任何音量。</summary>
    internal static List<AudioSessionInfo> ProbeSessions(out string error)
    {
        var list = new List<AudioSessionInfo>();
        EnumerateSessions((control, pid) =>
        {
            string name = "";
            try
            {
                if (control.GetDisplayName(out string display) == 0) name = display ?? "";
            }
            catch { /* 名字读不出不影响 */ }
            bool muted = false;
            try
            {
                if (control is ISimpleAudioVolume volume && volume.GetMute(out bool m) == 0) muted = m;
            }
            catch { /* 静音态读不出不影响 */ }
            list.Add(new AudioSessionInfo(pid, name, muted));
        }, out error);
        return list;
    }

    /// <summary>对指定进程 ID 的音频会话批量设置静音（测试与主流程共用）。</summary>
    internal static int ApplyToPids(bool mute, ICollection<int> pids, out string error)
    {
        int applied = 0;
        if (pids.Count > 0)
        {
            EnumerateSessions((control, pid) =>
            {
                if (!pids.Contains(pid)) return;
                if (control is not ISimpleAudioVolume volume) return;
                var ctx = Guid.Empty;
                if (volume.SetMute(mute, ref ctx) == 0) applied++;
            }, out error);
        }
        else
        {
            error = "";
        }
        return applied;
    }

    /// <summary>
    /// 遍历所有播放设备上的全部音频会话（含未在播放的会话），对每个会话回调
    /// （会话控制接口, 进程 ID）。单个设备/会话失败只记录原因，不中断整轮。
    /// </summary>
    private static void EnumerateSessions(Action<IAudioSessionControl, int> visit, out string error)
    {
        error = "";
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? devices = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceState.Active, out devices);
            devices.GetCount(out int deviceCount);

            for (int d = 0; d < deviceCount; d++)
            {
                IMMDevice? device = null;
                object? managerObject = null;
                IAudioSessionEnumerator? sessions = null;
                try
                {
                    devices.Item(d, out device);
                    if (device is null) continue;
                    var iid = typeof(IAudioSessionManager2).GUID;
                    device.Activate(ref iid, ClsCtx.All, IntPtr.Zero, out managerObject);
                    if (managerObject is not IAudioSessionManager2 manager) continue;
                    manager.GetSessionEnumerator(out sessions);
                    sessions.GetCount(out int sessionCount);

                    for (int s = 0; s < sessionCount; s++)
                    {
                        IAudioSessionControl? control = null;
                        try
                        {
                            sessions.GetSession(s, out control);
                            if (control is null) continue;
                            // 进程 ID 只在 IAudioSessionControl2 上；拿不到就跳过该会话
                            if (control is not IAudioSessionControl2 control2) continue;
                            if (control2.GetProcessId(out uint pid) != 0) continue;
                            visit(control, (int)pid);
                        }
                        catch (Exception ex)
                        {
                            error = ex.Message;
                        }
                        finally
                        {
                            Release(control);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (error.Length == 0) error = ex.Message;
                }
                finally
                {
                    Release(sessions);
                    Release(managerObject);
                    Release(device);
                }
            }
        }
        catch (Exception ex)
        {
            if (error.Length == 0) error = ex.Message;
        }
        finally
        {
            Release(devices);
            Release(enumerator);
        }
    }

    /// <summary>一个音频会话的快照（进程 ID / 会话显示名 / 是否已静音）。</summary>
    internal readonly record struct AudioSessionInfo(int Pid, string Name, bool Muted);

    /// <summary>当前在跑的游戏进程 ID（按白名单名匹配；取不到进程列表时返回空集）。</summary>
    public static HashSet<int> FindGameProcessIds()
    {
        var pids = new HashSet<int>();
        foreach (var name in GameProcessNames)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }
            foreach (var p in procs)
            {
                try { pids.Add(p.Id); } catch { /* 进程已退出 */ }
                finally { p.Dispose(); }
            }
        }
        return pids;
    }

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            try { Marshal.ReleaseComObject(comObject); } catch { /* 已释放 */ }
        }
    }

    // ---------------- Core Audio COM 声明 ----------------

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    private enum EDataFlow
    {
        eRender = 0,
        eCapture = 1,
        eAll = 2,
    }

    [Flags]
    private enum DeviceState
    {
        Active = 0x1,
        Disabled = 0x2,
        NotPresent = 0x4,
        Unplugged = 0x8,
        All = 0xF,
    }

    [Flags]
    private enum ClsCtx
    {
        InprocServer = 0x1,
        InprocHandler = 0x2,
        LocalServer = 0x4,
        RemoteServer = 0x10,
        All = InprocServer | InprocHandler | LocalServer | RemoteServer,
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, DeviceState stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, ClsCtx clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        // IAudioSessionManager（基接口）的槽位必须先声明
        [PreserveSig] int GetAudioSessionControl(ref Guid sessionGuid, int streamFlags, out IAudioSessionControl control);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid sessionGuid, int streamFlags, out ISimpleAudioVolume volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        [PreserveSig] int RegisterSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterSessionNotification(IntPtr notification);
        [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr notification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingId);
        [PreserveSig] int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notification);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // 基接口 IAudioSessionControl 槽位（部分只需占位，参数用 IntPtr）
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr value, IntPtr eventContext);
        [PreserveSig] int GetIconPath(IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr value, IntPtr eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingId);
        [PreserveSig] int SetGroupingParam(IntPtr groupingId, IntPtr eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notification);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier(IntPtr sessionId);
        [PreserveSig] int GetSessionInstanceIdentifier(IntPtr instanceId);
        [PreserveSig] int GetProcessId(out uint processId);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
