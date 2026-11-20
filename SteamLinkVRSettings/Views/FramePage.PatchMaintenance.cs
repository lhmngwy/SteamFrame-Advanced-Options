using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SteamFrame.PatchMaintenance;
using System.Diagnostics;
using System.Security.Principal;

namespace SteamLinkVRSettings.Views;

public sealed partial class FramePage
{
    private bool _maintenancePromptOpen;
    private async Task RefreshMaintenanceAsync()
    {
        try
        {
            var reply = await MaintenanceClient.SendAsync(new("status"));
            if (!reply.Ok || reply.Snapshot == null) { MaintenanceStatus.Text = reply.Message; return; }
            RenderMaintenance(reply.Snapshot);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            MaintenanceStatus.Text = "Automatic maintenance is not installed or is not responding. Install/update the service, or uninstall it to recover. No background patching is enabled by opening this app.";
        }
    }
    private void RenderMaintenance(MaintenanceSnapshot snapshot)
    {
        MaintenanceStatus.Text = $"Service {snapshot.Version}: {(snapshot.Enabled ? "enabled" : "disabled")}\n{snapshot.Status.State}\nWatched driver: {snapshot.DriverPath}\nLast check: {snapshot.Status.LastCheck?.ToLocalTime().ToString("g") ?? "not yet"}\nLast patch: {snapshot.Status.LastSuccess?.ToLocalTime().ToString("g") ?? "none"}\nNotifications: {snapshot.Status.NotificationStatus}\nPushover credentials: {(snapshot.CredentialsConfigured ? "configured (hidden)" : "not configured")}";
        MaintenanceSuccessInput.IsChecked = snapshot.NotifySuccess;
        MaintenanceFailureInput.IsChecked = snapshot.NotifyFailure;
        MaintenanceDeviceInput.Text = snapshot.Device;
    }
    private async Task<string> MaintenanceRequestAsync(ControlRequest request)
    {
        var reply = await MaintenanceClient.SendAsync(request);
        if (!reply.Ok) throw new InvalidOperationException(reply.Message);
        if (reply.Snapshot != null) RenderMaintenance(reply.Snapshot);
        return reply.Message;
    }
    private async Task DisableMaintenanceBeforeRestoreAsync()
    {
        var snapshot = await MaintenanceClient.DisableBeforeRestoreAsync();
        if (snapshot != null) RenderMaintenance(snapshot);
    }
    private async Task<bool> ConfirmMaintenanceAsync(string title, string text, string action)
    {
        if (_maintenancePromptOpen || _actionPending) return false;
        _maintenancePromptOpen = true;
        try
        {
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = action, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally { _maintenancePromptOpen = false; }
    }
    private async Task RunMaintenanceInstallerAsync(string action)
    {
        var executable = System.IO.Path.Combine(AppContext.BaseDirectory, "PatchService", "SteamFrame.PatchService.exe");
        if (!System.IO.File.Exists(executable)) throw new InvalidOperationException("The bundled service payload is missing. Use a complete current release or run Build.ps1.");
        using var results = new InstallerResultListener();
        using var resultLifetime = new CancellationTokenSource();
        var receive = results.ReceiveAsync(resultLifetime.Token);
        var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WorkingDirectory = System.IO.Path.GetDirectoryName(executable)! };
        start.ArgumentList.Add(action);
        if (action == "install")
        {
            start.ArgumentList.Add(_paths.SteamVrRoot);
            using var identity = WindowsIdentity.GetCurrent();
            start.ArgumentList.Add(identity.User!.Value);
        }
        start.ArgumentList.Add("--result-pipe");
        start.ArgumentList.Add(results.PipeName);
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("Could not start the service installer.");
            await process.WaitForExitAsync();
            InstallerResult? result = null;
            if (await Task.WhenAny(receive, Task.Delay(TimeSpan.FromSeconds(3), resultLifetime.Token)) == receive)
                result = await receive;
            if (process.ExitCode != 0 || result?.Success == false)
                throw new InvalidOperationException(result?.FailureDescription
                    ?? $"The service installer exited with code {process.ExitCode} without returning diagnostics. Update the entire app folder, including PatchService, and retry.");
            if (action == "install")
            {
                var health = await MaintenanceClient.SendAsync(new("status"));
                if (!health.Ok || health.Snapshot == null) throw new InvalidOperationException("The installer completed, but the service did not return a valid status: " + health.Message);
                RenderMaintenance(health.Snapshot);
            }
            else await RefreshMaintenanceAsync();
        }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223)
        { throw new InvalidOperationException("Service operation canceled: administrator approval was declined. The installer was not started."); }
        finally
        {
            resultLifetime.Cancel();
            try { await receive; }
            catch (Exception error) when (error is OperationCanceledException or IOException or InvalidDataException or System.Text.Json.JsonException) { }
        }
    }
    private async void InstallMaintenance_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmMaintenanceAsync("Enable automatic patch maintenance?",
            $"Install or update a Windows service that starts before sign-in and watches:\n{_paths.Driver}\n\nIt reapplies the verified 500 Mbps/HEVC 6.2 patch after SteamVR updates. It waits for SteamVR to close and never interrupts a running session. Service files are installed in Program Files. Administrator approval is required. This also updates the watched installation and enables maintenance.", "Install and enable")) return;
        await ActionAsync(async () => { await RunMaintenanceInstallerAsync("install"); return "Automatic patch maintenance installed and enabled."; });
    }
    private async void UninstallMaintenance_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmMaintenanceAsync("Uninstall automatic patch maintenance?", "Stop and remove the service and its saved Pushover credentials. The current driver, original backups and patch receipts will not be removed or restored.", "Uninstall")) return;
        await ActionAsync(async () => { await RunMaintenanceInstallerAsync("uninstall"); return "Maintenance service removed. Driver and backups were left unchanged."; });
    }
    private async void EnableMaintenance_Click(object sender, RoutedEventArgs e) => await ActionAsync(() => MaintenanceRequestAsync(new("enable")));
    private async void DisableMaintenance_Click(object sender, RoutedEventArgs e) => await ActionAsync(() => MaintenanceRequestAsync(new("disable")));
    private async void CheckMaintenance_Click(object sender, RoutedEventArgs e) => await ActionAsync(() => MaintenanceRequestAsync(new("check")));
    private async void RefreshMaintenance_Click(object sender, RoutedEventArgs e) => await RefreshMaintenanceAsync();
    private async void TestMaintenanceNotification_Click(object sender, RoutedEventArgs e) => await ActionAsync(() => MaintenanceRequestAsync(new("test")));
    private async void RemoveMaintenanceCredentials_Click(object sender, RoutedEventArgs e) => await ActionAsync(() => MaintenanceRequestAsync(new("remove-credentials")));
    private async void SaveMaintenanceNotifications_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        PushoverCredentials? credentials = null;
        try
        {
            if (MaintenanceTokenInput.Password.Length > 0 || MaintenanceUserInput.Password.Length > 0)
            { credentials = new(MaintenanceTokenInput.Password.Trim(), MaintenanceUserInput.Password.Trim()); credentials.Validate(); }
            return await MaintenanceRequestAsync(new("settings", NotifySuccess: MaintenanceSuccessInput.IsChecked == true,
                NotifyFailure: MaintenanceFailureInput.IsChecked == true, Device: MaintenanceDeviceInput.Text.Trim(), Credentials: credentials));
        }
        finally { MaintenanceTokenInput.Password = ""; MaintenanceUserInput.Password = ""; }
    });
    private void CopyMaintenanceDetails_Click(object sender, RoutedEventArgs e)
    {
        // No local paths, user identities, credentials, or queued message contents are exported.
        _ = ActionAsync(async () =>
        {
            var reply = await MaintenanceClient.SendAsync(new("status"));
            var snapshot = reply.Snapshot ?? throw new InvalidOperationException(reply.Message);
            var text = $"Automatic patch maintenance {snapshot.Version}\nEnabled: {snapshot.Enabled}\nState: {snapshot.Status.State}\nLast check: {snapshot.Status.LastCheck:O}\nLast patch: {snapshot.Status.LastSuccess:O}\nIncident: {snapshot.Status.IncidentId}\nDriver identity: {snapshot.Status.Identity}\nNotification: {snapshot.Status.NotificationStatus}";
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage(); data.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            return "Sanitized maintenance details copied.";
        });
    }
}
