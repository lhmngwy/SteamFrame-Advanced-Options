using System.ComponentModel;
using SteamFrame.PatchMaintenance;

namespace SteamFrame.PatchService;

internal sealed class InstallationFailureException(string stage, Exception error, bool rollbackAttempted = false, Exception? rollbackError = null)
    : Exception($"Installer failed at {stage}.", error)
{
    public string Stage { get; } = stage;
    public bool RollbackAttempted { get; } = rollbackAttempted;
    public Exception? RollbackError { get; } = rollbackError;
}

internal static class InstallerEntryPoint
{
    public static Task<int> RunAsync(string[] arguments) => RunAsync(arguments, ServiceInstaller.RunAsync);

    internal static async Task<int> RunAsync(string[] arguments, Func<string[], Task> runInstaller)
    {
        InstallerInvocation invocation;
        try { invocation = InstallerInvocation.Parse(arguments); }
        catch (ArgumentException) { return 2; }
        InstallerResult result;
        try
        {
            await runInstaller(invocation.Arguments);
            result = new(true, "Completed", "Service operation completed.");
        }
        catch (Exception error) { result = DescribeFailure(error); }

        if (invocation.ResultPipe != null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await InstallerResultListener.SendAsync(invocation.ResultPipe, result, timeout.Token); }
            catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException)
            { /* A closed UI does not change the outcome of the installation. */ }
        }
        return result.Success ? 0 : 1;
    }

    internal static InstallerResult DescribeFailure(Exception error)
    {
        var failure = error as InstallationFailureException;
        var primary = failure?.InnerException ?? error;
        int? windowsError = null;
        for (var current = primary; current != null; current = current.InnerException)
        {
            if (current is Win32Exception native) { windowsError = native.NativeErrorCode; break; }
            if ((unchecked((uint)current.HResult) & 0xFFFF0000u) == 0x80070000u)
            { windowsError = current.HResult & 0xFFFF; break; }
        }
        return new(false, failure?.Stage ?? "Starting the installer", Limit(primary.Message), windowsError,
            failure?.RollbackAttempted == true, failure?.RollbackError is { } rollback ? Limit(rollback.Message) : null);
    }
    private static string Limit(string message) => message.Length > 2048 ? message[..2048] : message;
}
