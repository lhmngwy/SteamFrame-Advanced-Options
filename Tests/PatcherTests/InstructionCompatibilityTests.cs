using System.Security.Cryptography;
using SteamLinkVRSettings.Frame;

namespace SteamLinkVRSettings.Tests;

public sealed class InstructionCompatibilityTests
{
    [Theory]
    [InlineData(-0x10, 0x928, 0x9a60, 0x270, false, false, true, false)]
    [InlineData(0x318, 0x1200, 0xa000, 0x400, true, true, false, true)]
    [InlineData(0x118, 0x1500, 0xb000, 0x500, false, true, true, false)]
    public async Task DerivesStackObjectAndRegisterChangesAndRestoresExactly(int stack, int config, int table, int pointer, bool rdi, bool ecx, bool chain, bool scheduled)
    {
        var fixture = new SyntheticEncoderImage(stack, config, table, pointer, rdi, ecx, chain, scheduled);
        var plan = AdaptivePatchScanner.Discover(fixture.Bytes);
        Assert.Equal(fixture.Hook, plan.Changes[4].FileOffset);
        Assert.Equal(fixture.DefaultLevel, plan.Changes[2].FileOffset);
        Assert.Equal("b7000000", plan.Changes[2].OriginalHex);
        using var directory = new DriverTestDirectory();
        var path = Path.Combine(directory.Root, "driver_vrlink.dll");
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllBytesAsync(path, fixture.Bytes, ct);
        var patcher = new BitratePatcher(PatchManifest.LoadSupported(), () => false);
        var status = await patcher.ApplyAsync(path, ct);
        Assert.Equal(plan.PatchedSha256, status.Sha256);
        Assert.True(status.CanRestore);
        Assert.True((await patcher.InspectAsync(path, ct)).IsPatched);
        var time = File.GetLastWriteTimeUtc(path);
        await patcher.ApplyAsync(path, ct);
        Assert.Equal(time, File.GetLastWriteTimeUtc(path));
        await patcher.RestoreAsync(path, ct);
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(path, ct));
    }

    [Theory]
    [InlineData("query slot")]
    [InlineData("config pointer")]
    [InlineData("config version")]
    [InlineData("copy size")]
    [InlineData("query output")]
    [InlineData("level field")]
    [InlineData("level register")]
    [InlineData("level clobber")]
    [InlineData("object clobber")]
    [InlineData("codec GUID")]
    [InlineData("memcpy import")]
    [InlineData("occupied padding")]
    [InlineData("unwind cycle")]
    [InlineData("unrelated fragment")]
    [InlineData("branch into copy")]
    [InlineData("ambiguous preset")]
    [InlineData("ambiguous level")]
    public async Task RefusesBrokenRelationshipsWithoutWritingOrCreatingBackups(string change)
    {
        var fixture = new SyntheticEncoderImage(scheduled: change is "level clobber" or "object clobber");
        var bytes = fixture.Bytes;
        switch (change)
        {
            case "query slot": bytes[fixture.Query + 2] ^= 8; break;
            case "config pointer": bytes[fixture.ConfigPointerStore + 3] ^= 8; break;
            case "config version": bytes[fixture.PresetVersion + 16] ^= 1; break;
            case "copy size": bytes[fixture.Hook - 4] ^= 1; break;
            case "query output": bytes[fixture.Query - 1] ^= 8; break;
            case "level field": bytes[fixture.LevelStore + 2] ^= 4; break;
            case "level register": bytes[fixture.LevelStore + 1] ^= 8; break;
            case "level clobber": Convert.FromHexString("b80000000090").CopyTo(bytes, fixture.LevelStore - 6); break;
            case "object clobber": Convert.FromHexString("be0000000090").CopyTo(bytes, fixture.LevelStore - 6); break;
            case "codec GUID": bytes[0x3400] ^= 1; break;
            case "memcpy import": bytes[0x3122] ^= 1; break;
            case "occupied padding": bytes[0x1800] = 0xcc; break;
            case "unwind cycle": fixture.Write32(0x5014, fixture.Chain); fixture.Write32(fixture.Chain + 12, fixture.Chain); break;
            case "unrelated fragment": bytes[fixture.Chain] = 1; break;
            case "branch into copy":
                bytes[fixture.PresetEnd - 1] = 0xeb; bytes[fixture.PresetEnd] = unchecked((byte)(fixture.Hook - fixture.PresetEnd - 1));
                fixture.Write32(0x5000 + 3 * 12 + 4, fixture.PresetEnd + 1); break;
            case "ambiguous preset":
                bytes.AsSpan(0x1300, fixture.PresetEnd - 0x1300).CopyTo(bytes.AsSpan(0x1500));
                fixture.Relative(0x1500 + fixture.Hook - 0x1300 + 1, 0x1500 + fixture.Hook - 0x1300 + 5, 0x1600);
                AddFunction(fixture, 0x1500, fixture.PresetEnd + 0x200); break;
            case "ambiguous level":
                bytes.AsSpan(0x1400, fixture.LevelEnd - 0x1400 + 1).CopyTo(bytes.AsSpan(0x1500));
                // Relocated RIP references must still resolve to the real HEVC GUID.
                fixture.Relative(0x150a, 0x150e, 0x3400); fixture.Relative(0x151e, 0x1522, 0x3408);
                AddFunction(fixture, 0x1500, fixture.LevelEnd + 0x101); break;
        }
        var error = Assert.Throws<InvalidDataException>(() => AdaptivePatchScanner.Discover(bytes));
        if (change.StartsWith("ambiguous", StringComparison.Ordinal)) Assert.Contains("found 2", error.Message);
        if (change == "unwind cycle") Assert.Contains("Cyclic", error.Message);
        using var directory = new DriverTestDirectory();
        var path = Path.Combine(directory.Root, "driver_vrlink.dll");
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllBytesAsync(path, bytes, ct);
        var patcher = new BitratePatcher(PatchManifest.LoadSupported(), () => false);
        var status = await patcher.InspectAsync(path, ct);
        Assert.False(status.IsSupported);
        await Assert.ThrowsAsync<InvalidOperationException>(() => patcher.ApplyAsync(path, ct));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, ct));
        Assert.Single(Directory.EnumerateFiles(directory.Root));
    }

    private static void AddFunction(SyntheticEncoderImage fixture, int begin, int end)
    {
        // There are five entries when initialization uses two chained fragments.
        fixture.Write32(0x503c, begin); fixture.Write32(0x5040, end); fixture.Write32(0x5044, 0x51a0);
        fixture.Bytes[0x51a0] = 1;
        fixture.Write32(0x98 + 140, 6 * 12);
    }

    [Fact]
    public async Task InstalledFixturePatchesAndRestoresOnlyAnIsolatedCopy()
    {
        var fixture = Environment.GetEnvironmentVariable("SLVR_PATCHER_ADAPTIVE_FIXTURE");
        if (!File.Exists(fixture)) Assert.Skip("Set SLVR_PATCHER_ADAPTIVE_FIXTURE to test a newer original driver on an isolated copy.");
        var ct = TestContext.Current.CancellationToken;
        var original = await File.ReadAllBytesAsync(fixture!, ct);
        var plan = AdaptivePatchScanner.Discover(original);
        Assert.Equal(6, plan.Changes.Count);
        TestContext.Current.TestOutputHelper!.WriteLine($"Source: {plan.OriginalSha256}; output: {plan.PatchedSha256}");
        foreach (var segment in plan.Changes)
            TestContext.Current.TestOutputHelper.WriteLine($"0x{segment.FileOffset:x}: {segment.OriginalHex} -> {segment.PatchedHex} ({segment.Reason})");
        using var directory = new DriverTestDirectory();
        var path = Path.Combine(directory.Root, "driver_vrlink.dll");
        await File.WriteAllBytesAsync(path, original, ct);
        var patcher = new BitratePatcher(PatchManifest.LoadSupported(), () => false);
        var status = await patcher.InspectAsync(path, ct);
        status = await patcher.ApplyAsync(path, ct);
        Assert.True(status.CanRestore);
        Assert.Equal(plan.PatchedSha256, status.Sha256);
        Assert.Equal(original, await File.ReadAllBytesAsync(status.BackupPath, ct));
        Assert.True((await new BitratePatcher(PatchManifest.LoadSupported(), () => false).InspectAsync(path, ct)).IsPatched);
        await patcher.RestoreAsync(path, ct);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, ct));
        Assert.Equal(plan.OriginalSha256, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fixture!, ct))).ToLowerInvariant());
    }
}
