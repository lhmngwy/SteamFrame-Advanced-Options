using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Hosting;
using SteamFrame.PatchMaintenance;

namespace SteamFrame.PatchService;

internal sealed class MaintenanceWorker : BackgroundService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly MaintenanceStore store = new(MaintenancePaths.Data);
    private MaintenanceConfig config = null!;
    private MaintenanceEngine engine = null!;
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 65536 };
    private PushoverClient notifications = null!;
    private long nextCheck;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await RunAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            try
            {
                System.Diagnostics.EventLog.WriteEntry(MaintenancePaths.ServiceName,
                    "Automatic driver maintenance stopped (" + error.GetType().Name + "). Check the protected service configuration, journal and folder permissions.",
                    System.Diagnostics.EventLogEntryType.Error);
            }
            catch (Exception logError) when (logError is System.ComponentModel.Win32Exception or InvalidOperationException or System.Security.SecurityException) { }
            // A graceful BackgroundService StopHost is reported to SCM as success and would skip recovery.
            // Every operation has unwound its guards before this exit; the durable journal handles restart.
            Environment.Exit(1);
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        config = store.Read<MaintenanceConfig>("config.json") ?? throw new InvalidDataException("Service configuration missing.");
        config.Validate();
        var journal = store.Read<MaintenanceJournal>("journal.json") ?? new();
        if (journal.SchemaVersion != 1 || journal.Queue == null || journal.Queue.Count > 100) throw new InvalidDataException("Invalid maintenance journal.");
        engine = new(new InstalledDriver(config.SteamVrRoot), journal, state => store.Write("journal.json", state));
        notifications = new(http);
        using var monitor = new DriverChangeMonitor(config.SteamVrRoot);
        using var pipeLifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var pipeTask = ServeAsync(pipeLifetime.Token);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (pipeTask.IsCompleted) await pipeTask; // A broken control endpoint must fail the service, not run unmanageable.
                await gate.WaitAsync(stoppingToken);
                try
                {
                    if (config.Enabled) monitor.EnsureWatching(); else monitor.Dispose();
                    if (Environment.TickCount64 >= nextCheck && monitor.Settled)
                    {
                        await engine.TickAsync(config, stoppingToken);
                        nextCheck = Environment.TickCount64 + 30_000;
                    }
                    await DeliverNotificationsAsync(stoppingToken);
                }
                finally { gate.Release(); }
                // Detect resume and missed watcher events without relying on an interactive user session.
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        finally { pipeLifetime.Cancel(); await pipeTask; }
    }

    private async Task DeliverNotificationsAsync(CancellationToken ct)
    {
        PushoverCredentials? credentials = null;
        if (config.Enabled && engine.Journal.Queue.Count > 0 && !engine.Journal.DeliveryPaused && engine.Journal.DeliveryResumeAt <= DateTimeOffset.UtcNow)
        {
            try { credentials = store.ReadCredentials(); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException
                or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                engine.Journal.DeliveryPaused = true;
                engine.Journal.NotificationStatus = "Saved Pushover credentials could not be read; replace or remove them. Driver maintenance is unaffected.";
                engine.Persist(); return;
            }
        }
        await engine.DeliverAsync(config, credentials, notifications, ct);
    }

    private async Task ServeAsync(CancellationToken ct)
    {
        var security = CreatePipeSecurity(config.OwnerSid);
        while (!ct.IsCancellationRequested)
        {
            using var pipe = NamedPipeServerStreamAcl.Create(MaintenancePaths.PipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 65536, 65536, security);
            try
            {
                await pipe.WaitForConnectionAsync(ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(40));
                var request = await ControlProtocol.ReadAsync<ControlRequest>(pipe, timeout.Token);
                await gate.WaitAsync(timeout.Token);
                ControlReply reply;
                try { reply = await HandleAsync(request, timeout.Token); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.Security.Cryptography.CryptographicException)
                {
                    reply = new(false, request.Action == "health"
                        ? "The service could not verify driver read access and temporary-file creation/deletion: " + ex.Message
                        : "The maintenance request could not be completed. Check configuration and permissions.");
                }
                finally { gate.Release(); }
                await ControlProtocol.WriteAsync(pipe, reply, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or System.Text.Json.JsonException) { if (ct.IsCancellationRequested) return; }
        }
    }

    internal static PipeSecurity CreatePipeSecurity(string ownerSid)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        using var identity = WindowsIdentity.GetCurrent();
        foreach (var sid in new[] { new SecurityIdentifier(ownerSid), new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            identity.User!, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new(sid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    private async Task<ControlReply> HandleAsync(ControlRequest request, CancellationToken ct)
    {
        if (request.ProtocolVersion != 1) return new(false, "App/service protocol mismatch. Update the installed service.");
        switch (request.Action)
        {
            case "status": break;
            case "health": await InstalledDriver.ProbeAccessAsync(config.SteamVrRoot, ct); break;
            case "check":
                // Explicit user retry still runs every byte/backup/runtime check; it never forces a patch.
                engine.RequestCheck();
                nextCheck = 0; break;
            case "disable":
                config.Enabled = false; store.Write("config.json", config);
                engine.Journal.State = "Disabled"; engine.SettingsChanged(config);
                break; // gate guarantees no in-flight patch remains before acknowledging.
            case "enable":
                config.Validate(); config.Enabled = true; store.Write("config.json", config); nextCheck = 0;
                break;
            case "settings":
                var candidate = new MaintenanceConfig { SteamVrRoot = config.SteamVrRoot, OwnerSid = config.OwnerSid,
                    Enabled = config.Enabled, NotifySuccess = request.NotifySuccess, NotifyFailure = request.NotifyFailure, Device = request.Device };
                candidate.Validate();
                if (request.Credentials != null)
                {
                    var validated = await notifications.ValidateAsync(request.Credentials, candidate.Device, ct);
                    if (!validated.Accepted) return new(false, validated.Description);
                    store.SaveCredentials(request.Credentials);
                }
                config = candidate; store.Write("config.json", config); engine.SettingsChanged(config);
                break;
            case "remove-credentials":
                store.RemoveCredentials(); engine.Journal.Queue.Clear(); engine.Persist(); break;
            case "test":
                var credentials = store.ReadCredentials();
                if (credentials == null) return new(false, "Save Pushover credentials first.");
                var result = await notifications.SendAsync(credentials, config.Device, "SteamFrame automatic driver patch notifications are configured.", DateTimeOffset.UtcNow, ct);
                return new(result.Accepted, result.Description, Snapshot());
            default: return new(false, "Unknown maintenance action.");
        }
        return new(true, "Maintenance settings updated.", Snapshot());
    }
    private MaintenanceSnapshot Snapshot() => new(1, typeof(MaintenanceWorker).Assembly.GetName().Version!.ToString(), config.Enabled,
        config.NotifySuccess, config.NotifyFailure, config.Device, store.HasCredentials, MaintenancePaths.Driver(config.SteamVrRoot), engine.Journal);
    public override void Dispose() { http.Dispose(); gate.Dispose(); base.Dispose(); }
}
