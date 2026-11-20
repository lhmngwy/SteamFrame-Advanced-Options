using SteamFrame.PatchMaintenance;

namespace SteamFrame.PatchService;

/// <summary>Events accelerate checks. Periodic reconciliation remains authoritative after overflow or directory replacement.</summary>
internal sealed class DriverChangeMonitor(string root, Func<long>? clock = null) : IDisposable
{
    private FileSystemWatcher? watcher;
    private long changed = (clock ?? (() => Environment.TickCount64))();
    private long rearmAt;
    private int invalid;
    private long Now => (clock ?? (() => Environment.TickCount64))();
    public bool Settled => Now - Interlocked.Read(ref changed) >= 10_000;
    public void EnsureWatching()
    {
        try
        {
            var directory = Path.GetDirectoryName(MaintenancePaths.Driver(root))!;
            MaintenancePaths.RejectReparseAncestors(directory);
            if (!Directory.Exists(directory)) { Dispose(); return; }
            if (watcher != null && Interlocked.Exchange(ref invalid, 0) == 0 && Now < rearmAt) return;
            Dispose();
            watcher = new(directory) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName };
            watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed; watcher.Renamed += Changed;
            watcher.Error += (_, _) => { Interlocked.Exchange(ref changed, Now); Interlocked.Exchange(ref invalid, 1); };
            watcher.EnableRaisingEvents = true;
            rearmAt = Now + 60_000;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { Dispose(); } // A disappearing/inaccessible directory is reconciled by polling, not a service crash.
    }
    private void Changed(object sender, FileSystemEventArgs args)
    {
        if (string.Equals(args.Name, "driver_vrlink.dll", StringComparison.OrdinalIgnoreCase)
            || args is RenamedEventArgs rename && string.Equals(rename.OldName, "driver_vrlink.dll", StringComparison.OrdinalIgnoreCase))
            Interlocked.Exchange(ref changed, Now);
    }
    public void Dispose() { var old = Interlocked.Exchange(ref watcher, null); old?.Dispose(); }
}
