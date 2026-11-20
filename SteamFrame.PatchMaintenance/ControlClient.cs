using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace SteamFrame.PatchMaintenance;

public static class ControlProtocol
{
    private const int Limit = 65536;
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > Limit) throw new InvalidDataException("Control message too large.");
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), ct);
        await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, ct);
        var length = BitConverter.ToInt32(header);
        if (length is <= 0 or > Limit) throw new InvalidDataException("Invalid control message length.");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, ct);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Empty control message.");
    }
}

public static class MaintenanceClient
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    public static async Task<ControlReply> SendAsync(ControlRequest request, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var pipe = new NamedPipeClientStream(".", MaintenancePaths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(3000, timeout.Token);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)) throw new IOException("Cannot verify maintenance service identity.");
        ServiceEndpointIdentity.Verify(pid);
        await ControlProtocol.WriteAsync(pipe, request, timeout.Token);
        return await ControlProtocol.ReadAsync<ControlReply>(pipe, timeout.Token);
    }

    public static async Task<MaintenanceSnapshot?> DisableBeforeRestoreAsync(CancellationToken ct = default)
    {
        // The protected installed payload remains present even if a service is stopped or unhealthy.
        if (!File.Exists(Path.Combine(MaintenancePaths.Install, "SteamFrame.PatchService.exe"))) return null;
        var reply = await SendAsync(new("disable"), ct);
        if (!reply.Ok) throw new InvalidOperationException("Automatic maintenance must be disabled before restoring: " + reply.Message);
        return reply.Snapshot;
    }
}
