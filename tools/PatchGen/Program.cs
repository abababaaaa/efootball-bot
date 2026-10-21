// 差量补丁生成器（发布链工具）
// 用法：dotnet run --project tools/PatchGen -c Release -- <旧exe> <新exe> <补丁输出>
// CI 在打 tag 时对「上一个 Release 的 exe」生成一跳补丁，作为独立资产上传；
// 客户端更新时优先尝试该补丁，失败自动回退整包（见 UpdateApplier.TryDeltaAsync）。
using EfootballBot.Core.System;

if (args.Length < 3)
{
    Console.WriteLine("用法：PatchGen <旧exe> <新exe> <补丁输出>");
    return 2;
}

try
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    DeltaPatch.Create(args[0], args[1], args[2]);
    sw.Stop();

    long patch = new FileInfo(args[2]).Length;
    long newer = new FileInfo(args[1]).Length;
    Console.WriteLine($"PATCH OK: {args[2]} — {DeltaPatch.Describe(patch, newer)}，耗时 {sw.ElapsedMilliseconds}ms");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine("PATCH FAIL: " + ex);
    return 1;
}
