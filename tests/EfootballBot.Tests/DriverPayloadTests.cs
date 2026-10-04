using EfootballBot.Core.System;
using Xunit;

namespace EfootballBot.Tests;

public class DriverPayloadTests
{
    [Fact]
    public void ComputeSha256_matches_known_input()
    {
        string path = Path.Combine(Path.GetTempPath(), $"efb_hash_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
        try
        {
            // sha256(字节 01 02 03 04) 的标准值
            Assert.Equal(
                "9F64A747E1B97F131FABB6B447296C9B6F0201E79FB3C5356E6C77E89B6A806A",
                DriverPayload.ComputeSha256(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReleaseToAsync_extracts_official_installer_with_matching_hash()
    {
        string path = Path.Combine(Path.GetTempPath(), $"efb_setup_{Guid.NewGuid():N}.exe");
        await DriverPayload.ReleaseToAsync(path, CancellationToken.None);
        try
        {
            Assert.Equal(DriverPayload.ExpectedSha256, DriverPayload.ComputeSha256(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
