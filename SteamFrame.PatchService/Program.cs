using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SteamFrame.PatchMaintenance;
using SteamFrame.PatchService;

try
{
    if (args.Length > 0)
    {
        return await InstallerEntryPoint.RunAsync(args);
    }
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddWindowsService(options => options.ServiceName = MaintenancePaths.ServiceName);
    builder.Logging.ClearProviders();
    builder.Logging.AddEventLog(options => { options.SourceName = MaintenancePaths.ServiceName; options.LogName = "Application"; });
    builder.Services.AddHostedService<MaintenanceWorker>();
    await builder.Build().RunAsync();
    return 0;
}
catch (Exception)
{
    // Never log command payloads, credentials, or raw exception paths to the event log.
    return 1;
}
