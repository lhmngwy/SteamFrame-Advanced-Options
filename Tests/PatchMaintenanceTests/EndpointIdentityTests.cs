using SteamFrame.PatchMaintenance;

namespace SteamLinkVRSettings.Tests;

public sealed class EndpointIdentityTests
{
    private const string Expected = @"C:\Program Files\SteamFrame Advanced Options\PatchService\SteamFrame.PatchService.exe";
    private const string Account = @"NT SERVICE\SteamFramePatchService";

    [Theory]
    [InlineData(false, 4u)] [InlineData(true, 4u)] [InlineData(true, 2u)]
    public void Matching_registered_own_process_service_is_accepted(bool quoted, uint state)
    {
        ServiceEndpointIdentity.RequireMatch(123, 123, 123, state, 0x10, quoted ? '"' + Expected + '"' : Expected, Account, Expected);
    }

    [Theory]
    [InlineData(0u, 123u, 123u, 4u)]
    [InlineData(456u, 123u, 123u, 4u)]
    [InlineData(123u, 456u, 123u, 4u)]
    [InlineData(123u, 123u, 456u, 4u)]
    [InlineData(123u, 123u, 123u, 1u)]
    [InlineData(123u, 123u, 123u, 3u)]
    public void Forged_changed_or_stopped_processes_are_rejected(uint pipe, uint before, uint after, uint state)
    {
        Assert.Throws<IOException>(() => ServiceEndpointIdentity.RequireMatch(pipe, before, after, state, 0x10, Expected, Account, Expected));
    }

    [Theory]
    [InlineData(@"C:\Users\Public\SteamFrame.PatchService.exe")]
    [InlineData("\"C:\\Program Files\\SteamFrame Advanced Options\\PatchService\\SteamFrame.PatchService.exe\" --arbitrary-arguments")]
    [InlineData(null)]
    public void Wrong_images_or_extra_service_arguments_are_rejected(string? image)
    {
        Assert.Throws<IOException>(() => ServiceEndpointIdentity.RequireMatch(123, 123, 123, 4, 0x10, image, Account, Expected));
    }

    [Theory]
    [InlineData("LocalSystem", 0x10u)]
    [InlineData("NT SERVICE\\AnotherService", 0x10u)]
    [InlineData(Account, 0x20u)]
    public void Unexpected_accounts_or_shared_process_services_are_rejected(string account, uint type)
    {
        Assert.Throws<IOException>(() => ServiceEndpointIdentity.RequireMatch(123, 123, 123, 4, type, Expected, account, Expected));
    }

    [Fact]
    public async Task Installed_service_can_be_queried_read_only_without_process_inspection_privileges()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("SLVR_SERVICE_IDENTITY_SMOKE") == "1",
            "Opt-in read-only smoke test against an already installed/running service; never installs or changes it.");
        var response = await MaintenanceClient.SendAsync(new("status"), TestContext.Current.CancellationToken);
        Assert.True(response.Ok, response.Message);
        Assert.NotNull(response.Snapshot);
        Assert.Equal(1, response.Snapshot.ProtocolVersion);
    }
}
