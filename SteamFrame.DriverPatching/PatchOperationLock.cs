using System.Security.AccessControl;
using System.Security.Principal;

namespace SteamLinkVRSettings.Frame;

/// <summary>Serializes every app/service writer. Mutex ownership stays on one dedicated pool thread.</summary>
public static class PatchOperationLock
{
    internal const string Name = @"Global\SteamFrame.AdvancedOptions.DriverPatch.v1";

    public static Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken ct = default) => Task.Run(() =>
    {
        var security = new MutexSecurity();
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid,
            WellKnownSidType.LocalServiceSid, WellKnownSidType.AuthenticatedUserSid })
            security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(sid, null),
                MutexRights.Synchronize | MutexRights.Modify, AccessControlType.Allow));
        using var mutex = Open(security);
        bool owned = false;
        try
        {
            while (!owned)
            {
                ct.ThrowIfCancellationRequested();
                try { owned = mutex.WaitOne(200); }
                catch (AbandonedMutexException) { owned = true; } // The operation always re-inspects disk.
            }
            ct.ThrowIfCancellationRequested();
            return operation().GetAwaiter().GetResult();
        }
        finally { if (owned) mutex.ReleaseMutex(); }
    }, ct);

    private static Mutex Open(MutexSecurity security)
    {
        try { return MutexAcl.OpenExisting(Name, MutexRights.Synchronize | MutexRights.Modify); }
        catch (WaitHandleCannotBeOpenedException)
        {
            try { return MutexAcl.Create(false, Name, out _, security); }
            catch (UnauthorizedAccessException) { return MutexAcl.OpenExisting(Name, MutexRights.Synchronize | MutexRights.Modify); }
        }
    }
}
