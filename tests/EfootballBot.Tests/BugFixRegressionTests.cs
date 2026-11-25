using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using EfootballBot.Core;
using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Bot.Scenarios.PeakLadder;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Config;
using EfootballBot.Core.Input;
using EfootballBot.Core.Logging;
using EfootballBot.Core.System;
using EfootballBot.Core.Vision;
using Xunit;

namespace EfootballBot.Tests;

public class BugFixRegressionTests
{
    [Fact]
    public void VigemPad_DisconnectWhenNotConnected_IsIdempotent()
    {
        using var pad = new VigemPad();
        pad.Disconnect();
        pad.Disconnect();
        Assert.False(pad.IsConnected);
        Assert.Equal(-1, pad.UserIndex);
    }

    [Fact]
    public void CleanupStaleArtifacts_WhenClean_DoesNotThrow()
    {
        UpdateApplier.CleanupStaleArtifacts();
    }

    [Fact]
    public void AbiStructSizes_MatchWindowsX64Contracts()
    {
        if (IntPtr.Size == 8)
        {
            Assert.Equal(40, ForegroundMouse.InputStructSize);
            Assert.Equal(16, CrashReporter.MinidumpExceptionInfoSize);
        }
        Assert.Equal(45, Marshal.SizeOf<DwmNative.DwmThumbnailProperties>());
    }

    [Fact]
    public void MainWindow_PassesLogFilter_KeyIncludesWarnAndError()
    {
        Assert.True(MainWindow.PassesLogFilter(LogLevel.Success, "Key"));
        Assert.True(MainWindow.PassesLogFilter(LogLevel.Warn, "Key"));
        Assert.True(MainWindow.PassesLogFilter(LogLevel.Error, "Key"));
        Assert.False(MainWindow.PassesLogFilter(LogLevel.Info, "Key"));
        Assert.False(MainWindow.PassesLogFilter(LogLevel.Debug, "Key"));

        Assert.True(MainWindow.PassesLogFilter(LogLevel.Warn, "Warn"));
        Assert.True(MainWindow.PassesLogFilter(LogLevel.Error, "Warn"));
        Assert.False(MainWindow.PassesLogFilter(LogLevel.Success, "Warn"));
    }

    [Fact]
    public void DiagReporter_RedactSecrets_MasksWebhooksAndKeys()
    {
        string input = """
            {
              "Notify": { "WebhookUrl": "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=secret-abc", "InstanceName": "pc1" },
              "UnlimitedOcr": { "ApiKey": "sk-test-12345" }
            }
            """;
        string redacted = DiagReporter.RedactSecrets(input);
        Assert.DoesNotContain("secret-abc", redacted);
        Assert.DoesNotContain("sk-test-12345", redacted);
        Assert.Contains("pc1", redacted);
    }

    [Fact]
    public void BackgroundParker_ParkRecord_RoundTripsLarge64BitHwnd()
    {
        long hwnd = 0x00007FF812345678L;
        var rec = new BackgroundParker.ParkRecord("eFootball", hwnd, 10, 20, 1280, 720, "2026-11-25 12:00:00");
        string json = JsonSerializer.Serialize(rec);
        var loaded = JsonSerializer.Deserialize<BackgroundParker.ParkRecord>(json);
        Assert.NotNull(loaded);
        Assert.Equal(hwnd, loaded!.Hwnd);
        Assert.Equal("eFootball", loaded.ProcessName);
    }

    [Fact]
    public void GameSettingsLocator_ParseVdfPathLine_ExtractsSecondQuotedValue()
    {
        Assert.Equal(@"D:\SteamLibrary", GameSettingsLocator.ParseVdfPathLine("    \"path\"\t\t\"D:\\\\SteamLibrary\""));
        Assert.Equal(@"E:\Program Files (x86)\SteamLib", GameSettingsLocator.ParseVdfPathLine("\"path\" \"E:\\\\Program Files (x86)\\\\SteamLib\""));
        Assert.Null(GameSettingsLocator.ParseVdfPathLine("    \"label\"    \"\""));
    }

    [Fact]
    public void DeltaPatch_LargeContiguousInsertOver8Mb_SplitsAndAppliesCleanly()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ef-dp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string oldPath = Path.Combine(dir, "old.bin");
            string newPath = Path.Combine(dir, "new.bin");
            string patchPath = Path.Combine(dir, "p.bin");
            string outPath = Path.Combine(dir, "out.bin");

            File.WriteAllBytes(oldPath, new byte[1024]);
            var payload = new byte[9 * 1024 * 1024 + 123];
            new Random(42).NextBytes(payload);
            File.WriteAllBytes(newPath, payload);

            DeltaPatch.Create(oldPath, newPath, patchPath);
            DeltaPatch.Apply(oldPath, patchPath, outPath);
            Assert.Equal(payload, File.ReadAllBytes(outPath));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void DeltaPatch_ForgedNegativeHeader_ThrowsInvalidData()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ef-dp-bad-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string oldPath = Path.Combine(dir, "old.bin");
            string patchPath = Path.Combine(dir, "forged.bin");
            string outPath = Path.Combine(dir, "out.bin");
            File.WriteAllBytes(oldPath, new byte[64]);
            using (var bw = new BinaryWriter(File.Create(patchPath)))
            {
                bw.Write(Encoding.ASCII.GetBytes("EBDP1\0"));
                bw.Write(0L);
                bw.Write(-1);
                bw.Write(new byte[32]);
            }
            Assert.Throws<InvalidDataException>(() => DeltaPatch.Apply(oldPath, patchPath, outPath));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void UpdateApplier_SwapExe_RefreshesOldBackupTimestamp()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ef-swap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string exe = Path.Combine(dir, "Bot.exe");
            string newExe = Path.Combine(dir, "Bot.exe.new");
            File.WriteAllBytes(exe, new byte[] { 1, 2, 3 });
            File.WriteAllBytes(newExe, new byte[] { 4, 5, 6 });
            File.SetLastWriteTimeUtc(exe, DateTime.UtcNow.AddDays(-30));

            UpdateApplier.SwapExe(exe, newExe);
            string oldExe = UpdateApplier.OldPathFor(exe);
            Assert.True(File.Exists(oldExe));
            Assert.True(DateTime.UtcNow - File.GetLastWriteTimeUtc(oldExe) < TimeSpan.FromMinutes(1));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void FastGithubKernel_StripBlock_UnmatchedBeginDoesNotEatFollowingEntries()
    {
        string raw = "127.0.0.1 localhost\r\n# >>> FastGithub hosts >>>\r\n1.2.3.4 example.com\r\n192.168.1.50 nas.local\r\n";
        string stripped = FastGithubKernel.StripBlock(raw);
        Assert.Contains("127.0.0.1 localhost", stripped);
        Assert.Contains("192.168.1.50 nas.local", stripped);
    }

    [Fact]
    public void LineReread_MapBack_DividesCropSizeByScale()
    {
        var frame = new FrameData(new byte[1280 * 720 * 4], 1280, 720, 0);
        var mapped = LineReread.MapBack(
            new OcrWord("test", 0.25, 0.5, 0.5, 0.25, 0.99f),
            offX: 100, offY: 200, cropW: 400, cropH: 200, scale: 2, frame);
        // srcW = 400 / 2 = 200, px = 100 + 0.25 * 200 = 150 -> 150 / 1280
        Assert.Equal(150.0 / 1280.0, mapped.X, precision: 6);
        Assert.Equal(250.0 / 720.0, mapped.Y, precision: 6);
        Assert.Equal(100.0 / 1280.0, mapped.W, precision: 6);
        Assert.Equal(25.0 / 720.0, mapped.H, precision: 6);
    }

    [Fact]
    public void PeakLadderBrain_ParsePlayableCount_PrefersAdjacentNumberOverTrailingScore()
    {
        var ocr = new OcrResult(new[]
        {
            new OcrWord("本周可参赛场次: 12  积分 9999", 0.3, 0.5, 0.4, 0.05, 0.99f),
        }, 1280, 720);
        Assert.Equal(12, PeakLadderBrain.ParsePlayableCount(ocr));
    }

    [Fact]
    public void AppConfig_LoadFrom_IoExceptionDoesNotQuarantineHealthyFileAsBad()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ef-lock-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "SchemaVersion": 3 }""");
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var cfg = AppConfig.LoadFrom(path);
                Assert.NotNull(cfg);
            }
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".bad"));
        }
        finally
        {
            try { File.Delete(path); } catch { }
            try { File.Delete(path + ".bad"); } catch { }
        }
    }

    [Fact]
    public void CnAdvance_FindButton_PrefersLowerButtonAndWholeWordOk()
    {
        var ocr = new OcrResult(new[]
        {
            new OcrWord("TOKEN", 0.5, 0.9, 0.1, 0.04, 0.99f),
            new OcrWord("确定", 0.4, 0.25, 0.1, 0.04, 0.99f),
            new OcrWord("确定", 0.45, 0.78, 0.12, 0.05, 0.99f),
        }, 1280, 720);

        var okBtn = CnAdvance.FindButton(ocr, null, "OK");
        Assert.Null(okBtn);

        var confirmBtn = CnAdvance.FindButton(ocr, null, "确定");
        Assert.NotNull(confirmBtn);
        Assert.True(confirmBtn!.Value.Y > 0.7);
    }

    [Fact]
    public void Updater_BuildDownloadUrls_AllowsCustomGithubAssetName()
    {
        string prev = Updater.R2PublicBaseUrl;
        try
        {
            Updater.R2PublicBaseUrl = "https://cdn.example.com";
            var urls = Updater.BuildDownloadUrls(new Version(9, 0, 1), "EfootballBot-9.0.1.exe", githubFileName: "EfootballBot.exe");
            Assert.Contains(urls, u => u == "https://cdn.example.com/EfootballBot-9.0.1.exe");
            Assert.Contains(urls, u => u.EndsWith("/EfootballBot.exe"));
        }
        finally
        {
            Updater.R2PublicBaseUrl = prev;
        }
    }

    [Fact]
    public void MatchSessionTracker_MarkAtMenu_ResetsRestartMenuGate()
    {
        var t = new MatchSessionTracker();
        var t0 = new DateTime(2026, 11, 25, 10, 0, 0);
        t.Observe(MatchScreen.InMatch, t0);
        t.Observe(MatchScreen.FullTime, t0.AddMinutes(6));
        t.Abort();
        t.MarkAtMenu();
        // MarkAtMenu clears internal _needMenuBeforeRestart so clean start after skip can proceed immediately
        Assert.False(t.Active);
    }

    [Fact]
    public void DryRunPad_XInputProxy_ExposesInnerAndSkipsConnectDisconnect()
    {
        using var inner = new ProxyPad();
        using var dry = new DryRunPad(inner, (_, _) => { });
        Assert.Same(inner, dry.Inner);
        dry.Connect();
        Assert.False(inner.IsConnected);
        dry.Disconnect();
        Assert.False(inner.IsConnected);
    }

    [Fact]
    public void MaxLevelSwapBrain_HasDialogConfirmWords_RecognizesOkAndYes()
    {
        var okOcr = new OcrResult(new[] { new OcrWord("OK", 0.5, 0.7, 0.08, 0.04, 0.99f) }, 1280, 720);
        var yesOcr = new OcrResult(new[] { new OcrWord("是", 0.5, 0.7, 0.05, 0.04, 0.99f) }, 1280, 720);
        Assert.True(MaxLevelSwapBrain.HasDialogConfirmWords(okOcr));
        Assert.True(MaxLevelSwapBrain.HasDialogConfirmWords(yesOcr));
    }

    [Fact]
    public void DiagReporter_ReadAllTextShared_ReadsActiveLockedLog()
    {
        string path = Path.Combine(Path.GetTempPath(), $"efootball-diag-{Guid.NewGuid():N}.yml");
        try
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var sw = new StreamWriter(fs, new UTF8Encoding(true)))
            {
                sw.WriteLine("WebhookUrl: https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=secret-123");
                sw.Flush();

                string raw = DiagReporter.ReadAllTextShared(path);
                string redacted = DiagReporter.RedactSecrets(raw);
                Assert.Contains("WebhookUrl", redacted);
                Assert.DoesNotContain("secret-123", redacted);
            }
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void MainWindowLogs_LogPrefixRegex_MatchesMultilineMessages()
    {
        var m = MainWindow.LogPrefixRegex.Match("[12:34:56] [测试] 第一行\n第二行");
        Assert.True(m.Success);
        Assert.Equal("12:34:56", m.Groups["t"].Value);
        Assert.Equal("[测试]", m.Groups["tag"].Value);
        Assert.Equal("第一行\n第二行", m.Groups["rest"].Value);
    }

    [Fact]
    public void FileLogger_StripLeadingTimestamp_PreservesBracketedTags()
    {
        Assert.Equal("[MyLeague] 已完成", FileLogger.StripLeadingTimestamp("[MyLeague] 已完成"));
        Assert.Equal("[CnWeekly] 进主页", FileLogger.StripLeadingTimestamp("[12:34:56] [CnWeekly] 进主页"));
    }

    [Fact]
    public void SharedPadWriter_RecoversOddSeqAndIgnoresPostDisposeCalls()
    {
        string name = $"Local\\EfootballBot.Test.{Guid.NewGuid():N}";
        using var holder = MemoryMappedFile.CreateOrOpen(name, SharedPadWriter.StructSize);
        using (var accessor = holder.CreateViewAccessor(0, SharedPadWriter.StructSize))
        {
            accessor.Write(12, 3L);
        }

        var writer = new SharedPadWriter(name);
        using (var accessor = holder.CreateViewAccessor(0, SharedPadWriter.StructSize))
        {
            Assert.Equal(4L, accessor.ReadInt64(12));
        }

        writer.Dispose();
        writer.SetActive(true);
        writer.SetTargets(SharedPadWriter.TargetGlobal);
        Assert.False(writer.ProxySeen());
    }

    [Fact]
    public void AppConfig_TryLoadFrom_ReturnsFalseOnCorruptJson()
    {
        string path = Path.Combine(Path.GetTempPath(), $"efootball-corrupt-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ invalid json");
            Assert.False(AppConfig.TryLoadFrom(path, out var cfg));
            Assert.Null(cfg);
        }
        finally
        {
            try { File.Delete(path); } catch { }
            try { File.Delete(path + ".bad"); } catch { }
        }
    }

    [Fact]
    public void SidecarDetectors_DoubleDispose_IsIdempotent()
    {
        var cfg = new VisionSidecarConfig { ExePath = typeof(SidecarDetector).Assembly.Location };
        var det = new SidecarDetector(cfg);
        det.Dispose();
        det.Dispose();

        var ocr = new SidecarOcrBackend(cfg);
        ocr.Dispose();
        ocr.Dispose();
    }
}
