using SteamLinkVRSettings.Frame;

namespace SteamLinkVRSettings.Tests;

public sealed class CurrentBetaCapTests
{
    [Theory]
    [InlineData(0x140)]
    [InlineData(0x148)]
    public async Task Complete_cap_families_patch_both_ceilings_and_restore_exactly(int target)
    {
        var fixture = new SyntheticEncoderImage(capTarget: target);
        var plan = AdaptivePatchScanner.Discover(fixture.Bytes);
        Assert.Equal(0x3300, plan.Changes[0].FileOffset);
        Assert.Equal(0x3304, plan.Changes[1].FileOffset);
        Assert.Equal("0000fa43", plan.Changes[0].PatchedHex);
        Assert.Equal("0000fa43", plan.Changes[1].PatchedHex);
        Assert.Equal(6, plan.Changes.Count);
        using var directory = new DriverTestDirectory();
        var path = Path.Combine(directory.Root, "driver_vrlink.dll");
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllBytesAsync(path, fixture.Bytes, ct);
        var patcher = new BitratePatcher(PatchManifest.LoadSupported(), () => false);
        var installed = await patcher.ApplyAsync(path, ct);
        Assert.True(installed.IsPatched);
        Assert.Equal(BitrateBackupState.VerifiedOriginal, installed.BackupState);
        Assert.Equal(plan.PatchedSha256, installed.Sha256);
        Assert.True((await patcher.InspectAsync(path, ct)).CanRestore);
        await patcher.RestoreAsync(path, ct);
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(path, ct));
    }

    [Theory]
    [InlineData("unverified member offset")]
    [InlineData("adapter query slot")]
    [InlineData("branch target")]
    [InlineData("different selected register")]
    [InlineData("cap constant")]
    [InlineData("RIP reference")]
    [InlineData("function boundary")]
    [InlineData("ambiguous families")]
    public async Task New_family_still_refuses_changed_or_ambiguous_relationships(string change)
    {
        var fixture = new SyntheticEncoderImage(capTarget: 0x148);
        switch (change)
        {
            case "unverified member offset": fixture.Write32(0x100d, 0x14c); break;
            case "adapter query slot": fixture.Bytes[0x101d] = 0x90; break;
            case "branch target": fixture.Bytes[0x1024] = 8; break;
            case "different selected register": fixture.Bytes[0x1029] = 5; break;
            case "cap constant": fixture.Write32(0x3300, 0x43690000); break;
            case "RIP reference": fixture.Relative(0x102a, 0x102e, 0x3304); break;
            case "function boundary": fixture.Write32(0x5004, 0x102c); break;
            case "ambiguous families":
                fixture.Bytes.AsSpan(0x1000, 46).CopyTo(fixture.Bytes.AsSpan(0x1700));
                fixture.Write32(0x170d, 0x140);
                break;
        }
        Assert.Throws<InvalidDataException>(() => AdaptivePatchScanner.Discover(fixture.Bytes));
        using var directory = new DriverTestDirectory();
        var path = Path.Combine(directory.Root, "driver_vrlink.dll");
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllBytesAsync(path, fixture.Bytes, ct);
        var patcher = new BitratePatcher(PatchManifest.LoadSupported(), () => false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => patcher.ApplyAsync(path, ct));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(path, ct));
        Assert.Single(Directory.EnumerateFiles(directory.Root));
    }
}
