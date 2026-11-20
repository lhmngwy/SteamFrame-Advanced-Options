using System.IO.Pipes;
using System.Security.Cryptography;
using SteamFrame.PatchMaintenance;
using SteamLinkVRSettings.Frame;

namespace SteamLinkVRSettings.Tests;

public sealed class IntegrationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SteamFrame-maintenance-tests-" + Guid.NewGuid().ToString("N"));
    public IntegrationTests() { Directory.CreateDirectory(directory); }
    public void Dispose() { Directory.Delete(directory, true); }

    [Fact] public void Store_round_trips_durably_and_rejects_corruption()
    {
        var store = new MaintenanceStore(directory);
        store.Write("journal.json", new MaintenanceJournal { State = "first" });
        store.Write("journal.json", new MaintenanceJournal { State = "second" });
        Assert.Equal("second", store.Read<MaintenanceJournal>("journal.json")!.State);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        File.WriteAllText(Path.Combine(directory, "journal.json"), "broken");
        Assert.Throws<System.Text.Json.JsonException>(() => store.Read<MaintenanceJournal>("journal.json"));
    }
    [Fact] public void Credentials_are_encrypted_and_removed_without_plaintext_persistence()
    {
        var store = new MaintenanceStore(directory); var credentials = new PushoverCredentials(new string('a', 30), new string('b', 30));
        store.SaveCredentials(credentials);
        Assert.Equal(credentials, store.ReadCredentials());
        Assert.DoesNotContain(credentials.Token, File.ReadAllText(Path.Combine(directory, "credentials.json")));
        store.RemoveCredentials(); Assert.False(store.HasCredentials); Assert.Null(store.ReadCredentials());
    }
    [Fact] public async Task Control_protocol_round_trips_without_a_real_service()
    {
        var name = "SteamFrame-tests-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var connected = server.WaitForConnectionAsync(timeout.Token); await client.ConnectAsync(timeout.Token); await connected;
        var request = ControlProtocol.ReadAsync<ControlRequest>(server, timeout.Token);
        await ControlProtocol.WriteAsync(client, new ControlRequest("disable"), timeout.Token);
        Assert.Equal("disable", (await request).Action);
        var reply = ControlProtocol.ReadAsync<ControlReply>(client, timeout.Token);
        await ControlProtocol.WriteAsync(server, new ControlReply(true, "disabled"), timeout.Token);
        Assert.True((await reply).Ok);
    }
    [Theory] [InlineData(-1)] [InlineData(0)] [InlineData(65537)]
    public async Task Control_protocol_rejects_unbounded_frames(int length)
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(length));
        await Assert.ThrowsAsync<InvalidDataException>(() => ControlProtocol.ReadAsync<ControlRequest>(stream, default));
    }
    [Fact] public async Task Cross_process_writer_lock_serializes_and_recovers_after_exception()
    {
        int active = 0, maximum = 0;
        var tasks = Enumerable.Range(0, 8).Select(_ => PatchOperationLock.RunAsync(async () =>
        { var count = Interlocked.Increment(ref active); maximum = Math.Max(count, maximum); await Task.Delay(10); Interlocked.Decrement(ref active); return 0; }));
        await Task.WhenAll(tasks); Assert.Equal(1, maximum);
        await Assert.ThrowsAsync<IOException>(() => PatchOperationLock.RunAsync<int>(() => throw new IOException()));
        Assert.Equal(42, await PatchOperationLock.RunAsync(() => Task.FromResult(42)));
    }
    private sealed class FileDriver(string path) : IMaintenanceDriver
    {
        private readonly BitratePatcher patcher = new(PatchManifest.LoadSupported(), () => false);
        public bool UpdateInProgress => false;
        public Task<BitratePatchStatus> InspectAsync(CancellationToken ct) => patcher.InspectAsync(path, ct);
        public Task<BitratePatchStatus> ApplyAsync(string expectedSourceSha256, CancellationToken ct) => patcher.ApplyExpectedAsync(path, expectedSourceSha256, ct);
    }
    [Fact] public async Task Real_patch_engine_handles_synthetic_update_backup_receipt_and_restore()
    {
        var path = Path.Combine(directory, "driver_vrlink.dll"); var original = new SyntheticEncoderImage().Bytes;
        File.WriteAllBytes(path, original);
        var store = new MaintenanceStore(Path.Combine(directory, "state"));
        var now = DateTimeOffset.UtcNow; var config = new MaintenanceConfig { Enabled = true };
        var engine = new MaintenanceEngine(new FileDriver(path), new(), state => store.Write("journal.json", state), () => now);
        await engine.TickAsync(config, default); now = now.AddSeconds(11); await engine.TickAsync(config, default);
        Assert.NotEqual(original, File.ReadAllBytes(path));
        var status = await new BitratePatcher().InspectAsync(path);
        Assert.True(status.IsPatched); Assert.Equal(BitrateBackupState.VerifiedOriginal, status.BackupState);
        Assert.True(File.Exists(status.ReceiptPath)); Assert.True(Assert.Single(engine.Journal.Queue).Success);
        // Simulate a Steam update restoring this exact original build, then let maintenance reapply.
        File.WriteAllBytes(path, original); now = now.AddSeconds(30); await engine.TickAsync(config, default);
        now = now.AddSeconds(11); await engine.TickAsync(config, default); Assert.Equal(2, engine.Journal.Queue.Count);
        config.Enabled = false; await engine.TickAsync(config, default);
        var patcher = new BitratePatcher(PatchManifest.LoadSupported(), () => false);
        await patcher.RestoreAsync(path); now = now.AddMinutes(1); await engine.TickAsync(config, default);
        Assert.Equal(original, File.ReadAllBytes(path)); Assert.Empty(engine.Journal.Queue);
    }
    [Theory] [InlineData("relative")] [InlineData("\\\\server\\share")]
    public void Config_rejects_relative_and_network_roots(string root)
    {
        var config = new MaintenanceConfig { SteamVrRoot = root, OwnerSid = "S-1-5-21-1-2-3-1000" };
        Assert.Throws<InvalidDataException>(() => config.Validate());
    }
    [Fact] public async Task Expected_source_hash_is_checked_inside_the_writer_lock()
    {
        var path = Path.Combine(directory, "driver_vrlink.dll"); var original = new SyntheticEncoderImage().Bytes;
        File.WriteAllBytes(path, original);
        var patcher = new BitratePatcher(PatchManifest.LoadSupported(), () => false);
        await Assert.ThrowsAsync<IOException>(() => patcher.ApplyExpectedAsync(path, new string('0', 64), TestContext.Current.CancellationToken));
        Assert.Equal(original, File.ReadAllBytes(path)); Assert.Single(Directory.GetFiles(directory));
    }
    [Fact] public async Task Cancelled_writer_wait_does_not_execute_or_poison_the_mutex()
    {
        using var ready = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var held = PatchOperationLock.RunAsync(() => { ready.Set(); release.Wait(TestContext.Current.CancellationToken); return Task.FromResult(0); }, TestContext.Current.CancellationToken);
        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); bool called = false;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PatchOperationLock.RunAsync(() => { called = true; return Task.FromResult(0); }, cancelled.Token));
            Assert.False(called);
        }
        finally { release.Set(); await held; }
        Assert.Equal(1, await PatchOperationLock.RunAsync(() => Task.FromResult(1), TestContext.Current.CancellationToken));
    }
    [Fact] public async Task Writer_mutex_excludes_a_separate_windows_process()
    {
        using var ready = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var held = PatchOperationLock.RunAsync(() => { ready.Set(); release.Wait(TestContext.Current.CancellationToken); return Task.FromResult(0); }, TestContext.Current.CancellationToken);
        System.Diagnostics.Process? process = null;
        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            var script = "$m=[System.Threading.Mutex]::OpenExisting('" + PatchOperationLock.Name
                + "',[System.Security.AccessControl.MutexRights]'Synchronize, Modify'); if($m.WaitOne(200)){$m.ReleaseMutex();exit 2};$m.Dispose();exit 0";
            var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
            process = System.Diagnostics.Process.Start(start)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode); Assert.Equal("", await process.StandardError.ReadToEndAsync(timeout.Token));
        }
        finally
        {
            if (process != null) { if (!process.HasExited) process.Kill(true); process.Dispose(); }
            release.Set(); await held;
        }
    }
    [Fact] public void Store_limits_state_size()
    {
        File.WriteAllBytes(Path.Combine(directory, "large.json"), new byte[1_048_577]);
        Assert.Throws<InvalidDataException>(() => new MaintenanceStore(directory).Read<MaintenanceJournal>("large.json"));
    }
}
