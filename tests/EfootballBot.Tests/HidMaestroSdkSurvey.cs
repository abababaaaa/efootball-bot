using System.Reflection;
using System.Runtime.Loader;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// HIDMaestro SDK 签名调查（本机 SDK：publish\data\hidmaestro\HIDMaestro.Core.dll）。
// 背景：实机 v3.1.0.1 提交状态报 "Non-static method requires a target"（TargetException，
// 实例方法被 null 目标 Invoke）——HidMaestroPad.Submit 的三个反射调用点必有一处签名对不上。
// 本测试不猜：直接枚举 SDK 实际类型/字段/方法签名，结果落 hidmaestro_survey.txt。
// SDK 不在（CI/他机）时跳过。定位后本文件保留作版本升级时的核对工具。
public class HidMaestroSdkSurvey
{
    private readonly ITestOutputHelper _out;
    public HidMaestroSdkSurvey(ITestOutputHelper output) => _out = output;

    private const string DllPath = @"B:\Efootball挂机软件\publish\data\hidmaestro\HIDMaestro.Core.dll";
    private const string OutPath = @"B:\Efootball挂机软件\hidmaestro_survey.txt";

    [Fact]
    public void Dump_Sdk_Signatures()
    {
        if (!File.Exists(DllPath)) { _out.WriteLine("SDK 不在，跳过"); return; }

        var alc = new AssemblyLoadContext("HidMaestroSurvey", isCollectible: true);
        var dir = Path.GetDirectoryName(DllPath)!;
        foreach (var dep in new[] { "WinRT.Runtime.dll", "Microsoft.Windows.SDK.NET.dll" })
        {
            var p = Path.Combine(dir, dep);
            if (File.Exists(p)) alc.LoadFromAssemblyPath(p);
        }
        var asm = alc.LoadFromAssemblyPath(DllPath);

        using var w = new StreamWriter(OutPath);
        var types = asm.GetTypes().Where(t => t.Name.StartsWith("HM") || t.Name.Contains("Hat")).OrderBy(t => t.Name).ToList();
        w.WriteLine($"== {types.Count} types ==");
        foreach (var t in types)
        {
            w.WriteLine($"\n### {t.FullName} (static={t.IsAbstract && t.IsSealed}, enum={t.IsEnum}, iface={t.IsInterface})");
            if (t.IsEnum)
            {
                foreach (var v in Enum.GetValues(t))
                    w.WriteLine($"  = {v} ({Convert.ChangeType(v, Convert.GetTypeCode(v))})");
                continue;
            }
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                w.WriteLine($"  field  {f.FieldType.Name} {f.Name} (static={f.IsStatic})");
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                w.WriteLine($"  prop   {p.PropertyType.Name} {p.Name}");
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (m.Name is "ToString" or "Equals" or "GetHashCode" or "GetType") continue;
                var ps = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name}{(p.ParameterType.IsByRef ? "&" : "")} {p.Name}"));
                w.WriteLine($"  method {(m.IsStatic ? "static " : "")}{m.ReturnType.Name} {m.Name}({ps})");
            }
        }
        alc.Unload();
        _out.WriteLine($"dumped -> {OutPath}");
    }

    // 复刻 HidMaestroPad.Submit 的绑定路径（不需要驱动/提权/真实设备）：
    // 实机 3.1.0.1 报 TargetException "Non-static method requires a target"（实例方法被 null
    // 目标 Invoke），但静态 dump 三个调用点签名全对——本测试逐条复刻绑定+可离线调用的
    // StandardAxes 实调，找出行为与预期不符的那一处。
    [Fact]
    public void Replicate_Submit_Binding()
    {
        if (!File.Exists(DllPath)) { _out.WriteLine("SDK 不在，跳过"); return; }

        var alc = new AssemblyLoadContext("HidMaestroBindProbe", isCollectible: true);
        var dir = Path.GetDirectoryName(DllPath)!;
        foreach (var dep in new[] { "WinRT.Runtime.dll", "Microsoft.Windows.SDK.NET.dll" })
        {
            var p = Path.Combine(dir, dep);
            if (File.Exists(p)) alc.LoadFromAssemblyPath(p);
        }
        var asm = alc.LoadFromAssemblyPath(DllPath);
        Type? Find(string name) => asm.GetTypes().FirstOrDefault(t => t.Name == name);

        var tState = Find("HMGamepadState");
        var tHelpers = Find("HMGamepadStateHelpers");
        var tCtrl = Find("HMController");
        Assert.NotNull(tState); Assert.NotNull(tHelpers); Assert.NotNull(tCtrl);

        // ① SubmitState 绑定（与 HidMaestroPad 一致：MakeByRefType 精确匹配）
        var mSubmit = tCtrl!.GetMethod("SubmitState", [tState!.MakeByRefType()]);
        _out.WriteLine($"① SubmitState(ByRef) found={mSubmit is not null} static={mSubmit?.IsStatic.ToString() ?? "-"} " +
                       $"decl={mSubmit?.DeclaringType?.FullName ?? "-"} params={string.Join("|", mSubmit?.GetParameters().Select(pp => pp.ParameterType.Name + (pp.ParameterType.IsByRef ? "&" : "")) ?? [])}");

        // ② StandardAxes 绑定（Public|Static）
        var mAxes = tHelpers!.GetMethod("StandardAxes", BindingFlags.Public | BindingFlags.Static);
        _out.WriteLine($"② StandardAxes(Public|Static) found={mAxes is not null} static={mAxes?.IsStatic.ToString() ?? "-"} params={mAxes?.GetParameters().Length.ToString() ?? "-"}");

        // ③ StandardAxes 实调（无驱动可跑：只需 profile 对象）
        var tCtx = Find("HMContext");
        object? profile = null;
        try
        {
            var ctx = Activator.CreateInstance(tCtx!);
            tCtx!.GetMethod("LoadDefaultProfiles", Type.EmptyTypes)!.Invoke(ctx, null);
            profile = tCtx.GetMethod("GetProfile", [typeof(string)])!.Invoke(ctx, ["xbox-360-wired"]);
            _out.WriteLine($"③ profile={profile?.GetType().FullName}");
            try
            {
                var axes = mAxes!.Invoke(null, [profile, 0f, 0f, 0f, 0f, 0f, 0f]);
                _out.WriteLine($"③ StandardAxes OK -> {axes?.GetType().FullName} count={(axes as System.Collections.IDictionary)?.Count}");
            }
            catch (Exception ex) { _out.WriteLine($"③ StandardAxes THREW: [{ex.GetType().Name}] {ex.Message} inner=[{ex.InnerException?.GetType().Name}] {ex.InnerException?.Message}"); }
        }
        catch (Exception ex) { _out.WriteLine($"③ profile 阶段失败: [{ex.GetType().Name}] {ex.Message}"); }

        // ④ HMGamepadState 构造 + 字段赋值（Buttons/Hat/Axes 均按 field 写）
        try
        {
            var st = Activator.CreateInstance(tState!)!;
            var tBtn = Find("HMButton"); var tHat = Find("HMHat");
            tState!.GetField("Buttons")!.SetValue(st, Enum.ToObject(tBtn!, 1u));
            tState.GetField("Hat")!.SetValue(st, Enum.ToObject(tHat!, 0));
            var axes = mAxes?.Invoke(null, [profile, 0f, 0f, 0f, 0f, 0f, 0f]);
            tState.GetField("Axes")!.SetValue(st, axes);
            _out.WriteLine("④ state 构造+字段赋值 OK");
        }
        catch (Exception ex) { _out.WriteLine($"④ state 构造 THREW: [{ex.GetType().Name}] {ex.Message} inner=[{ex.InnerException?.GetType().Name}] {ex.InnerException?.Message}"); }

        // ⑤ 全名相同的多类型排查（RequireType 按 Name 取 FirstOrDefault 可能选错）
        foreach (var name in new[] { "HMController", "HMGamepadState", "HMGamepadStateHelpers", "HMProfile" })
        {
            var matches = asm.GetTypes().Where(t => t.Name == name).Select(t => t.FullName).ToList();
            _out.WriteLine($"⑤ {name}: {matches.Count} 个 → {string.Join(", ", matches)}");
        }

        // ⑥ SDK 内置 profile 清单（找 DS4 形态——本机实锤 DS4 HID 形态 eFootball 认、
        //    xbox-360-wired 形态不认；若 SDK 有 DS4 档即换挡修复）
        try
        {
            var tCtx2 = Find("HMContext");
            var ctx2 = Activator.CreateInstance(tCtx2!);
            tCtx2!.GetMethod("LoadDefaultProfiles", Type.EmptyTypes)!.Invoke(ctx2, null);
            var profiles = tCtx2.GetProperty("AllProfiles")!.GetValue(ctx2) as System.Collections.IEnumerable;
            int n = 0;
            foreach (var p in profiles!)
            {
                n++;
                var t = p.GetType();
                string G(string prop) => t.GetProperty(prop)?.GetValue(p)?.ToString() ?? "-";
                _out.WriteLine($"⑥ profile id={G("Id")} name={G("Name")} type={G("Type")} conn={G("Connection")} " +
                               $"vid={G("VendorId")} pid={G("ProductId")} deployable={G("IsDeployable")} backend={G("Backend")}");
            }
            _out.WriteLine($"⑥ 共 {n} 个内置 profile");
        }
        catch (Exception ex) { _out.WriteLine($"⑥ profile 枚举失败: [{ex.GetType().Name}] {ex.Message}"); }

        // ⑦ 本机 XInput 槽位占用（不提权可查）：xbox-360-wired 形态走 XInput 通道时，
        //    槽位 0 被幽灵设备占住 = 游戏只听槽位 0 的哑设备（UE XInput 插件抢到就弃 DInput）
        try
        {
            for (uint slot = 0; slot < 4; slot++)
            {
                var caps = new XInputCaps();
                int rc = XInputGetCapabilities(slot, 0, ref caps);
                var state = new XInputState();
                int rs = XInputGetState(slot, ref state);
                _out.WriteLine($"⑦ XInput slot {slot}: Caps rc={rc} (0=有设备) type={caps.Type} subType={caps.SubType} | State rc={rs} (0=在位) pkt={state.PacketNumber}");
            }
        }
        catch (Exception ex) { _out.WriteLine($"⑦ XInput 探测失败: {ex.Message}"); }
        alc.Unload();
    }

    [System.Runtime.InteropServices.DllImport("xinput1_4.dll")]
    private static extern int XInputGetCapabilities(uint dwUserIndex, uint dwFlags, ref XInputCaps pCapabilities);
    [System.Runtime.InteropServices.DllImport("xinput1_4.dll")]
    private static extern int XInputGetState(uint dwUserIndex, ref XInputState pState);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct XInputCaps
    {
        public uint Type;
        public uint SubType;
        public ushort Flags;
        public ushort VibLeft, VibRight;      // XINPUT_VIBRATION
        public ushort Buttons;                // XINPUT_GAMEPAD 起始
        public byte LTrigger, RTrigger;
        public short LX, LY, RX, RY;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LTrigger;
        public byte RTrigger;
        public short LX, LY, RX, RY;
    }
}
