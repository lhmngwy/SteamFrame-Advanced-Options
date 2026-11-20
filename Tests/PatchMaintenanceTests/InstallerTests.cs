using System.ComponentModel;
using SteamFrame.PatchMaintenance;
using SteamFrame.PatchService;

namespace SteamLinkVRSettings.Tests;

public sealed class InstallerTests
{
    [Fact]
    public void Restart_recovery_requests_start_and_change_configuration_access()
    {
        // ChangeServiceConfig2(SC_ACTION_RESTART) requires BOTH rights. Being admin
        // does not add rights omitted from the OpenService access mask.
        Assert.Equal(0x0012u, ServiceRecovery.RequiredServiceAccess);
    }

    [Fact]
    public void Failure_diagnostics_preserve_native_cause_even_when_rollback_also_fails()
    {
        var failure = new InstallationFailureException("Configuring service crash recovery", new Win32Exception(5), true, new IOException("Recovery file is locked."));
        var result = InstallerEntryPoint.DescribeFailure(failure);
        Assert.False(result.Success);
        Assert.Equal(5, result.WindowsError);
        Assert.Equal("Configuring service crash recovery", result.Stage);
        Assert.Equal(new Win32Exception(5).Message, result.Message);
        Assert.True(result.RollbackAttempted);
        Assert.Contains("Recovery file is locked", result.RollbackError);
        Assert.Contains("Windows error 5", result.FailureDescription);
        Assert.Contains("Rollback also failed", result.FailureDescription);
    }

    [Fact]
    public void Errors_before_any_transaction_do_not_claim_a_rollback()
    {
        var result = InstallerEntryPoint.DescribeFailure(new InstallationFailureException("Checking administrator privileges", new UnauthorizedAccessException("Administrator approval is required.")));
        Assert.False(result.RollbackAttempted);
        Assert.Equal(5, result.WindowsError);
        Assert.DoesNotContain("rolled back", result.FailureDescription);
        Assert.Contains("administrator", result.FailureDescription);
    }

    [Theory]
    [InlineData("")]
    [InlineData("another-app-pipe")]
    [InlineData("\\\\remote\\pipe\\arbitrary")]
    [InlineData("SteamFrame.AdvancedOptions.PatchInstallerResult.not-a-guid")]
    public void Elevated_reply_can_only_use_a_local_bounded_result_pipe_name(string name)
    { Assert.Throws<ArgumentException>(() => InstallerResultListener.ValidatePipeName(name)); }

    [Fact]
    public void Invocation_keeps_paths_with_spaces_and_separates_the_result_channel()
    {
        var name = InstallerResultListener.Prefix + Guid.NewGuid().ToString("N");
        var args = new[] { "install", @"D:\Steam Library\steamapps\common\SteamVR", "S-1-5-21-1-2-3-1000", "--result-pipe", name };
        var invocation = InstallerInvocation.Parse(args);
        Assert.Equal(args[..3], invocation.Arguments);
        Assert.Equal(name, invocation.ResultPipe);
        Assert.Null(InstallerInvocation.Parse(args[..3]).ResultPipe);
        Assert.Equal(new[] { "uninstall" }, InstallerInvocation.Parse(new[] { "uninstall", "--result-pipe", name }).Arguments);
    }

    [Fact]
    public void Unexpected_installer_arguments_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => InstallerInvocation.Parse([]));
        Assert.Throws<ArgumentException>(() => InstallerInvocation.Parse(["run-command", "arbitrary.exe"]));
        Assert.Throws<ArgumentException>(() => InstallerInvocation.Parse(["install", @"C:\SteamVR"]));
        Assert.Throws<ArgumentException>(() => InstallerInvocation.Parse(["uninstall", "extra"]));
    }

    [Fact]
    public async Task Installer_error_is_returned_over_the_same_private_channel_the_UI_uses()
    {
        using var listener = new InstallerResultListener();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var received = listener.ReceiveAsync(timeout.Token);
        var exit = await InstallerEntryPoint.RunAsync(["install", @"C:\SteamVR", "S-1-5-21-1-2-3-1000", "--result-pipe", listener.PipeName],
            _ => Task.FromException(new InstallationFailureException("Configuring service crash recovery", new Win32Exception(5), true)));
        var result = await received;
        Assert.Equal(1, exit);
        Assert.Equal(5, result.WindowsError);
        Assert.Contains("Configuring service crash recovery", result.FailureDescription);
        Assert.Contains("Changes were rolled back", result.FailureDescription);
    }

    [Fact]
    public async Task Installer_success_is_reported_without_creating_a_service_in_tests()
    {
        using var listener = new InstallerResultListener();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var received = listener.ReceiveAsync(timeout.Token);
        var exit = await InstallerEntryPoint.RunAsync(["uninstall", "--result-pipe", listener.PipeName], _ => Task.CompletedTask);
        Assert.Equal(0, exit); Assert.True((await received).Success);
    }

    [Fact]
    public async Task Closing_UI_or_declining_UAC_cancels_a_listener_that_never_connects()
    {
        using var listener = new InstallerResultListener();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var waiting = listener.ReceiveAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task Published_helper_reports_its_real_unelevated_failure_without_installing_anything()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        Assert.SkipWhen(new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator),
            "This smoke test intentionally runs without elevation and must never reach installation.");
        DirectoryInfo? root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "SteamLinkVRSettings.sln"))) root = root.Parent;
        var executable = Path.Combine(root?.FullName ?? "", "artifacts", "PatchService", "SteamFrame.PatchService.exe");
        Assert.SkipWhen(!File.Exists(executable), "Run Build.ps1 to stage the self-contained helper for this smoke test.");
        using var listener = new InstallerResultListener();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var received = listener.ReceiveAsync(timeout.Token);
        var start = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "install", Path.Combine(Path.GetTempPath(), "SteamFrame-nonexistent-" + Guid.NewGuid().ToString("N")),
            identity.User!.Value, "--result-pipe", listener.PipeName }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var result = await received;
            Assert.Equal(1, process.ExitCode); Assert.False(result.Success);
            Assert.Contains(result.Stage, new[] { "Acquiring the installer lock", "Checking administrator privileges" });
            Assert.False(result.RollbackAttempted);
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            timeout.Cancel();
            try { await received; } catch (OperationCanceledException) { }
        }
    }
}
