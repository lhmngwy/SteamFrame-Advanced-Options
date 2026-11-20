using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using SteamFrame.PatchMaintenance;

namespace SteamFrame.PatchService;

/// <summary>Only this short-lived elevated path installs or replaces the protected service payload.</summary>
internal static class ServiceInstaller
{
    private sealed class Progress { public string Stage { get; set; } = "Acquiring the installer lock"; }
    public static Task RunAsync(string[] args) => Task.Run(() =>
    {
        var progress = new Progress();
        try
        {
            var security = new MutexSecurity();
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                security.AddAccessRule(new(new SecurityIdentifier(sid, null), MutexRights.FullControl, AccessControlType.Allow));
            using var mutex = MutexAcl.Create(false, @"Global\SteamFrame.AdvancedOptions.PatchServiceInstallation.v1", out _, security);
            bool owned = false;
            try
            {
                try { owned = mutex.WaitOne(TimeSpan.FromSeconds(60)); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned) throw new IOException("Another maintenance installer is still running.");
                RunCoreAsync(args, progress).GetAwaiter().GetResult();
            }
            finally { if (owned) mutex.ReleaseMutex(); }
        }
        catch (InstallationFailureException) { throw; }
        catch (Exception error) { throw new InstallationFailureException(progress.Stage, error); }
    });

    private static async Task RunCoreAsync(string[] args, Progress progress)
    {
        progress.Stage = "Checking administrator privileges";
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Administrator approval is required.");
        MaintenancePaths.RejectReparseAncestors(MaintenancePaths.Install);
        MaintenancePaths.RejectReparseAncestors(MaintenancePaths.Data);
        progress.Stage = "Reading maintenance configuration";
        var store = new MaintenanceStore(MaintenancePaths.Data);
        MaintenanceConfig? previous;
        try { previous = store.Read<MaintenanceConfig>("config.json"); }
        catch (Exception ex) when (args[0] == "uninstall" && ex is System.Text.Json.JsonException or InvalidDataException)
        { previous = null; } // Corrupt state must not make a stopped service impossible to uninstall.
        if (args[0] == "uninstall")
        {
            if (args.Length != 1) throw new ArgumentException("Invalid uninstall request.");
            progress.Stage = "Stopping the service"; Stop();
            if (previous != null)
            {
                previous.Enabled = false; store.Write("config.json", previous);
                try { previous.Validate(); RevokeDriverAccess(previous.SteamVrRoot); }
                catch (Exception ex) when (ex is InvalidDataException or ArgumentException) { }
            }
            else store.Remove("config.json");
            progress.Stage = "Removing the service registration";
            if (Exists()) await ScAsync("delete", MaintenancePaths.ServiceName);
            progress.Stage = "Removing service credentials and payload";
            store.RemoveCredentials();
            store.Write("journal.json", new MaintenanceJournal { State = "Service uninstalled" });
            if (Directory.Exists(MaintenancePaths.Install)) DeletePayload(MaintenancePaths.Install);
            return;
        }
        progress.Stage = "Validating the SteamVR installation";
        if (args.Length != 3) throw new ArgumentException("Install requires the SteamVR root and owner SID.");
        var config = previous == null ? new MaintenanceConfig() : new MaintenanceConfig
        {
            NotifySuccess = previous.NotifySuccess, NotifyFailure = previous.NotifyFailure, Device = previous.Device
        };
        config.SteamVrRoot = Path.GetFullPath(args[1]); config.OwnerSid = args[2]; config.Enabled = true; config.Validate();
        if (!File.Exists(MaintenancePaths.Driver(config.SteamVrRoot))) throw new FileNotFoundException("The selected SteamVR driver does not exist.");
        // The caller chooses a real local user; never grant anonymous/network/world pipe access.
        var owner = new SecurityIdentifier(config.OwnerSid);
        if (!owner.IsAccountSid()) throw new InvalidDataException("A local Windows account SID is required.");
        var source = Path.GetFullPath(AppContext.BaseDirectory);
        var previousJournal = store.Read<MaintenanceJournal>("journal.json");
        if (source.TrimEnd(Path.DirectorySeparatorChar).Equals(MaintenancePaths.Install, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Run the installer from the app's bundled PatchService folder.");
        MaintenancePaths.RejectReparseAncestors(source);
        var stage = MaintenancePaths.Install + ".stage-" + Guid.NewGuid().ToString("N");
        var old = MaintenancePaths.Install + ".previous-" + Guid.NewGuid().ToString("N");
        bool existed = Exists(), movedOld = false, installedNew = false;
        try
        {
            progress.Stage = "Staging the protected service payload";
            ProtectDirectory(Path.GetDirectoryName(MaintenancePaths.Install)!, null, false);
            ProtectDirectory(stage, null, false);
            CopyPayload(source, stage);
            progress.Stage = "Stopping the previous service"; Stop();
            progress.Stage = "Installing the protected service payload";
            if (Directory.Exists(MaintenancePaths.Install)) { Directory.Move(MaintenancePaths.Install, old); movedOld = true; }
            Directory.Move(stage, MaintenancePaths.Install); installedNew = true;
            progress.Stage = "Registering the Windows service";
            if (!existed)
                await ScAsync("create", MaintenancePaths.ServiceName, "binPath=", '"' + Path.Combine(MaintenancePaths.Install, "SteamFrame.PatchService.exe") + '"',
                    "start=", "auto", "obj=", @"NT SERVICE\" + MaintenancePaths.ServiceName, "DisplayName=", "SteamFrame automatic driver patch maintenance");
            else
                await ScAsync("config", MaintenancePaths.ServiceName, "binPath=", '"' + Path.Combine(MaintenancePaths.Install, "SteamFrame.PatchService.exe") + '"', "start=", "auto", "obj=", @"NT SERVICE\" + MaintenancePaths.ServiceName);
            progress.Stage = "Configuring the dedicated service identity";
            await ScAsync("sidtype", MaintenancePaths.ServiceName, "unrestricted");
            var serviceSid = ServiceSid();
            progress.Stage = "Securing service files and data";
            ProtectDirectory(MaintenancePaths.Install, serviceSid, false);
            ProtectDirectory(Path.GetDirectoryName(MaintenancePaths.Data)!, null, false);
            ProtectDirectory(MaintenancePaths.Data, serviceSid, true);
            progress.Stage = "Granting access to the driver directory";
            GrantDriverAccess(config.SteamVrRoot, serviceSid);
            progress.Stage = "Saving maintenance configuration";
            store.Write("config.json", config);
            // A new watched installation must not inherit the previous target's pending journal or notifications.
            if (previous != null && !string.Equals(previous.SteamVrRoot, config.SteamVrRoot, StringComparison.OrdinalIgnoreCase))
                store.Write("journal.json", new MaintenanceJournal());
            progress.Stage = "Configuring service crash recovery";
            ServiceRecovery.Configure();
            progress.Stage = "Registering the Windows event log source";
            if (!EventLog.SourceExists(MaintenancePaths.ServiceName))
                EventLog.CreateEventSource(MaintenancePaths.ServiceName, "Application");
            progress.Stage = "Starting the Windows service";
            using (var controller = new ServiceController(MaintenancePaths.ServiceName))
            { controller.Start(); controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30)); }
            progress.Stage = "Verifying the service identity and driver access";
            ControlReply? health = null;
            Exception? healthError = null;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try { health = await MaintenanceClient.SendAsync(new("health")); if (health.Ok) break; }
                catch (Exception ex) when (ex is IOException or System.TimeoutException or OperationCanceledException) { healthError = ex; }
                await Task.Delay(1000);
            }
            if (health?.Ok != true) throw new IOException("The installed service did not pass its health check. "
                + (health?.Message ?? healthError?.Message ?? "No service reply was received."), healthError);
            // A scanner upgrade must reconsider a previously unsupported hash, not
            // leave the old journal's block in effect until a separate UI click.
            progress.Stage = "Scheduling driver revalidation";
            var retry = await MaintenanceClient.SendAsync(new("check"));
            if (!retry.Ok) throw new IOException("The updated service could not schedule driver revalidation: " + retry.Message);
            progress.Stage = "Removing previous installation permissions";
            if (previous != null && !string.Equals(previous.SteamVrRoot, config.SteamVrRoot, StringComparison.OrdinalIgnoreCase)) RevokeDriverAccess(previous.SteamVrRoot);
            if (movedOld)
            {
                // Cleanup is after commit: an antivirus lock must not turn a healthy upgrade
                // into rollback using an old directory that cleanup may already have partly removed.
                try { DeletePayload(old); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception error)
        {
            var failedStage = progress.Stage;
            try
            {
                Stop();
                if (installedNew && Directory.Exists(MaintenancePaths.Install)) DeletePayload(MaintenancePaths.Install);
                if (movedOld) Directory.Move(old, MaintenancePaths.Install);
                if (previous != null) store.Write("config.json", previous);
                else store.Remove("config.json");
                if (previousJournal != null) store.Write("journal.json", previousJournal);
                else store.Remove("journal.json");
                if (previous != null && !string.Equals(previous.SteamVrRoot, config.SteamVrRoot, StringComparison.OrdinalIgnoreCase)) RevokeDriverAccess(config.SteamVrRoot);
                if (!existed && Exists()) { RevokeDriverAccess(config.SteamVrRoot); await ScAsync("delete", MaintenancePaths.ServiceName); }
                if (existed)
                {
                    using var controller = new ServiceController(MaintenancePaths.ServiceName);
                    controller.Start();
                }
            }
            catch (Exception rollbackError) { throw new InstallationFailureException(failedStage, error, true, rollbackError); }
            throw new InstallationFailureException(failedStage, error, true);
        }
        finally
        {
            if (Directory.Exists(stage))
            {
                try { DeletePayload(stage); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static bool Exists()
    {
        using var controller = new ServiceController(MaintenancePaths.ServiceName);
        try { _ = controller.Status; return true; }
        catch (InvalidOperationException ex) when (ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 1060 }) { return false; }
    }
    private static void Stop()
    {
        if (!Exists()) return;
        using var controller = new ServiceController(MaintenancePaths.ServiceName);
        if (controller.Status == ServiceControllerStatus.Stopped) return;
        if (controller.Status != ServiceControllerStatus.StopPending) controller.Stop();
        controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
    }
    private static async Task ScAsync(params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start Windows service management.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new System.ComponentModel.Win32Exception(process.ExitCode,
            "Windows service management failed: " + new System.ComponentModel.Win32Exception(process.ExitCode).Message);
    }
    private static SecurityIdentifier ServiceSid() => (SecurityIdentifier)new NTAccount(@"NT SERVICE\" + MaintenancePaths.ServiceName).Translate(typeof(SecurityIdentifier));
    private static void ProtectDirectory(string path, SecurityIdentifier? serviceSid, bool writable)
    {
        MaintenancePaths.RejectReparseAncestors(path);
        Directory.CreateDirectory(path);
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            acl.AddAccessRule(new(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (serviceSid != null) acl.AddAccessRule(new(serviceSid, writable ? FileSystemRights.Modify : FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (!writable) acl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
        if (writable)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
            {
                MaintenancePaths.RejectReparseAncestors(entry);
                if (Directory.Exists(entry)) new DirectoryInfo(entry).SetAccessControl(acl);
                else
                {
                    var fileAcl = new FileSecurity(); fileAcl.SetAccessRuleProtection(true, false);
                    fileAcl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
                    foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                        fileAcl.AddAccessRule(new(rule.IdentityReference, rule.FileSystemRights, rule.AccessControlType));
                    new FileInfo(entry).SetAccessControl(fileAcl);
                }
            }
        }
    }
    private static void GrantDriverAccess(string root, SecurityIdentifier sid)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(MaintenancePaths.Driver(root))!);
        MaintenancePaths.RejectReparseAncestors(directory.FullName);
        var acl = directory.GetAccessControl();
        acl.AddAccessRule(new(sid, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(acl);
    }
    private static void RevokeDriverAccess(string root)
    {
        if (!Exists()) return;
        var directory = new DirectoryInfo(Path.GetDirectoryName(MaintenancePaths.Driver(root))!);
        if (!directory.Exists) return;
        MaintenancePaths.RejectReparseAncestors(directory.FullName);
        var acl = directory.GetAccessControl(); acl.PurgeAccessRules(ServiceSid()); directory.SetAccessControl(acl);
    }
    private static void CopyPayload(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            MaintenancePaths.RejectReparseAncestors(file);
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, false);
        }
        if (!File.Exists(Path.Combine(destination, "SteamFrame.PatchService.exe"))) throw new IOException("Service payload is incomplete.");
    }
    private static void DeletePayload(string path)
    {
        // Only the fixed installation or our GUID-named staging/rollback directories may be removed.
        if (path != MaintenancePaths.Install && !path.StartsWith(MaintenancePaths.Install + ".stage-", StringComparison.Ordinal)
            && !path.StartsWith(MaintenancePaths.Install + ".previous-", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe cleanup path.");
        MaintenancePaths.RejectReparseAncestors(path);
        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories)) MaintenancePaths.RejectReparseAncestors(entry);
        Directory.Delete(path, true);
    }
}
