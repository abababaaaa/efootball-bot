using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace EfootballBot.Core.System;

/// <summary>内置 ViGEmBus 安装器资源：常量、释放与哈希校验。</summary>
internal static class DriverPayload
{
    public const string ResourceName = "EfootballBot.ViGEmBusSetup.exe";

    /// <summary>ViGEmBus_1.22.0_x64_x86_arm64.exe 官方文件 SHA256（大写十六进制）。</summary>
    public const string ExpectedSha256 = "89220A7865076B342892F98865F3499FB7C4CFD673159E89D352C360FD014C6A";

    /// <summary>把嵌入资源释放到目标路径（覆盖已有文件）。</summary>
    public static async Task ReleaseToAsync(string targetPath, CancellationToken ct)
    {
        await using Stream src = typeof(DriverPayload).Assembly
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"内置驱动资源缺失：{ResourceName}");

        string? dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using FileStream fs = File.Create(targetPath);
        await src.CopyToAsync(fs, ct);
    }

    /// <summary>计算文件的 SHA256，返回大写十六进制字符串。</summary>
    public static string ComputeSha256(string path)
    {
        using SHA256 sha = SHA256.Create();
        using FileStream fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }
}
