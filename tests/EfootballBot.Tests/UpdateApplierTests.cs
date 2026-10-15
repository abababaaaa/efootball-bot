using EfootballBot.Core.System;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>更新应用器：交换四象限判定 + rename-swap 真文件行为（含回滚）。
/// 背景：v2.0.5.2 的 cmd/bat copy /Y 覆盖式替换没有回滚能力，新 exe 被杀软截断/
/// 拦截时旧版已被毁（"更新后旧版复活"系列事故）；v2.0.5.6 重写为"运行中 exe 可改名"
/// 的原子交换 + .old 备份。备份删除延迟 10s——新版本启动即崩时备份仍在，可手动改回。</summary>
public class UpdateApplierTests : IDisposable
{
    private readonly string _dir;

    public UpdateApplierTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "updtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---------------- 四象限判定（纯函数） ----------------

    [Theory]
    [InlineData(true, true, UpdateApplier.StartupFinalizeAction.DeleteOld)]    // 正常更新完成：新 exe 已在跑，清备份
    [InlineData(false, true, UpdateApplier.StartupFinalizeAction.RestoreOld)]  // 交换中断（掉电等）：从备份还原
    [InlineData(true, false, UpdateApplier.StartupFinalizeAction.None)]        // 无更新残留
    [InlineData(false, false, UpdateApplier.StartupFinalizeAction.None)]       // 无从谈起
    public void DecideFinalizeAction_CoversAllQuadrants(bool exe, bool old,
        UpdateApplier.StartupFinalizeAction expected)
    {
        Assert.Equal(expected, UpdateApplier.DecideFinalizeAction(exe, old));
    }

    [Fact]
    public void OldPathFor_KeepsOriginalFileName()
    {
        // 用户常把 exe 改名存放（如 "EfootballBot (8).exe"）：备份名必须跟随原名，
        // 不能写死 EfootballBot.exe.old
        Assert.Equal(@"C:\x\EfootballBot (8).exe.old", UpdateApplier.OldPathFor(@"C:\x\EfootballBot (8).exe"));
    }

    // ---------------- rename-swap 真文件行为 ----------------

    [Fact]
    public void SwapExe_ReplacesContentAndLeavesBackup()
    {
        string exe = Path.Combine(_dir, "EfootballBot.exe");
        string newExe = Path.Combine(_dir, "download.bin");
        File.WriteAllText(exe, "OLD-VERSION-IMAGE");
        File.WriteAllText(newExe, "NEW-VERSION-IMAGE");

        UpdateApplier.SwapExe(exe, newExe);

        Assert.Equal("NEW-VERSION-IMAGE", File.ReadAllText(exe));              // 原路径=新版本
        Assert.Equal("OLD-VERSION-IMAGE", File.ReadAllText(exe + ".old"));     // 旧版本留作备份
        Assert.False(File.Exists(newExe));                                     // 新文件已移走
    }

    [Fact]
    public void SwapExe_MoveFails_RollsBackOriginal()
    {
        string exe = Path.Combine(_dir, "EfootballBot.exe");
        File.WriteAllText(exe, "OLD-VERSION-IMAGE");
        string missing = Path.Combine(_dir, "not-exists.bin"); // 移入源不存在 → 第二步必败

        Assert.ThrowsAny<Exception>(() => UpdateApplier.SwapExe(exe, missing));

        Assert.Equal("OLD-VERSION-IMAGE", File.ReadAllText(exe));              // 回滚成功：原样复原
        Assert.False(File.Exists(exe + ".old"));                               // 备份已归位，无残留
    }

    [Fact]
    public void SwapExe_StaleBackupFromPreviousCycle_IsReplaced()
    {
        // 上一轮更新完成后备份没删干净（如新版本 10s 内崩溃）又手动恢复了旧版继续用，
        // 再次更新时旧备份必须被本轮覆盖，不能因占用/残留报错中断
        string exe = Path.Combine(_dir, "EfootballBot.exe");
        string old = exe + ".old";
        string newExe = Path.Combine(_dir, "download.bin");
        File.WriteAllText(exe, "OLD-VERSION-IMAGE");
        File.WriteAllText(old, "STALE-BACKUP");
        File.WriteAllText(newExe, "NEW-VERSION-IMAGE");

        UpdateApplier.SwapExe(exe, newExe);

        Assert.Equal("NEW-VERSION-IMAGE", File.ReadAllText(exe));
        Assert.Equal("OLD-VERSION-IMAGE", File.ReadAllText(old));
    }
}
