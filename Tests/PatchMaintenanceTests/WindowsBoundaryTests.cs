using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using SteamFrame.PatchMaintenance;
using SteamFrame.PatchService;

namespace SteamLinkVRSettings.Tests;

public sealed class WindowsBoundaryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SteamFrame-watcher-tests-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact] public void Scm_handle_marshals_and_closes_with_a_read_only_connection()
    {
        // Exercise native handle lifetime without creating/changing a service or requiring elevation.
        var connect = typeof(ServiceRecovery).GetMethod("OpenSCManager", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        using var handle = (System.Runtime.InteropServices.SafeHandle)connect.Invoke(null, new object?[] { null, null, (uint)1 })!;
        Assert.False(handle.IsInvalid);
        var layout = typeof(ServiceRecovery).GetNestedType("FailureActions", System.Reflection.BindingFlags.NonPublic)!;
        Assert.Equal(IntPtr.Size == 8 ? 40 : 20, System.Runtime.InteropServices.Marshal.SizeOf(layout));
    }

    [Fact] public void Pipe_acl_is_local_and_restricted_to_owner_and_service_administrators()
    {
        using var identity = WindowsIdentity.GetCurrent(); var sid = identity.User!;
        var security = MaintenanceWorker.CreatePipeSecurity(sid.Value);
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
        Assert.Contains(rules, r => r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.NetworkSid, null)) && r.AccessControlType == AccessControlType.Deny);
        Assert.Contains(rules, r => r.IdentityReference.Equals(sid) && r.AccessControlType == AccessControlType.Allow);
        Assert.DoesNotContain(rules, r => r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.WorldSid, null)) && r.AccessControlType == AccessControlType.Allow);
        var name = "SteamFrame-acl-tests-" + Guid.NewGuid().ToString("N");
        using var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 65536, 65536, security);
        Assert.Throws<UnauthorizedAccessException>(() => NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 65536, 65536, security));
    }
    [Fact] public async Task Watcher_observes_replacement_and_rearms_after_directory_disappears()
    {
        var driver = MaintenancePaths.Driver(root); var directory = Path.GetDirectoryName(driver)!;
        Directory.CreateDirectory(directory); File.WriteAllText(driver, "first");
        long time = 0;
        using var monitor = new DriverChangeMonitor(root, () => Interlocked.Read(ref time));
        monitor.EnsureWatching(); Interlocked.Exchange(ref time, 11_000); Assert.True(monitor.Settled);
        File.WriteAllText(driver, "replacement");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (monitor.Settled) await Task.Delay(10, timeout.Token);
        Directory.Delete(directory, true); monitor.EnsureWatching();
        Directory.CreateDirectory(directory); monitor.EnsureWatching(); Interlocked.Exchange(ref time, 22_000);
        File.WriteAllText(driver, "new directory");
        while (monitor.Settled) await Task.Delay(10, timeout.Token);
        Assert.False(monitor.Settled);
    }
    [Fact] public void Steam_update_metadata_defers_incomplete_installations()
    {
        var runtime = Path.Combine(root, "steamapps", "common", "SteamVR"); Directory.CreateDirectory(runtime);
        var manifest = Path.Combine(root, "steamapps", "appmanifest_250820.acf");
        var driver = new InstalledDriver(runtime);
        File.WriteAllText(manifest, "\"StateFlags\" \"4\""); Assert.False(driver.UpdateInProgress);
        File.WriteAllText(manifest, "\"StateFlags\" \"1026\""); Assert.True(driver.UpdateInProgress);
        File.WriteAllText(manifest, "\"StateFlags\" \"4\"");
        Directory.CreateDirectory(Path.Combine(root, "steamapps", "downloading", "250820")); Assert.True(driver.UpdateInProgress);
    }
    [Fact] public async Task Service_health_probe_checks_access_without_modifying_the_driver_or_leaving_files()
    {
        var driver = MaintenancePaths.Driver(root); var directory = Path.GetDirectoryName(driver)!;
        Directory.CreateDirectory(directory); File.WriteAllBytes(driver, new byte[] { 1, 2, 3 });
        await InstalledDriver.ProbeAccessAsync(root, TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(driver)); Assert.Single(Directory.GetFiles(directory));
        using var locked = new FileStream(driver, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<IOException>(() => InstalledDriver.ProbeAccessAsync(root, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(directory));
    }
    [Fact] public void Store_cannot_escape_its_directory()
    {
        Directory.CreateDirectory(root); var store = new MaintenanceStore(root);
        Assert.Throws<ArgumentException>(() => store.Write("../outside.json", new { Value = "bad" }));
        Assert.Throws<ArgumentException>(() => store.Remove("credentials.json:stream"));
    }
}
