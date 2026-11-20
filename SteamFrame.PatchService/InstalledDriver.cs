using System.Text.RegularExpressions;
using SteamFrame.PatchMaintenance;
using SteamLinkVRSettings.Frame;

namespace SteamFrame.PatchService;

internal sealed class InstalledDriver(string root) : IMaintenanceDriver
{
    private readonly BitratePatcher patcher = new();
    private string Driver => MaintenancePaths.Driver(root);
    internal static async Task ProbeAccessAsync(string root, CancellationToken ct)
    {
        var driver = MaintenancePaths.Driver(root);
        MaintenancePaths.RejectReparseAncestors(driver);
        await using (var readable = new FileStream(driver, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
        {
            var firstByte = new byte[1];
            if (await readable.ReadAsync(firstByte, ct) != 1) throw new InvalidDataException("The configured driver is empty.");
        }
        var probe = Path.Combine(Path.GetDirectoryName(driver)!, ".frame-service-permission-" + Guid.NewGuid().ToString("N") + ".tmp");
        await using var writable = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough | FileOptions.DeleteOnClose);
        await writable.WriteAsync(new byte[] { 1 }, ct); writable.Flush(true);
    }
    public async Task<BitratePatchStatus> InspectAsync(CancellationToken ct)
    {
        try
        {
            MaintenancePaths.RejectReparseAncestors(Driver);
            return await patcher.InspectAsync(Driver, ct);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new(Driver, BitratePatcher.GetBackupPath(Driver), BitratePatchState.Unavailable, BitrateBackupState.Missing,
                null, null, true, "The configured driver path cannot be safely inspected.");
        }
    }
    public Task<BitratePatchStatus> ApplyAsync(string expectedSourceSha256, CancellationToken ct)
    {
        MaintenancePaths.RejectReparseAncestors(Driver);
        return patcher.ApplyExpectedAsync(Driver, expectedSourceSha256, ct);
    }
    public bool UpdateInProgress
    {
        get
        {
            try
            {
                var steamapps = Directory.GetParent(root)?.Parent?.FullName;
                if (steamapps == null) return false;
                if (Directory.Exists(Path.Combine(steamapps, "downloading", "250820"))) return true;
                var manifest = Path.Combine(steamapps, "appmanifest_250820.acf");
                if (!File.Exists(manifest)) return false;
                using var stream = File.OpenRead(manifest);
                if (stream.Length > 1_048_576) return true;
                using var reader = new StreamReader(stream);
                var match = Regex.Match(reader.ReadToEnd(), "\"StateFlags\"\\s+\"([0-9]+)\"", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                // Fully installed is 4. Other flags include update/download/staging activity.
                return match.Success && match.Groups[1].Value != "4";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RegexMatchTimeoutException) { return true; }
        }
    }
}
