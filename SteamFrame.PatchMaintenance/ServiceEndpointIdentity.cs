using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SteamFrame.PatchMaintenance;

/// <summary>SCM binds the protected service registration to its live PID without opening another account's process.</summary>
public static class ServiceEndpointIdentity
{
    private const uint QueryConfigAndStatus = 0x0001 | 0x0004;
    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Status
    {
        public uint ServiceType, State, ControlsAccepted, ExitCode, SpecificExitCode, CheckPoint, WaitHint, ProcessId, Flags;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Configuration
    {
        public uint ServiceType, StartType, ErrorControl;
        [MarshalAs(UnmanagedType.LPWStr)] public string? BinaryPath;
        public IntPtr LoadOrderGroup;
        public uint Tag;
        public IntPtr Dependencies;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Account;
        public IntPtr DisplayName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenService(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(ServiceHandle handle, uint level, out Status status, uint size, out uint needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(ServiceHandle handle, IntPtr buffer, uint size, out uint needed);

    public static void Verify(uint pipeServerProcessId)
    {
        using var manager = OpenSCManager(null, null, 1); // SC_MANAGER_CONNECT, no administrative writes.
        if (manager.IsInvalid) throw NativeError("Cannot query Windows Service Control Manager");
        using var service = OpenService(manager, MaintenancePaths.ServiceName, QueryConfigAndStatus);
        if (service.IsInvalid) throw NativeError("Cannot query the registered maintenance service");
        var before = ReadStatus(service);
        _ = QueryServiceConfig(service, IntPtr.Zero, 0, out var needed);
        var error = Marshal.GetLastWin32Error();
        if (error != 122 || needed < Marshal.SizeOf<Configuration>() || needed > 65536)
            throw new IOException("Cannot read the maintenance service configuration.", new Win32Exception(error));
        var memory = Marshal.AllocHGlobal(checked((int)needed));
        try
        {
            if (!QueryServiceConfig(service, memory, needed, out _)) throw NativeError("Cannot read the maintenance service configuration");
            var config = Marshal.PtrToStructure<Configuration>(memory);
            var after = ReadStatus(service);
            var expected = Path.Combine(MaintenancePaths.Install, "SteamFrame.PatchService.exe");
            RequireMatch(pipeServerProcessId, before.ProcessId, after.ProcessId, after.State, config.ServiceType,
                config.BinaryPath, config.Account, expected);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    // Kept separate so wrong PIDs, stopped services and forged image paths are covered without installing a service in tests.
    public static void RequireMatch(uint pipePid, uint beforePid, uint afterPid, uint state, uint type,
        string? registeredImage, string? account, string expectedImage)
    {
        if (pipePid == 0 || beforePid != pipePid || afterPid != pipePid || state is not (2 or 4) || type != 0x10
            || !string.Equals(account, @"NT SERVICE\" + MaintenancePaths.ServiceName, StringComparison.OrdinalIgnoreCase)
            || !(string.Equals(registeredImage, expectedImage, StringComparison.OrdinalIgnoreCase)
                || string.Equals(registeredImage, '"' + expectedImage + '"', StringComparison.OrdinalIgnoreCase)))
            throw new IOException("The maintenance endpoint does not match the registered Windows service. Check the service installation and retry.");
    }

    private static Status ReadStatus(ServiceHandle service)
    {
        if (!QueryServiceStatusEx(service, 0, out var status, (uint)Marshal.SizeOf<Status>(), out _))
            throw NativeError("Cannot read the maintenance service process ID");
        return status;
    }
    private static IOException NativeError(string operation)
    {
        var error = new Win32Exception(Marshal.GetLastWin32Error());
        return new IOException($"{operation}: {error.Message} (Windows error {error.NativeErrorCode}).", error);
    }
}
