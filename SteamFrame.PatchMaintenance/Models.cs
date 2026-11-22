using SteamLinkVRSettings.Frame;

namespace SteamFrame.PatchMaintenance;

public static class MaintenancePaths
{
    public const string ServiceName = "SteamFramePatchService";
    public const string PipeName = "SteamFrame.AdvancedOptions.PatchMaintenance.v1";
    public static string Data => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SteamFrame Advanced Options", "PatchService");
    public static string Install => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SteamFrame Advanced Options", "PatchService");
    public static string Driver(string root) => Path.Combine(Path.GetFullPath(root), "drivers", "vrlink", "bin", "win64", "driver_vrlink.dll");

    public static void RejectReparseAncestors(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Symbolic links and reparse points are not allowed for maintenance paths.");
    }
}

public sealed class MaintenanceConfig
{
    public int SchemaVersion { get; set; } = 1;
    public string SteamVrRoot { get; set; } = "";
    public string OwnerSid { get; set; } = "";
    public bool Enabled { get; set; }
    public bool NotifySuccess { get; set; } = true;
    public bool NotifyFailure { get; set; } = true;
    public string Device { get; set; } = "";
    public void Validate()
    {
        if (SchemaVersion != 1 || string.IsNullOrWhiteSpace(SteamVrRoot) || !Path.IsPathFullyQualified(SteamVrRoot) || SteamVrRoot.StartsWith(@"\\", StringComparison.Ordinal)
            || string.IsNullOrEmpty(OwnerSid) || !OwnerSid.StartsWith("S-1-", StringComparison.Ordinal) || Device == null || Device.Length > 25
            || Device.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
            throw new InvalidDataException("Invalid maintenance configuration.");
        _ = new System.Security.Principal.SecurityIdentifier(OwnerSid);
        MaintenancePaths.RejectReparseAncestors(MaintenancePaths.Driver(SteamVrRoot));
    }
}

public sealed record PushoverCredentials(string Token, string User)
{
    public void Validate()
    {
        if (Token == null || User == null || Token.Length != 30 || User.Length != 30 || !Token.All(char.IsAsciiLetterOrDigit) || !User.All(char.IsAsciiLetterOrDigit))
            throw new InvalidDataException("Pushover token and user/group key must each contain 30 letters or digits.");
    }
}

public sealed record PendingNotification(string Id, bool Success, string Message, DateTimeOffset At)
{
    public int Attempts { get; set; }
    public DateTimeOffset NextAttempt { get; set; }
}

public sealed record MaintenanceJournal
{
    public int SchemaVersion { get; set; } = 1;
    public string Identity { get; set; } = "";
    public string IncidentId { get; set; } = "";
    public DateTimeOffset ObservedAt { get; set; }
    public string? BlockedIdentity { get; set; }
    public string? ReportedFailure { get; set; }
    public string? InFlightSource { get; set; }
    public DateTimeOffset? LastSuccess { get; set; }
    public string State { get; set; } = "Waiting for first check";
    public string NotificationStatus { get; set; } = "No notifications sent";
    public bool DeliveryPaused { get; set; }
    public DateTimeOffset DeliveryResumeAt { get; set; }
    public DateTimeOffset? LastCheck { get; set; }
    public List<PendingNotification> Queue { get; set; } = [];
}

public sealed record MaintenanceSnapshot(int ProtocolVersion, string Version, bool Enabled, bool NotifySuccess,
    bool NotifyFailure, string Device, bool CredentialsConfigured, string DriverPath, MaintenanceJournal Status);

// Credentials never appear in replies or in command-line arguments.
public sealed record ControlRequest(string Action, int ProtocolVersion = 1, bool NotifySuccess = true,
    bool NotifyFailure = true, string Device = "", PushoverCredentials? Credentials = null);
public sealed record ControlReply(bool Ok, string Message, MaintenanceSnapshot? Snapshot = null);

public interface IMaintenanceDriver
{
    Task<BitratePatchStatus> InspectAsync(CancellationToken ct);
    Task<BitratePatchStatus> ApplyAsync(string expectedSourceSha256, CancellationToken ct);
    bool UpdateInProgress { get; }
}

public sealed record DeliveryResult(bool Accepted, bool PermanentFailure, string Description, DateTimeOffset? RetryAt = null);
public interface INotificationSender
{
    Task<DeliveryResult> SendAsync(PushoverCredentials credentials, string device, string message, DateTimeOffset at, CancellationToken ct);
}
