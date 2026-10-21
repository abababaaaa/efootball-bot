using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace EfootballBot.Core.System;

/// <summary>
/// 差量补丁（最小可用版）：只支持「上一版 → 新版」一跳。
///
/// 为什么用「内容定义分块」而不是固定偏移比较：单文件 exe 是压缩打包的，
/// 任何一处代码改动都会让后面所有字节整体挪位，固定对齐的块几乎全部失配；
/// 用滚动哈希（gear hash）按内容切块后，插入/删除只影响切口附近的一两个块，
/// 其余块仍能命中旧文件并直接拷贝。
///
/// 文件格式（小端）：
/// <code>
/// "EBDP1\0"              6B 魔数
/// int64  newLength       新文件长度
/// int32  opCount
/// op COPY:   byte 1, int64 offset, int32 length
/// op INSERT: byte 2, int32 length, byte[length]
/// 32B    SHA256(新文件)  应用端自校验（不依赖外部清单）
/// </code>
///
/// 安全约束：应用失败/校验不过 = 什么都不替换。补丁永远只是加速路径，
/// 不承担正确性——失败就回退整包下载。
/// </summary>
public static class DeltaPatch
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("EBDP1\0");

    private const int MinChunk = 2 * 1024;
    private const int MaxChunk = 64 * 1024;
    private const ulong Mask = (1UL << 13) - 1; // 平均块 ≈ 8KB

    private const byte OpCopy = 1;
    private const byte OpInsert = 2;

    private readonly record struct Op(byte Kind, long Offset, int Length, int DataStart);

    /// <summary>生成把 <paramref name="oldFile"/> 变成 <paramref name="newFile"/> 的补丁。</summary>
    public static void Create(string oldFile, string newFile, string patchFile)
    {
        byte[] oldBytes = File.ReadAllBytes(oldFile);
        byte[] newBytes = File.ReadAllBytes(newFile);

        // 旧文件索引：块哈希 → 偏移列表（同一内容可能出现多次，命中时任取其一）
        var index = new Dictionary<ulong, List<int>>();
        foreach (var (offset, len) in Chunks(oldBytes))
        {
            ulong h = HashBytes(oldBytes.AsSpan(offset, len));
            if (!index.TryGetValue(h, out var list)) index[h] = list = new List<int>(1);
            if (list.Count < 8) list.Add(offset); // 上限：防病态重复数据把内存吃光
        }

        var ops = new List<Op>();
        int insertStart = -1;

        void FlushInsert(int endExclusive)
        {
            if (insertStart < 0) return;
            ops.Add(new Op(OpInsert, 0, endExclusive - insertStart, insertStart));
            insertStart = -1;
        }

        foreach (var (offset, len) in Chunks(newBytes))
        {
            int match = -1;
            ulong h = HashBytes(newBytes.AsSpan(offset, len));
            if (index.TryGetValue(h, out var candidates))
            {
                foreach (int cand in candidates)
                {
                    if (cand + len <= oldBytes.Length &&
                        oldBytes.AsSpan(cand, len).SequenceEqual(newBytes.AsSpan(offset, len)))
                    {
                        match = cand;
                        break;
                    }
                }
            }

            if (match >= 0)
            {
                FlushInsert(offset);
                ops.Add(new Op(OpCopy, match, len, 0));
            }
            else if (insertStart < 0)
            {
                insertStart = offset;
            }
        }
        FlushInsert(newBytes.Length);

        WritePatch(patchFile, newBytes, ops);
    }

    private static void WritePatch(string patchFile, byte[] newBytes, List<Op> ops)
    {
        using var fs = File.Create(patchFile);
        using var bw = new BinaryWriter(fs);

        bw.Write(Magic);
        bw.Write((long)newBytes.Length);
        bw.Write(ops.Count);

        foreach (var op in ops)
        {
            bw.Write(op.Kind);
            if (op.Kind == OpCopy)
            {
                bw.Write(op.Offset);
                bw.Write(op.Length);
            }
            else
            {
                bw.Write(op.Length);
                bw.Write(newBytes, op.DataStart, op.Length);
            }
        }

        bw.Write(SHA256.HashData(newBytes));
        bw.Flush();
    }

    /// <summary>把补丁应用到旧文件，产出新文件；任何不符都抛异常且不留下半成品。</summary>
    public static void Apply(string oldFile, string patchFile, string outFile)
    {
        using var patch = new BinaryReader(File.OpenRead(patchFile));
        var magic = patch.ReadBytes(Magic.Length);
        if (!magic.SequenceEqual(Magic))
            throw new InvalidDataException("补丁格式不正确（魔数不匹配）");

        long newLength = patch.ReadInt64();
        int opCount = patch.ReadInt32();

        using var oldFs = File.OpenRead(oldFile);
        using var outFs = File.Create(outFile);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[MaxChunk];
        long written = 0;

        try
        {
            for (int i = 0; i < opCount; i++)
            {
                byte kind = patch.ReadByte();
                if (kind == OpCopy)
                {
                    long offset = patch.ReadInt64();
                    int len = patch.ReadInt32();
                    if (offset < 0 || len <= 0 || offset + len > oldFs.Length)
                        throw new InvalidDataException("补丁引用了旧文件之外的区间");

                    oldFs.Seek(offset, SeekOrigin.Begin);
                    int remaining = len;
                    while (remaining > 0)
                    {
                        int n = oldFs.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                        if (n <= 0) throw new InvalidDataException("读取旧文件失败（长度不符）");
                        outFs.Write(buffer, 0, n);
                        hash.AppendData(buffer, 0, n);
                        written += n;
                        remaining -= n;
                    }
                }
                else if (kind == OpInsert)
                {
                    int len = patch.ReadInt32();
                    if (len < 0 || len > 8 * 1024 * 1024)
                        throw new InvalidDataException("补丁插入段长度异常");

                    int remaining = len;
                    while (remaining > 0)
                    {
                        int n = patch.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                        if (n <= 0) throw new InvalidDataException("补丁数据不完整");
                        outFs.Write(buffer, 0, n);
                        hash.AppendData(buffer, 0, n);
                        written += n;
                        remaining -= n;
                    }
                }
                else
                {
                    throw new InvalidDataException($"未知补丁操作：{kind}");
                }
            }

            outFs.Flush();

            if (written != newLength)
                throw new InvalidDataException($"补丁产出长度不符：{written} != {newLength}");

            byte[] expected = patch.ReadBytes(32);
            string actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            string want = Convert.ToHexString(expected).ToLowerInvariant();
            if (expected.Length != 32 || actual != want)
                throw new InvalidDataException($"补丁结果校验失败：{Safe(actual)} != {Safe(want)}");
        }
        catch
        {
            outFs.Dispose();
            try { File.Delete(outFile); } catch { }
            throw;
        }

        static string Safe(string s) => s.Length >= 12 ? s[..12] + "…" : s;
    }

    /// <summary>内容定义分块（gear hash 滚动）。返回 (偏移, 长度)。</summary>
    private static IEnumerable<(int Offset, int Length)> Chunks(byte[] data)
    {
        int start = 0;
        while (start < data.Length)
        {
            int maxLen = Math.Min(MaxChunk, data.Length - start);
            ulong h = 0;
            int cut = maxLen;
            for (int i = 0; i < maxLen; i++)
            {
                h = (h << 1) + Gear[data[start + i]];
                if (i + 1 >= MinChunk && (h & Mask) == 0)
                {
                    cut = i + 1;
                    break;
                }
            }
            yield return (start, cut);
            start += cut;
        }
    }

    private static ulong HashBytes(ReadOnlySpan<byte> b)
    {
        ulong h = 14695981039346656037UL;
        foreach (byte x in b)
        {
            h ^= x;
            h *= 1099511628211UL;
        }
        return h;
    }

    /// <summary>gear 表（固定种子：生成端与本地测试必须完全一致）。</summary>
    private static readonly ulong[] Gear = BuildGear();

    private static ulong[] BuildGear()
    {
        var rnd = new Random(0x5EED);
        var t = new ulong[256];
        for (int i = 0; i < t.Length; i++)
            t[i] = (ulong)rnd.NextInt64();
        return t;
    }

    /// <summary>补丁大小的人话描述（日志用）。</summary>
    public static string Describe(long patchBytes, long newBytes)
        => newBytes <= 0
            ? $"{patchBytes / 1024}KB"
            : $"{patchBytes / 1024}KB（整包 {newBytes / 1024 / 1024}MB 的 {patchBytes * 100.0 / newBytes:F1}%）";
}
