using SteamLinkVRSettings.Frame;

namespace SteamFrame.PatchMaintenance;

/// <summary>One serialized reconciliation; all decisions and notification incidents survive restarts.</summary>
public sealed class MaintenanceEngine(IMaintenanceDriver driver, MaintenanceJournal journal, Action<MaintenanceJournal> save,
    Func<DateTimeOffset>? clock = null)
{
    private DateTimeOffset Now => (clock ?? (() => DateTimeOffset.UtcNow))();
    public MaintenanceJournal Journal { get; } = journal;
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan FailureGrace = TimeSpan.FromMinutes(10);

    public void RequestCheck()
    {
        // Explicit revalidation (including after a service/scanner update) clears
        // the old decision, never the source/backup verification requirements.
        Journal.BlockedIdentity = null;
        Journal.InFlightSource = null;
        Persist();
    }

    public async Task TickAsync(MaintenanceConfig config, CancellationToken ct)
    {
        Journal.LastCheck = Now;
        Prune(config);
        if (!config.Enabled) { Journal.State = "Disabled"; Persist(); return; }
        var status = await driver.InspectAsync(ct);
        var identity = status.Sha256 ?? status.State.ToString();
        if (identity != Journal.Identity)
        {
            Journal.Identity = identity; Journal.IncidentId = Guid.NewGuid().ToString("N")[..12];
            Journal.ObservedAt = Now; Journal.ReportedFailure = null; Journal.BlockedIdentity = null;
        }
        if (status.BackupState == BitrateBackupState.Mismatch)
        { Fail(config, "Recovery files could not be verified. Automatic writes are blocked.", true); return; }
        if (status.IsPatched)
        {
            // Recover a crash after replacement, but never report pre-existing patches as our success.
            if (Journal.InFlightSource != null && status.BackupSha256 == Journal.InFlightSource
                && status.BackupState == BitrateBackupState.VerifiedOriginal)
                Success(config, status);
            Journal.InFlightSource = null;
            Journal.State = status.BackupState == BitrateBackupState.VerifiedOriginal ? "Watching; patch and original backup verified" : "Patch verified; matching original backup is missing";
            Persist(); return;
        }
        if (Journal.BlockedIdentity == identity) { Persist(); return; }
        if (driver.UpdateInProgress || Now - Journal.ObservedAt < SettleTime)
        { Journal.State = "Update detected; waiting for files to settle"; Persist(); return; }
        if (status.State == BitratePatchState.Unsupported)
        { Fail(config, "Unsupported driver layout. No driver changes were made. Update the app and installed maintenance service, then retry compatibility checks.", true); return; }
        if (status.State is BitratePatchState.Missing or BitratePatchState.Unavailable)
        {
            if (Now - Journal.ObservedAt >= FailureGrace) Fail(config, "Driver or recovery files remain unavailable. Check the watched installation and file permissions.", false);
            else { Journal.State = "Waiting for the driver and recovery files to become readable"; Persist(); }
            return;
        }
        if (status.VrServerRunning) { Journal.State = "Waiting for SteamVR to close; the running session will not be interrupted"; Persist(); return; }
        Journal.InFlightSource = status.Sha256;
        Journal.State = "Applying patch"; Persist(); // Durable before any write.
        try
        {
            var patched = await driver.ApplyAsync(status.Sha256!, ct);
            if (!patched.IsPatched || patched.BackupState != BitrateBackupState.VerifiedOriginal || patched.BackupSha256 != Journal.InFlightSource)
                throw new InvalidDataException("Patch verification failed.");
            Success(config, patched);
            Journal.Identity = patched.Sha256!; Journal.BlockedIdentity = null;
            Journal.State = "Watching; patch and original backup verified";
            Journal.InFlightSource = null; Persist();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            // SteamVR or Steam may have won the race. Fresh inspection decides whether it is safe to retry.
            var fresh = await driver.InspectAsync(ct);
            if (ex is PatchVerificationException)
            {
                Journal.Identity = fresh.Sha256 ?? Journal.Identity;
                Journal.InFlightSource = null;
                Fail(config, "Post-replacement verification failed. Files may have changed. Recovery evidence was retained; inspect driver details before retrying.", true);
                return;
            }
            if (fresh.IsPatched && fresh.BackupState == BitrateBackupState.VerifiedOriginal && fresh.BackupSha256 == status.Sha256)
            {
                Journal.InFlightSource = null; Journal.Identity = fresh.Sha256!;
                Journal.State = "Watching; patch verified after another process changed the driver";
                Persist(); return;
            }
            if (fresh.VrServerRunning && !fresh.IsPatched)
            { Journal.InFlightSource = null; Journal.State = "Waiting for SteamVR to close"; Persist(); return; }
            if (fresh.Sha256 != status.Sha256 && !fresh.IsPatched)
            { Journal.InFlightSource = null; Journal.State = "Driver changed; waiting for the next check"; Persist(); return; }
            Journal.InFlightSource = null;
            Fail(config, "Patch application or verification failed. Files may have changed; inspect driver details before retrying. Recovery evidence was retained.", true);
        }
    }

    private void Success(MaintenanceConfig config, BitratePatchStatus status)
    {
        Journal.LastSuccess = Now;
        if (config.NotifySuccess) Enqueue(true, $"500 Mbps and HEVC 6.2 patch applied and verified. Original backup verified. {status.InstalledVersion}");
    }
    private void Fail(MaintenanceConfig config, string reason, bool block)
    {
        Journal.State = reason;
        if (block) Journal.BlockedIdentity = Journal.Identity;
        if (Journal.ReportedFailure != Journal.IncidentId)
        {
            if (config.NotifyFailure) Enqueue(false, reason);
            Journal.ReportedFailure = Journal.IncidentId;
        }
        Persist();
    }
    private void Enqueue(bool success, string message)
    {
        var id = Journal.IncidentId + (success ? ":success" : ":failure");
        if (!Journal.Queue.Any(x => x.Id == id)) Journal.Queue.Add(new(id, success, message + $" Incident {Journal.IncidentId}.", Now));
        Journal.Queue = Journal.Queue.TakeLast(100).ToList();
    }
    public void Prune(MaintenanceConfig config) => Journal.Queue.RemoveAll(x => !config.Enabled || Now - x.At > TimeSpan.FromHours(24)
        || x.Success && !config.NotifySuccess || !x.Success && !config.NotifyFailure);
    public async Task DeliverAsync(MaintenanceConfig config, PushoverCredentials? credentials, INotificationSender sender, CancellationToken ct)
    {
        var before = Journal.Queue.Count;
        Prune(config);
        if (before != Journal.Queue.Count) Persist();
        if (Journal.DeliveryPaused || Journal.DeliveryResumeAt > Now) return;
        if (credentials == null)
        {
            if (Journal.Queue.Count > 0 && Journal.NotificationStatus != "Pushover credentials not configured")
            { Journal.NotificationStatus = "Pushover credentials not configured"; Persist(); }
            return;
        }
        var pending = Journal.Queue.FirstOrDefault(x => x.NextAttempt <= Now);
        if (pending == null) return;
        // Reserve a retry time durably before HTTP, including when the process crashes during the call.
        pending.Attempts++;
        pending.NextAttempt = Now.AddSeconds(Math.Min(3600, 30 * Math.Pow(2, Math.Min(pending.Attempts - 1, 7))));
        Persist();
        var result = await sender.SendAsync(credentials, config.Device, pending.Message, pending.At, ct);
        Journal.NotificationStatus = result.Description;
        if (result.Accepted) Journal.Queue.Remove(pending);
        else if (result.PermanentFailure)
        {
            // Pause all deliveries until settings are explicitly saved again.
            Journal.DeliveryPaused = true;
            foreach (var item in Journal.Queue) item.NextAttempt = DateTimeOffset.MaxValue;
        }
        else if (result.RetryAt != null)
        {
            Journal.DeliveryResumeAt = result.RetryAt.Value > Now.AddSeconds(5) ? result.RetryAt.Value : Now.AddSeconds(5);
            foreach (var item in Journal.Queue) item.NextAttempt = result.RetryAt.Value > Now.AddSeconds(5) ? result.RetryAt.Value : Now.AddSeconds(5);
        }
        Persist();
    }
    public void SettingsChanged(MaintenanceConfig config)
    {
        Prune(config);
        Journal.DeliveryPaused = false; Journal.DeliveryResumeAt = default;
        foreach (var pending in Journal.Queue) pending.NextAttempt = Now;
        Persist();
    }
    private void Persist() => save(Journal);
}
