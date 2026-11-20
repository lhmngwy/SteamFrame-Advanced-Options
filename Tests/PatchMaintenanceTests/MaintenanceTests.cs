using System.Net;
using System.Text;
using System.Text.Json;
using SteamFrame.PatchMaintenance;
using SteamLinkVRSettings.Frame;

namespace SteamLinkVRSettings.Tests;

public sealed class MaintenanceTests
{
    private sealed class Driver : IMaintenanceDriver
    {
        public BitratePatchStatus Status = Original();
        public int Writes;
        public bool UpdateInProgress { get; set; }
        public Exception? Error;
        public Func<BitratePatchStatus>? AfterError;
        public Task<BitratePatchStatus> InspectAsync(CancellationToken ct) => Task.FromResult(Status);
        public Task<BitratePatchStatus> ApplyAsync(string expectedSourceSha256, CancellationToken ct)
        {
            Assert.Equal(Status.Sha256, expectedSourceSha256);
            Writes++;
            if (Error != null) { if (AfterError != null) Status = AfterError(); throw Error; }
            Status = Status with { State = BitratePatchState.Patched, BackupState = BitrateBackupState.VerifiedOriginal, BackupSha256 = Status.Sha256, Sha256 = "patched" };
            return Task.FromResult(Status);
        }
    }
    private sealed class Sender(DeliveryResult result) : INotificationSender
    {
        public int Calls;
        public Task<DeliveryResult> SendAsync(PushoverCredentials credentials, string device, string message, DateTimeOffset at, CancellationToken ct)
        { Calls++; return Task.FromResult(result); }
    }
    private sealed class Rig
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-08T00:00:00Z");
        public Driver Driver = new();
        public MaintenanceConfig Config = new() { Enabled = true };
        public MaintenanceJournal State = new();
        public MaintenanceEngine Engine;
        public Rig() { Engine = Create(); }
        public MaintenanceEngine Create() => new(Driver, State, state => { State = JsonSerializer.Deserialize<MaintenanceJournal>(JsonSerializer.Serialize(state))!; }, () => Now);
        public async Task Tick(int seconds = 0) { Now = Now.AddSeconds(seconds); await Engine.TickAsync(Config, default); }
        public async Task Apply() { await Tick(); await Tick(11); }
    }
    private static BitratePatchStatus Original() => new("driver", "backup", BitratePatchState.Original, BitrateBackupState.Missing, "original", null, false, "compatible");
    private static PushoverCredentials Credentials => new(new string('a', 30), new string('b', 30));

    [Fact] public async Task Original_is_settled_patched_and_notified_once()
    {
        var rig = new Rig(); await rig.Tick(); Assert.Equal(0, rig.Driver.Writes);
        await rig.Tick(11); Assert.Equal(1, rig.Driver.Writes); Assert.True(Assert.Single(rig.State.Queue).Success);
        await rig.Tick(60); Assert.Equal(1, rig.Driver.Writes); Assert.Single(rig.State.Queue);
    }
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task Success_and_failure_switches_are_independent(bool success, bool failure)
    {
        var rig = new Rig(); rig.Config.NotifySuccess = success; rig.Config.NotifyFailure = failure;
        await rig.Apply(); Assert.Equal(success ? 1 : 0, rig.State.Queue.Count);
        rig.Driver.Status = Original() with { Sha256 = "unknown", State = BitratePatchState.Unsupported };
        await rig.Tick(); await rig.Tick(11);
        Assert.Equal((success ? 1 : 0) + (failure ? 1 : 0), rig.State.Queue.Count);
    }
    [Fact] public async Task Existing_patch_does_not_report_success()
    {
        var rig = new Rig(); rig.Driver.Status = Original() with { State = BitratePatchState.Patched };
        await rig.Tick(60); Assert.Empty(rig.State.Queue); Assert.Equal(0, rig.Driver.Writes);
    }
    [Fact] public async Task Running_vr_is_pending_not_failure_and_never_stopped()
    {
        var rig = new Rig(); rig.Driver.Status = Original() with { VrServerRunning = true };
        await rig.Tick(); await rig.Tick(3600); Assert.Empty(rig.State.Queue); Assert.Equal(0, rig.Driver.Writes);
        rig.Driver.Status = rig.Driver.Status with { VrServerRunning = false }; await rig.Tick(); Assert.Equal(1, rig.Driver.Writes);
    }
    [Fact] public async Task Active_update_never_patches()
    {
        var rig = new Rig(); rig.Driver.UpdateInProgress = true;
        await rig.Tick(); await rig.Tick(3600); Assert.Equal(0, rig.Driver.Writes); Assert.Empty(rig.State.Queue);
        rig.Driver.UpdateInProgress = false; await rig.Tick(); Assert.Equal(1, rig.Driver.Writes);
    }
    [Theory] [InlineData(BitratePatchState.Missing)] [InlineData(BitratePatchState.Unavailable)]
    public async Task Transient_unavailability_has_grace_and_eventually_recovers(BitratePatchState state)
    {
        var rig = new Rig(); rig.Driver.Status = Original() with { State = state, Sha256 = null };
        await rig.Tick(); await rig.Tick(11); Assert.Empty(rig.State.Queue);
        await rig.Tick(600); Assert.False(Assert.Single(rig.State.Queue).Success);
        await rig.Tick(600); Assert.Single(rig.State.Queue);
        rig.Driver.Status = Original(); await rig.Tick(); await rig.Tick(11); Assert.Equal(1, rig.Driver.Writes);
    }
    [Fact] public async Task Unsupported_failure_is_deduplicated_across_restart()
    {
        var rig = new Rig(); rig.Driver.Status = Original() with { State = BitratePatchState.Unsupported };
        await rig.Apply(); rig.Engine = rig.Create(); await rig.Tick(60);
        Assert.Single(rig.State.Queue); Assert.Equal(0, rig.Driver.Writes);
    }
    [Fact] public async Task Scanner_update_revalidation_reconsiders_the_same_previously_blocked_source()
    {
        var rig = new Rig(); rig.Driver.Status = Original() with { State = BitratePatchState.Unsupported };
        await rig.Apply(); Assert.NotNull(rig.State.BlockedIdentity);
        var failureId = Assert.Single(rig.State.Queue).Id;
        rig.Engine = rig.Create(); rig.Driver.Status = Original(); // Same hash, now recognized by the new scanner.
        await rig.Tick(30); Assert.Equal(0, rig.Driver.Writes);
        rig.Engine.RequestCheck();
        await rig.Tick(); Assert.Equal(1, rig.Driver.Writes);
        Assert.Null(rig.State.BlockedIdentity);
        Assert.Equal(failureId, rig.State.Queue[0].Id);
        Assert.True(rig.State.Queue[1].Success);
    }
    [Fact] public async Task Backup_mismatch_blocks_even_a_recognized_patch()
    {
        var rig = new Rig(); rig.Driver.Status = Original() with { State = BitratePatchState.Patched, BackupState = BitrateBackupState.Mismatch };
        await rig.Apply(); Assert.False(Assert.Single(rig.State.Queue).Success); Assert.Equal(0, rig.Driver.Writes);
    }
    [Fact] public async Task Each_real_replacement_of_the_same_build_gets_a_new_incident()
    {
        var rig = new Rig(); await rig.Apply(); var first = Assert.Single(rig.State.Queue).Id;
        rig.Driver.Status = Original(); await rig.Tick(); await rig.Tick(11);
        Assert.Equal(2, rig.State.Queue.Count); Assert.NotEqual(first, rig.State.Queue[1].Id);
    }
    [Fact] public async Task Disabled_cancels_queue_and_does_not_write()
    {
        var rig = new Rig(); await rig.Apply(); rig.Config.Enabled = false;
        rig.Driver.Status = Original(); await rig.Tick(100);
        Assert.Equal(1, rig.Driver.Writes); Assert.Empty(rig.State.Queue); Assert.Equal("Disabled", rig.State.State);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task Turning_off_each_type_cancels_only_that_type(bool success)
    {
        var rig = new Rig(); rig.Engine.Journal.Queue.Add(new("s", true, "success", rig.Now)); rig.Engine.Journal.Queue.Add(new("f", false, "failure", rig.Now));
        rig.Config.NotifySuccess = !success; rig.Config.NotifyFailure = success; rig.Engine.SettingsChanged(rig.Config);
        Assert.Equal(!success, Assert.Single(rig.State.Queue).Success); await Task.CompletedTask;
    }
    [Fact] public async Task Crash_after_replacement_recovers_only_with_matching_verified_original()
    {
        var rig = new Rig(); rig.State.InFlightSource = "original";
        rig.Driver.Status = Original() with { State = BitratePatchState.Patched, Sha256 = "patched", BackupSha256 = "original", BackupState = BitrateBackupState.VerifiedOriginal };
        await rig.Tick(); Assert.True(Assert.Single(rig.State.Queue).Success);
        rig.Engine = rig.Create(); await rig.Tick(); Assert.Single(rig.State.Queue);
    }
    [Fact] public async Task Crash_recovery_without_original_does_not_claim_success()
    {
        var rig = new Rig(); rig.State.InFlightSource = "original";
        rig.Driver.Status = Original() with { State = BitratePatchState.Patched, Sha256 = "patched" };
        await rig.Tick(); Assert.Empty(rig.State.Queue);
    }
    [Fact] public async Task Vr_starting_during_patch_is_deferred()
    {
        var rig = new Rig(); rig.Driver.Error = new InvalidOperationException();
        rig.Driver.AfterError = () => Original() with { VrServerRunning = true };
        await rig.Apply(); Assert.Empty(rig.State.Queue); Assert.Null(rig.State.BlockedIdentity);
    }
    [Fact] public async Task Driver_replaced_during_attempt_is_reinspected()
    {
        var rig = new Rig(); rig.Driver.Error = new IOException();
        rig.Driver.AfterError = () => Original() with { Sha256 = "new build" };
        await rig.Apply(); Assert.Empty(rig.State.Queue); Assert.Null(rig.State.BlockedIdentity);
    }
    [Fact] public async Task Post_replacement_failure_is_terminal_and_preserves_a_failure()
    {
        var rig = new Rig(); rig.Driver.Error = new PatchVerificationException("retained", new IOException());
        rig.Driver.AfterError = () => Original() with { Sha256 = "unverified" };
        await rig.Apply(); Assert.Equal("unverified", rig.State.BlockedIdentity);
        await rig.Tick(60); Assert.Equal(1, rig.Driver.Writes); Assert.False(Assert.Single(rig.State.Queue).Success);
    }
    [Fact] public async Task Permanent_application_error_is_not_retried_by_polling()
    {
        var rig = new Rig(); rig.Driver.Error = new UnauthorizedAccessException();
        await rig.Apply(); await rig.Tick(3600); Assert.Equal(1, rig.Driver.Writes); Assert.Single(rig.State.Queue);
    }
    [Fact] public async Task Invalid_data_during_application_reports_failure_instead_of_crashing()
    {
        var rig = new Rig(); rig.Driver.Error = new InvalidDataException("unsafe");
        await rig.Apply(); Assert.False(Assert.Single(rig.State.Queue).Success); Assert.NotNull(rig.State.BlockedIdentity);
    }
    [Fact] public async Task Concurrent_manual_patch_does_not_claim_service_success_or_failure()
    {
        var rig = new Rig(); rig.Driver.Error = new IOException("changed");
        rig.Driver.AfterError = () => Original() with { State = BitratePatchState.Patched, Sha256 = "patched", BackupState = BitrateBackupState.VerifiedOriginal, BackupSha256 = "original" };
        await rig.Apply(); Assert.Empty(rig.State.Queue); Assert.Null(rig.State.InFlightSource);
    }
    [Fact] public async Task Delivery_success_is_separate_from_patch_success()
    {
        var rig = new Rig(); await rig.Apply(); var lastSuccess = rig.State.LastSuccess;
        var sender = new Sender(new(true, false, "accepted"));
        await rig.Engine.DeliverAsync(rig.Config, Credentials, sender, default);
        Assert.Empty(rig.State.Queue); Assert.Equal(lastSuccess, rig.State.LastSuccess); Assert.Equal(1, sender.Calls);
    }
    [Fact] public async Task Delivery_transient_failure_backs_off_and_survives_restart()
    {
        var rig = new Rig(); await rig.Apply(); var sender = new Sender(new(false, false, "offline"));
        await rig.Engine.DeliverAsync(rig.Config, Credentials, sender, default);
        rig.Engine = rig.Create(); await rig.Engine.DeliverAsync(rig.Config, Credentials, sender, default);
        Assert.Equal(1, sender.Calls); Assert.NotNull(rig.State.LastSuccess);
        rig.Now = rig.Now.AddSeconds(31); await rig.Engine.DeliverAsync(rig.Config, Credentials, sender, default); Assert.Equal(2, sender.Calls);
    }
    [Fact] public async Task Permanent_delivery_error_pauses_all_until_settings_change()
    {
        var rig = new Rig(); await rig.Apply(); rig.Engine.Journal.Queue.Add(new("second", false, "failure", rig.Now));
        var sender = new Sender(new(false, true, "invalid"));
        await rig.Engine.DeliverAsync(rig.Config, Credentials, sender, default);
        rig.Now = rig.Now.AddHours(1); await rig.Engine.DeliverAsync(rig.Config, Credentials, sender, default); Assert.Equal(1, sender.Calls);
        rig.Engine.SettingsChanged(rig.Config); await rig.Engine.DeliverAsync(rig.Config, Credentials, sender, default); Assert.Equal(2, sender.Calls);
    }
    [Fact] public async Task Newly_queued_messages_also_obey_persisted_permanent_failure_pause()
    {
        var rig = new Rig(); await rig.Apply(); var sender = new Sender(new(false, true, "invalid"));
        await rig.Engine.DeliverAsync(rig.Config, Credentials, sender, default);
        rig.Engine = rig.Create(); rig.Engine.Journal.Queue.Add(new("new", false, "another failure", rig.Now));
        await rig.Engine.DeliverAsync(rig.Config, Credentials, sender, default); Assert.Equal(1, sender.Calls);
    }
    [Fact] public async Task Quota_pause_applies_to_new_incidents_as_well_as_existing_queue()
    {
        var rig = new Rig(); await rig.Apply(); var sender = new Sender(new(false, false, "quota", rig.Now.AddHours(1)));
        await rig.Engine.DeliverAsync(rig.Config, Credentials, sender, default);
        rig.Engine = rig.Create(); rig.Engine.Journal.Queue.Add(new("new", false, "failure", rig.Now));
        await rig.Engine.DeliverAsync(rig.Config, Credentials, sender, default); Assert.Equal(1, sender.Calls);
    }
    [Fact] public async Task Expired_notifications_are_removed()
    {
        var rig = new Rig(); await rig.Apply(); rig.Now = rig.Now.AddHours(25);
        await rig.Engine.DeliverAsync(rig.Config, Credentials, new Sender(new(true, false, "ok")), default); Assert.Empty(rig.State.Queue);
    }
    private sealed class HttpStub(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => reply(request);
    }
    [Theory]
    [InlineData(200, "{\"status\":1}", true, false)]
    [InlineData(200, "{\"status\":0}", false, true)]
    [InlineData(400, "{}", false, true)]
    [InlineData(500, "{}", false, false)]
    [InlineData(200, "not json", false, false)]
    [InlineData(200, "{\"status\":\"invalid\"}", false, true)]
    [InlineData(200, "[]", false, true)]
    public async Task Pushover_checks_both_http_and_json(int code, string body, bool accepted, bool permanent)
    {
        using var http = new HttpClient(new HttpStub(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent(body) })));
        var result = await new PushoverClient(http).SendAsync(Credentials, "", "patch", DateTimeOffset.UtcNow, default);
        Assert.Equal(accepted, result.Accepted); Assert.Equal(permanent, result.PermanentFailure);
    }
    [Fact] public async Task Pushover_uses_https_post_body_and_no_emergency_priority()
    {
        using var http = new HttpClient(new HttpStub(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("https://api.pushover.net/1/messages.json", request.RequestUri!.ToString());
            var body = await request.Content!.ReadAsStringAsync(); Assert.Contains("priority=0", body); Assert.Contains("device=headset", body);
            Assert.DoesNotContain(Credentials.Token, request.RequestUri.ToString());
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"status\":1}") };
        }));
        Assert.True((await new PushoverClient(http).SendAsync(Credentials, "headset", "patch", DateTimeOffset.UtcNow, default)).Accepted);
    }
    [Fact] public async Task Quota_reset_is_respected()
    {
        var reset = DateTimeOffset.UtcNow.AddHours(3);
        using var http = new HttpClient(new HttpStub(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests); response.Headers.Add("X-Limit-App-Reset", reset.ToUnixTimeSeconds().ToString());
            return Task.FromResult(response);
        }));
        var result = await new PushoverClient(http).SendAsync(Credentials, "", "patch", DateTimeOffset.UtcNow, default);
        Assert.False(result.PermanentFailure); Assert.Equal(reset.ToUnixTimeSeconds(), result.RetryAt!.Value.ToUnixTimeSeconds());
    }
}
