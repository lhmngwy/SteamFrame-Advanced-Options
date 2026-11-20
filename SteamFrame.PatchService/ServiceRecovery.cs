using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SteamFrame.PatchMaintenance;

namespace SteamFrame.PatchService;

internal static class ServiceRecovery
{
    // ChangeServiceConfig2 requires SERVICE_START as well as SERVICE_CHANGE_CONFIG
    // when any failure action is SC_ACTION_RESTART, even for an administrator.
    internal const uint RequiredServiceAccess = 0x0002 | 0x0010;
    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr service);
    [StructLayout(LayoutKind.Sequential)]
    private struct ActionEntry { public uint Type; public uint Delay; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FailureActions
    {
        public uint ResetPeriod;
        public IntPtr RebootMessage;
        public IntPtr Command;
        public uint Count;
        public IntPtr Actions;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenService(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2(ServiceHandle service, uint level, ref FailureActions actions);

    public static void Configure()
    {
        using var manager = OpenSCManager(null, null, 1); // SC_MANAGER_CONNECT
        if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var service = OpenService(manager, MaintenancePaths.ServiceName, RequiredServiceAccess);
        if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var entries = new[] { new ActionEntry { Type = 1, Delay = 30_000 }, new ActionEntry { Type = 1, Delay = 120_000 },
            new ActionEntry { Type = 1, Delay = 300_000 }, new ActionEntry { Type = 0, Delay = 0 } }; // SC_ACTION_NONE ends the retry loop.
        var size = Marshal.SizeOf<ActionEntry>(); var memory = Marshal.AllocHGlobal(size * entries.Length);
        try
        {
            for (var i = 0; i < entries.Length; i++) Marshal.StructureToPtr(entries[i], memory + i * size, false);
            var config = new FailureActions { ResetPeriod = 86400, Count = (uint)entries.Length, Actions = memory };
            if (!ChangeServiceConfig2(service, 2, ref config)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
}
