// PadProxyAssets.cs —— 进程内代理 DLL 的源文件解析（exe 同级文件 / exe 内嵌资源）
//
// 为什么要内嵌（2026-11-20 用户反馈）：发布只带 EfootballBot.exe（单文件发布，
// xinput1_3.dll / xinput1_4.dll 因 ExcludeFromSingleFile 只留在 publish-stage 目录里），
// 换一台电脑只拷 exe 时，设置页「投放代理到游戏目录」找不到源文件 → 「程序目录缺少 xinput1_3.dll」。
// 现在两个 DLL 以嵌入资源打进 exe，投放时按需解压到 data\tmp\proxy\v{程序版本}\；
// exe 同级有同名文件时仍优先用它（本地调试、单独替换 DLL 热修都不受影响）。

using System.IO;
using System.Reflection;

namespace EfootballBot.Core.Input;

/// <summary>
/// 进程内代理的两个 DLL 从哪来：优先 exe 同级文件，其次 exe 内嵌资源（首次投放时解压到缓存）。
/// 消费者：设置页「投放代理到游戏目录」（<c>MainWindow.Settings.cs</c> → <c>DeployProxyTo</c>）。
/// </summary>
public static class PadProxyAssets
{
    /// <summary>游戏静态导入的代理名（游戏只从自己的 Binaries\Win64 加载同名 DLL）。</summary>
    public const string Dll13 = "xinput1_3.dll";

    /// <summary>SDL2 / 中间件是动态 LoadLibrary("XInput1_4.dll")，两个都要投。</summary>
    public const string Dll14 = "xinput1_4.dll";

    /// <summary>随 DLL 一起写进游戏目录的配置文件名（目标进程 / 共享内存名都在里面）。</summary>
    public const string IniName = "xinput_proxy.ini";

    /// <summary>嵌入资源名前缀（必须与 csproj 里 EmbeddedResource 的 LogicalName 一致）。</summary>
    public const string ResourcePrefix = "EfootballBot.Proxy.";

    private static readonly Assembly Self = typeof(PadProxyAssets).Assembly;

    /// <summary>嵌入资源名，如 <c>EfootballBot.Proxy.xinput1_3.dll</c>。</summary>
    public static string ResourceName(string fileName) => ResourcePrefix + fileName;

    /// <summary>exe 里是否带了这个 DLL 的副本（tools\xinput-proxy 未构建时构建产物里没有）。</summary>
    public static bool IsEmbedded(string fileName)
    {
        using var s = Self.GetManifestResourceStream(ResourceName(fileName));
        return s is not null;
    }

    /// <summary>有没有可投放的源：exe 同级文件，或 exe 内嵌副本。</summary>
    public static bool HasSource(string fileName) =>
        File.Exists(Beside(fileName)) || IsEmbedded(fileName);

    /// <summary>
    /// 取可直接 <see cref="File.Copy(string, string, bool)"/> 的源路径：
    /// ① exe 同级同名文件（本地调试 / 手动换 DLL 时优先）；② 内嵌资源解压到 <see cref="CacheDir"/>。
    /// 都没有返回 null（构建时没产出代理 DLL）。
    /// </summary>
    public static string? ResolveSource(string fileName)
    {
        string beside = Beside(fileName);
        if (File.Exists(beside)) return beside;

        string? hit = Extract(fileName, CacheDir);
        if (hit is not null) return hit;

        // 程序放在 Program Files 下时 data\ 不可写 → 退到系统临时目录，投放照样能走。
        return Extract(fileName, Path.Combine(
            Path.GetTempPath(), "EfootballBot", "proxy", VersionTag));
    }

    /// <summary>解压缓存目录：data\tmp\proxy\v{程序版本}\——版本一换就换目录，不会拿到上一版的旧 DLL。</summary>
    public static string CacheDir =>
        Path.Combine(EfootballBot.Core.System.AppPaths.TempDir, "proxy", VersionTag);

    /// <summary>
    /// 把内嵌 DLL 写到 dir 并返回落盘路径（已存在且长度一致视为就绪，不重写）；
    /// 资源不存在 / 写不进去返回 null。dir 显式传入，方便单测与异地回退。
    /// </summary>
    public static string? Extract(string fileName, string dir)
    {
        using Stream? res = Self.GetManifestResourceStream(ResourceName(fileName));
        if (res is null) return null;

        try
        {
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, fileName);
            if (File.Exists(target) && new FileInfo(target).Length == res.Length) return target;

            // 先写唯一临时名再换名：A/B 档案同时开、两个进程同时首投时，
            // 谁都不会读到写了一半的 DLL（写一半的 PE 被 LoadLibrary = 游戏直接崩）。
            string tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (FileStream fs = File.Create(tmp)) res.CopyTo(fs);
            try
            {
                File.Move(tmp, target, overwrite: true);
                SweepTmp(dir);
                return target;
            }
            catch (IOException)
            {
                // 另一个实例正映射着这个文件（File.Copy 源句柄不带 FILE_SHARE_DELETE）→ 用临时副本顶一次
                if (File.Exists(target)) return tmp;
                throw;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string VersionTag
    {
        get
        {
            Version? v = Self.GetName().Version;
            return v is null ? "0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    private static string Beside(string fileName) =>
        Path.Combine(EfootballBot.Core.System.AppPaths.ExeDir, fileName);

    /// <summary>清掉换名失败/进程被杀留下的临时文件（1 小时前的），避免 data\tmp 越攒越多。</summary>
    private static void SweepTmp(string dir)
    {
        try
        {
            foreach (string stale in Directory.EnumerateFiles(dir, "*.tmp"))
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(stale) < TimeSpan.FromHours(1)) continue;
                try { File.Delete(stale); } catch { /* 正在被别的实例用，下次再说 */ }
            }
        }
        catch { /* 清理失败不影响投放 */ }
    }
}
