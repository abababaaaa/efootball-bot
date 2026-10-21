using EfootballBot.Core.System;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>
/// 差量补丁契约测试：补丁必须能无损还原、对插入/挪位鲁棒、校验失败时绝不产出文件。
/// 这层是「更新换上一个坏 exe」的唯一防线——出错的代价是用户直接打不开程序。
/// </summary>
public class DeltaPatchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ebpatch_" + Guid.NewGuid().ToString("N")[..8]);

    public DeltaPatchTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string W(string name, byte[] data)
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, data);
        return p;
    }

    private static byte[] RandomBytes(int n, int seed)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        return b;
    }

    [Fact]
    public void Apply_RestoresNewFile_AndPatchIsSmall()
    {
        // 10MB 旧文件；新文件 = 旧文件中间插入 64KB 随机数据（模拟"改了一处代码、后面整体挪位"）
        var oldBytes = RandomBytes(10 * 1024 * 1024, 1);
        var insert = RandomBytes(64 * 1024, 2);
        var newBytes = oldBytes[..(5 * 1024 * 1024)]
            .Concat(insert)
            .Concat(oldBytes[(5 * 1024 * 1024)..])
            .ToArray();

        string oldFile = W("old.bin", oldBytes);
        string newFile = W("new.bin", newBytes);
        string patch = Path.Combine(_dir, "p.bin");
        string outFile = Path.Combine(_dir, "out.bin");

        DeltaPatch.Create(oldFile, newFile, patch);
        DeltaPatch.Apply(oldFile, patch, outFile);

        Assert.Equal(newBytes, File.ReadAllBytes(outFile));
        // 内容定义分块的价值：只有插入点附近需要新数据，补丁应远小于新文件
        long patchSize = new FileInfo(patch).Length;
        Assert.True(patchSize < newBytes.Length / 4,
            $"补丁 {patchSize} 字节，应远小于 {newBytes.Length} 字节");
        Assert.True(patchSize > 0);
    }

    [Fact]
    public void Apply_DetectsCorruptedPatch_AndLeavesNoOutput()
    {
        string oldFile = W("old.bin", RandomBytes(256 * 1024, 3));
        string newFile = W("new.bin", RandomBytes(256 * 1024, 4));
        string patch = Path.Combine(_dir, "p.bin");
        string outFile = Path.Combine(_dir, "out.bin");
        DeltaPatch.Create(oldFile, newFile, patch);

        // 翻转补丁尾部（SHA256 区域）→ 必须失败，且不能留下半成品
        var bytes = File.ReadAllBytes(patch);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(patch, bytes);

        Assert.ThrowsAny<Exception>(() => DeltaPatch.Apply(oldFile, patch, outFile));
        Assert.False(File.Exists(outFile));
    }

    [Fact]
    public void Apply_DetectsWrongOldFile()
    {
        // 新文件 = 旧文件插入一小段 → 补丁里大部分是 COPY（引用旧文件区间）。
        // 此时给一个「内容被改过」的旧文件，拷出来的字节必然不对 → 产物 SHA256 必须拦住。
        var oldBytes = RandomBytes(512 * 1024, 5);
        var insert = RandomBytes(8 * 1024, 6);
        var newBytes = oldBytes[..(256 * 1024)].Concat(insert).Concat(oldBytes[(256 * 1024)..]).ToArray();

        string oldFile = W("old.bin", oldBytes);
        string newFile = W("new.bin", newBytes);
        string patch = Path.Combine(_dir, "p.bin");
        string outFile = Path.Combine(_dir, "out.bin");
        DeltaPatch.Create(oldFile, newFile, patch);

        var tampered = (byte[])oldBytes.Clone();
        Array.Fill(tampered, (byte)0, 0, 64 * 1024); // 前 64KB 被换成 0
        string wrongOld = W("wrong.bin", tampered);

        Assert.ThrowsAny<Exception>(() => DeltaPatch.Apply(wrongOld, patch, outFile));
        Assert.False(File.Exists(outFile));
    }

    [Fact]
    public void Apply_IdenticalFiles_ProducesAllCopies()
    {
        var data = RandomBytes(512 * 1024, 8);
        string oldFile = W("old.bin", data);
        string newFile = W("new.bin", data);
        string patch = Path.Combine(_dir, "p.bin");
        string outFile = Path.Combine(_dir, "out.bin");

        DeltaPatch.Create(oldFile, newFile, patch);
        DeltaPatch.Apply(oldFile, patch, outFile);

        Assert.Equal(data, File.ReadAllBytes(outFile));
        // 完全相同的文件：补丁里几乎没有插入数据
        Assert.True(new FileInfo(patch).Length < 64 * 1024);
    }
}
