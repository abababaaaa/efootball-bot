using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;

namespace EfootballBot.Core.Input;

/// <summary>
/// HIDMaestro 虚拟手柄后端（唯一后端）：纯用户态 UMDF2 驱动（ViGEmBus 退役后的继任者），MIT。
/// SDK 无 NuGet 包、单 DLL 61MB（驱动/签名工具全内嵌）——不随发布包携带，
/// 首次启用时按需下载官方 Release zip，锁定 DLL SHA256 后解压到 data\hidmaestro\，
/// 经独立 AssemblyLoadContext 反射加载（隔离其 net10.0-windows10.0.26100 TFM 的 SDK 投影依赖）。
/// 加载/安装任一步失败抛清晰异常，由引擎告警并终止本次启动。
/// ★设备形态档可配（PadProfileId）：默认 dualshock-4-v1（VID_054C&PID_05C4——与原 ViGEm DS4 逐字节同形），
/// 纯 HID、XInput 失明不泄输入；xbox 形档同样被 eFootball 消费（2026-10-21 探针三档实测，
/// 早前"零消费"结论系停靠缩窗冤案）但 XInput 可见会泄给前台应用。
/// 映射：PadButton.A/B/X/Y → HMButton Cross/Circle/Square/Y（DS4 按方位），十字键 → HMHat，
/// 摇杆 ±1/扳机 0~1 交 HMGamepadStateHelpers.StandardAxes 按档位映射。
/// 槽位=创建顺序的经验保证（非文档保证），玩家 1 强保证仍依赖 HidHide 隐藏真实手柄。
/// </summary>
public sealed class HidMaestroPad : IVirtualPad
{
    public const string SdkVersion = "v1.12.0";
    public const string SdkZipUrl = "https://github.com/hifihedgehog/HIDMaestro/releases/download/" + SdkVersion + "/HIDMaestro-v1.12.0.zip";
    /// <summary>默认虚拟设备形态档：DualShock4 v1（VID_054C&PID_05C4，与原 ViGEm DS4 同形）。
    /// 纯 HID——XInput 天生失明，不泄输入给前台应用；eFootball 经 DInput 路径正常消费（探针实测）。
    /// Xbox 形档（xbox-360-wired / xbox-series-xs / xbox-elite-v2-bt-v2）2026-10-21 探针实测同样被
    /// eFootball 消费并占 XInput slot 0（早前"零消费"结论系停靠缩窗冤案），但会泄漏给前台应用，作可选档。
    /// 生效档由配置 PadProfileId 决定（<see cref="DefaultProfileId"/> 为默认）。</summary>
    public const string DefaultProfileId = "dualshock-4-v1";
    /// <summary>生效的设备形态档（构造时由配置注入，默认 <see cref="DefaultProfileId"/>）。</summary>
    private readonly string _profileId;
    /// <summary>官方 Release zip 的 SHA256（升级 SDK 版本时必须同步更新；zip 锁住即锁住全部解包产物）。</summary>
    public const string SdkZipSha256 = "CF03347A0BC88BFB198A606C0FF8375BC4A9420FA80C07EF336186986D79B244";
    /// <summary>官方 Release 内 HIDMaestro.Core.dll 的 SHA256（升级 SDK 版本时必须同步更新）。</summary>
    public const string SdkDllSha256 = "F0B048E1B566804226EFDF854F6201EB9D2895C1E83F3C1A8813562AAB2D186C";

    private static readonly object InitGate = new();
    private static AssemblyLoadContext? _alc;
    private static object? _ctx;          // HMContext（反射，类型不落编译期）
    private static Type? _tState, _tButton, _tHat;

    private readonly Action<string>? _log;
    private object? _controller;          // HMController
    private object? _profile;             // HMProfile（xbox-360-wired）
    private volatile bool _connected;

    // 状态镜像：每次 Set* 更新镜像后整体 SubmitState（与 ViGEm 手动提交语义一致）
    private uint _buttons;
    private int _hat;                     // HMHat 原始值
    private float _lx, _ly, _rx, _ry;     // 摇杆 ±1
    private float _lt, _rt;               // 扳机 0~1

    public int UserIndex { get; private set; } = -1;
    public bool IsConnected => _connected;

    public PadBackendKind Kind => PadBackendKind.HidMaestro;

    public HidMaestroPad(Action<string>? log = null, string? profileId = null)
    {
        _log = log;
        _profileId = string.IsNullOrWhiteSpace(profileId) ? DefaultProfileId : profileId!;
    }

    /// <summary>驱动是否已安装（离线判据：DriverStore 同时存在两份 inf 目录；GUI 环境检测用，不加载 DLL）。</summary>
    public static bool IsDriverInstalled()
    {
        try
        {
            var repo = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "DriverStore", "FileRepository");
            if (!Directory.Exists(repo)) return false;
            bool main = false, xusb = false;
            foreach (var d in Directory.EnumerateDirectories(repo, "hidmaestro*.inf_amd64_*"))
            {
                var name = Path.GetFileName(d);
                if (name.StartsWith("hidmaestro_xusb.", StringComparison.OrdinalIgnoreCase)) xusb = true;
                else if (name.StartsWith("hidmaestro.inf_", StringComparison.OrdinalIgnoreCase)) main = true;
            }
            return main && xusb;
        }
        catch { return false; }
    }

    public void Connect()
    {
        if (_connected) return;

        // 纯 HID 后端无 XInput 槽位概念：不做玩家 1 占用阻断，也不做 XInput 槽位探测
        var dllPath = EnsureSdk();

        try
        {
            lock (InitGate)
            {
                _alc ??= CreateLoadContext(dllPath);
                _tState ??= RequireType("HMGamepadState");
                _tButton ??= RequireType("HMButton");
                _tHat ??= RequireType("HMHat");

                if (_ctx is null)
                {
                    var tCtx = RequireType("HMContext");
                    _ctx = Activator.CreateInstance(tCtx)
                        ?? throw new InvalidOperationException("HMContext 创建失败。");
                    tCtx.GetMethod("LoadDefaultProfiles", Type.EmptyTypes)!
                        .Invoke(_ctx, null);
                    var installed = (bool)(tCtx.GetProperty("IsDriverInstalled")!.GetValue(_ctx) ?? false);
                    if (!installed)
                    {
                        // 需要管理员（写 LocalMachine 证书存储 + pnputil）。本程序常规已提权；
                        // 未提权时 SDK 抛 UnauthorizedAccessException，转成可读提示。
                        _log?.Invoke("HIDMaestro 驱动未安装，开始安装（自签证书 + pnputil，首次约 5-15 秒）…");
                        try
                        {
                            tCtx.GetMethod("InstallDriver", Type.EmptyTypes)!.Invoke(_ctx, (object?[]?)null);
                        }
                        catch (UnauthorizedAccessException)
                        {
                            throw new InvalidOperationException(
                                "HIDMaestro 驱动安装需要管理员权限。请以管理员身份运行本程序后重试。");
                        }
                        installed = (bool)(tCtx.GetProperty("IsDriverInstalled")!.GetValue(_ctx) ?? false);
                        if (!installed)
                            throw new InvalidOperationException("HIDMaestro 驱动安装后仍未就绪（可查看日志中 pnputil 相关输出）。");
                        _log?.Invoke("HIDMaestro 驱动安装完成。");
                    }

                    _profile = tCtx.GetMethod("GetProfile", [typeof(string)])!
                        .Invoke(_ctx, [_profileId])
                        ?? throw new InvalidOperationException($"未找到 {_profileId} 设备档（SDK 内置档缺失？）");
                }

                // CreateController 的接收者是 HMContext（HMController 只是返回值类型——在它上面 GetMethod
                // 恒为 null，null!.Invoke 即空引用异常）。参数用元数据类型 HMProfile 精确匹配。
                // identityKey 固定 → 同 key 重建得到相同设备路径（v1.8+），升级/重连不残留僵尸设备
                var tCtxType = RequireType("HMContext");
                _controller ??= tCtxType.GetMethod("CreateController", [RequireType("HMProfile"), typeof(string)])!
                    .Invoke(_ctx, [_profile, "efootballbot"])
                    ?? throw new InvalidOperationException("HIDMaestro 虚拟手柄创建失败。");
            }
        }
        catch (InvalidOperationException) { throw; }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            var inner = ex.InnerException;
            throw new InvalidOperationException($"连接 HIDMaestro 失败：[{inner.GetType().Name}] {inner.Message}", inner);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"连接 HIDMaestro 失败：[{ex.GetType().Name}] {ex.Message}", ex);
        }

        _connected = true;
        ResetAll();
        _log?.Invoke($"HIDMaestro 控制器已创建（设备档 {_profileId}，identityKey=efootballbot）。");

        // HID 层在场证明：HIDMaestro 虚拟设备节点形态为 ROOT\HIDCLASS\HM_<hash> + HID\HIDCLASS\1&<hash>
        // （探针 2026-10-20 实测定案；实例路径不含 VID/PID 也不含"HIDMAESTRO"字样——
        // 旧版找 VID_054C 恒假阴性，照报"未上线"）。设备创建可能异步上线，轮询至多 3s；
        // 查无=设备未上线（驱动/档位问题，SDK 却无报错），阻断启动——照报"已连接"更害人。
        bool online = false;
        for (int attempt = 0; attempt < 10 && !online; attempt++)
        {
            if (attempt > 0) Thread.Sleep(300);
            try
            {
                online = HidDeviceEnumerator.EnumerateAllInstances()
                    .Any(i => HidDeviceEnumerator.IsHidMaestroInstance(i.InstancePath));
            }
            catch { }
        }
        if (!online)
            throw new InvalidOperationException(
                @"HIDMaestro 控制器已创建但虚拟设备节点未上线（ROOT\HIDCLASS\HM_* 查无）。请重启本程序重试；若持续出现，请在「关于」页生成诊断反馈。");
        _log?.Invoke(@"HIDMaestro 虚拟设备节点已上线（ROOT\HIDCLASS\HM_* 在场）。");
    }

    // ---- 状态操作 ----

    public void SetButton(PadButton btn, bool pressed)
    {
        int bit = MapButton(btn);
        if (bit == 0) return;
        _buttons = pressed ? _buttons | (uint)bit : _buttons & ~(uint)bit;
        Submit();
    }

    public void SetDpad(PadDir? dir)
    {
        _hat = dir switch
        {
            PadDir.Up => 1,    // HMHat.North
            PadDir.Right => 3, // HMHat.East
            PadDir.Down => 5,  // HMHat.South
            PadDir.Left => 7,  // HMHat.West
            _ => 0,            // HMHat.None（中位）
        };
        Submit();
    }

    public void SetLeftStick(short x, short y) { _lx = StickNormalized(x); _ly = StickNormalized(y); Submit(); }
    public void SetRightStick(short x, short y) { _rx = StickNormalized(x); _ry = StickNormalized(y); Submit(); }
    public void SetLeftTrigger(byte value) { _lt = TriggerNormalized(value); Submit(); }
    public void SetRightTrigger(byte value) { _rt = TriggerNormalized(value); Submit(); }

    public void ResetAll()
    {
        _buttons = 0; _hat = 0; _lx = _ly = _rx = _ry = 0; _lt = _rt = 0;
        Submit();
    }

    public void Disconnect()
    {
        if (!_connected) return;
        _connected = false;
        try { if (_controller is not null) RequireType("HMController").GetMethod("Dispose", Type.EmptyTypes)!.Invoke(_controller, null); } catch { }
        _controller = null;
        UserIndex = -1;
    }

    public void Dispose()
    {
        Disconnect();
        lock (InitGate)
        {
            try { if (_ctx is not null) RequireType("HMContext").GetMethod("Dispose", Type.EmptyTypes)!.Invoke(_ctx, null); } catch { }
            _ctx = null;
        }
        GC.SuppressFinalize(this);
    }

    // ---- 映射纯函数（可单测；返回 SDK 枚举原始值，不落编译期类型）----

    /// <summary>PadButton → HMButton 位标志（DS4 按方位：Cross=A 底、Circle=B 右、Square=X 左、Y=Y 顶）。Dpad 走 Hat，返回 0。</summary>
    internal static int MapButton(PadButton b) => b switch
    {
        PadButton.A => 0x01,             // Cross
        PadButton.B => 0x02,             // Circle
        PadButton.X => 0x04,             // Square
        PadButton.Y => 0x08,             // Y
        PadButton.LeftShoulder => 0x10,  // LeftBumper
        PadButton.RightShoulder => 0x20, // RightBumper
        PadButton.Back => 0x40,
        PadButton.Start => 0x80,
        PadButton.LeftThumb => 0x100,    // LeftStick
        PadButton.RightThumb => 0x200,   // RightStick
        _ => 0,
    };

    /// <summary>摇杆 short(-32768~32767) → ±1 归一化（StandardAxes 输入约定）。</summary>
    internal static float StickNormalized(short v) => Math.Clamp(v / 32767f, -1f, 1f);

    /// <summary>扳机 byte(0~255) → 0~1（0=松开）。</summary>
    internal static float TriggerNormalized(byte v) => v / 255f;

    // ---- SDK 反射基础设施 ----

    private static string SdkDir => Path.Combine(EfootballBot.Core.System.AppPaths.DataDir, "hidmaestro");
    private static string SdkDllPath => Path.Combine(SdkDir, "HIDMaestro.Core.dll");
    private static string SdkZipPath => Path.Combine(SdkDir, "sdk.zip");
    private static readonly string[] DepDllNames = ["HIDMaestro.Core.dll", "WinRT.Runtime.dll", "Microsoft.Windows.SDK.NET.dll"];

    /// <summary>SDK 三件套是否齐且 Core.dll 哈希匹配。投影依赖缺失时必须重解压：
    /// v1.12.0 zip 把 WinRT.Runtime/Microsoft.Windows.SDK.NET 放在子目录里，早期版本只解出 Core.dll 就中断，
    /// 快速通道若只看 Core.dll 会带着残缺依赖直通 Connect，SDK 内部投影代理为 null → CreateController 抛 NRE。</summary>
    internal bool SdkReady()
        => File.Exists(SdkDllPath) && Sha256OfFile(SdkDllPath).Equals(SdkDllSha256, StringComparison.OrdinalIgnoreCase)
           && DepDllNames.Skip(1).All(n => { var p = Path.Combine(SdkDir, n); try { return new FileInfo(p).Length > 0; } catch { return false; } });

    /// <summary>确保 SDK DLL 就位（三件齐且哈希匹配则跳过），返回 DLL 路径。</summary>
    internal string EnsureSdk()
    {
        if (SdkReady()) return SdkDllPath;

        _log?.Invoke($"首次启用 HIDMaestro：下载官方 SDK（{SdkVersion}，约 160MB，一次性）…");
        Directory.CreateDirectory(SdkDir);
        if (!File.Exists(SdkZipPath) || !Sha256OfFile(SdkZipPath).Equals(SdkZipSha256, StringComparison.OrdinalIgnoreCase))
        {
            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
            using (var resp = client.GetAsync(SdkZipUrl, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
            {
                resp.EnsureSuccessStatusCode();
                using var src = resp.Content.ReadAsStream();
                using var dst = File.Create(SdkZipPath);
                src.CopyTo(dst);
            }
        }
        else
        {
            _log?.Invoke("检测到已校验的 SDK 离线包，跳过下载。");
        }

        using (var zip = global::System.IO.Compression.ZipFile.OpenRead(SdkZipPath))
        {
            foreach (var name in DepDllNames)
            {
                var entry = PickEntry(zip, name)
                    ?? throw new InvalidOperationException($"SDK 包内缺少 {name}（SHA256 已匹配，包结构异常？）。");
                var target = Path.Combine(SdkDir, name);
                using var es = entry.Open();
                using var ds = File.Create(target);
                es.CopyTo(ds);
            }
        }
        File.Delete(SdkZipPath);

        var actual = Sha256OfFile(SdkDllPath);
        if (!actual.Equals(SdkDllSha256, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(SdkDllPath); } catch { }
            throw new InvalidOperationException($"HIDMaestro SDK 校验失败（SHA256 不符，实际 {actual}），已拒绝加载。请更新程序或检查下载通道。");
        }
        if (!SdkReady())
            throw new InvalidOperationException("HIDMaestro SDK 解压不完整（投影依赖缺失），已拒绝加载。");
        _log?.Invoke("HIDMaestro SDK 就绪。");
        return SdkDllPath;
    }

    private static AssemblyLoadContext CreateLoadContext(string dllPath)
    {
        var alc = new AssemblyLoadContext("HIDMaestro", isCollectible: false);
        var dir = Path.GetDirectoryName(dllPath)!;
        alc.Resolving += (_, name) =>
        {
            var candidate = Path.Combine(dir, name.Name + ".dll");
            return File.Exists(candidate) ? alc.LoadFromAssemblyPath(candidate) : null;
        };
        // SDK 投影依赖先入 ALC，避免与应用主目录的 19041 版 SDK.NET 混装
        foreach (var dep in new[] { "WinRT.Runtime.dll", "Microsoft.Windows.SDK.NET.dll" })
        {
            var p = Path.Combine(dir, dep);
            if (File.Exists(p)) alc.LoadFromAssemblyPath(p);
        }
        alc.LoadFromAssemblyPath(dllPath);
        return alc;
    }

    private static Type RequireType(string name)
        => _alc!.Assemblies.SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
              .FirstOrDefault(t => t.Name == name)
           ?? throw new InvalidOperationException($"HIDMaestro SDK 中未找到类型 {name}（SDK 版本不兼容？）。");

    private void Submit()
    {
        // 本地快照：停止路径 Disconnect 会把 _controller 置空，与观察循环并发时
        // Invoke(null) 会抛 "Non-static method requires a target"（实机 2026-10-20 停止瞬间实锤）
        var controller = _controller;
        if (controller is null || _tState is null || _tButton is null || _tHat is null || _ctx is null || _profile is null) return;
        try
        {
            var state = Activator.CreateInstance(_tState)!;
            _tState.GetField("Buttons")!.SetValue(state, Enum.ToObject(_tButton, _buttons));
            _tState.GetField("Hat")!.SetValue(state, Enum.ToObject(_tHat, _hat));
            var axes = RequireType("HMGamepadStateHelpers")
                .GetMethod("StandardAxes", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, [_profile, _lx, _ly, _rx, _ry, _lt, _rt]);
            _tState.GetField("Axes")!.SetValue(state, axes);
            RequireType("HMController")
                .GetMethod("SubmitState", [_tState.MakeByRefType()])!
                .Invoke(controller, [state]);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"HIDMaestro 状态提交失败：{ex.Message}", ex);
        }
    }

    /// <summary>按文件名（不含目录）在 zip 内挑条目，取目录最浅者（根目录优先，子目录兜底）。
    /// v1.12.0 的 WinRT.Runtime/Microsoft.Windows.SDK.NET 不在根目录而在 HIDMaestroProfileExtractor\ 下，
    /// 旧实现用 GetEntry(根路径) 找不到 → 只解出 Core.dll → ALC 缺投影依赖 → SDK 内部 NRE。</summary>
    internal static global::System.IO.Compression.ZipArchiveEntry? PickEntry(global::System.IO.Compression.ZipArchive zip, string fileName)
        => zip.Entries
            .Where(e => e.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase) && e.Length > 0)
            .OrderBy(e => e.FullName.Count(c => c is '/' or '\\'))
            .FirstOrDefault();

    private static string Sha256OfFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }
}
