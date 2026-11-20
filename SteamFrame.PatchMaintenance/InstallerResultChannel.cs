using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SteamFrame.PatchMaintenance;

public sealed record InstallerResult(bool Success, string Stage, string Message, int? WindowsError = null,
    bool RollbackAttempted = false, string? RollbackError = null)
{
    public string FailureDescription => $"Service installation/removal failed at {Stage}: {Message}"
        + (WindowsError is int code ? $" (Windows error {code})." : "")
        + (RollbackAttempted ? RollbackError == null ? " Changes were rolled back." : $" Rollback also failed: {RollbackError}" : "");
}

public sealed record InstallerInvocation(string[] Arguments, string? ResultPipe)
{
    public static InstallerInvocation Parse(string[] arguments)
    {
        var core = arguments;
        string? pipe = null;
        if (arguments.Length >= 2 && arguments[^2] == "--result-pipe")
        {
            pipe = arguments[^1];
            InstallerResultListener.ValidatePipeName(pipe);
            core = arguments[..^2];
        }
        if (core.Length == 0 || !(core[0] == "install" && core.Length == 3 || core[0] == "uninstall" && core.Length == 1))
            throw new ArgumentException("Expected install <SteamVR root> <owner SID>, or uninstall.");
        return new(core, pipe);
    }
}

/// <summary>A private one-shot reply channel opened before UAC. No elevated arbitrary-path file writes.</summary>
public sealed class InstallerResultListener : IDisposable
{
    public const string Prefix = "SteamFrame.AdvancedOptions.PatchInstallerResult.";
    private readonly NamedPipeServerStream server;
    public string PipeName { get; } = Prefix + Guid.NewGuid().ToString("N");

    public InstallerResultListener()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        foreach (var sid in new[] { identity.User!, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        server = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 65536, 65536, security);
    }

    public async Task<InstallerResult> ReceiveAsync(CancellationToken ct)
    {
        await server.WaitForConnectionAsync(ct);
        return await ControlProtocol.ReadAsync<InstallerResult>(server, ct);
    }

    public static void ValidatePipeName(string name)
    {
        if (!name.StartsWith(Prefix, StringComparison.Ordinal) || name.Length != Prefix.Length + 32
            || !Guid.TryParseExact(name[Prefix.Length..], "N", out _))
            throw new ArgumentException("Invalid installer result pipe name.");
    }

    public static async Task SendAsync(string name, InstallerResult result, CancellationToken ct)
    {
        ValidatePipeName(name);
        // The caller must not be allowed to impersonate the elevated installer.
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await client.ConnectAsync(ct);
        await ControlProtocol.WriteAsync(client, result, ct);
    }

    public void Dispose() => server.Dispose();
}
