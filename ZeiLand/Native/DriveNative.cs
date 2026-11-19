using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ZeiLand.Native;

internal enum EjectResult { Ejected, InUse, Failed }

/// <summary>
/// Safely removing a drive, as the taskbar's "Safely Remove Hardware" does: from its drive letter to its disk, then
/// up to the device that can be removed (a USB stick, a USB disk), which is asked to go. An app with a file open on
/// it vetoes that, and the drive stays.
/// </summary>
internal static class DriveNative
{
    private static readonly Guid DiskInterface = new("53f56307-b6bf-11d0-94f2-00a0c91efb8b"); // GUID_DEVINTERFACE_DISK

    private const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x2D1080;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    private const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
    private const int CR_SUCCESS = 0;
    private const uint DN_REMOVABLE = 0x4000;

    /// <summary>On a device Windows can remove (a USB stick or disk, not a disk built into the PC).</summary>
    public static bool CanEject(string root) => RemovableDevice(root) is not null;

    /// <summary>"E:\": asks the removable device holding it to go, a few times (a first veto often clears).</summary>
    public static EjectResult Eject(string root)
    {
        if (RemovableDevice(root) is not { } device) return EjectResult.Failed;

        var vetoName = new StringBuilder(260);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0) Thread.Sleep(400);
            vetoName.Clear();
            int result = CM_Request_Device_EjectW(device, out int veto, vetoName, vetoName.Capacity, 0);
            if (result == CR_SUCCESS && veto == 0) return EjectResult.Ejected;
        }
        return EjectResult.InUse;
    }

    private static uint? RemovableDevice(string root) =>
        DeviceNumber($@"\\.\{root[0]}:") is { } volume && FindDisk(volume) is { } disk ? RemovableAncestor(disk) : null;

    /// <summary>The storage device number of a volume or disk (which disk, which partition).</summary>
    private static (uint Type, uint Number)? DeviceNumber(string path)
    {
        using var handle = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var number = new STORAGE_DEVICE_NUMBER();
        return DeviceIoControl(handle, IOCTL_STORAGE_GET_DEVICE_NUMBER, IntPtr.Zero, 0, out number,
            Marshal.SizeOf<STORAGE_DEVICE_NUMBER>(), out _, IntPtr.Zero)
            ? (number.DeviceType, number.DeviceNumber)
            : null;
    }

    /// <summary>The disk's device node, found among the present disks by its number.</summary>
    private static uint? FindDisk((uint Type, uint Number) volume)
    {
        var guid = DiskInterface;
        IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) return null;
        IntPtr detail = Marshal.AllocHGlobal(2048);
        try
        {
            var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
            {
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                var info = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, 2048, out _, ref info)) continue;
                if (Marshal.PtrToStringUni(detail + 4) is { } path && DeviceNumber(path) == volume) return info.DevInst;
            }
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    /// <summary>The disk itself or the nearest parent Windows can remove; none for a disk built into the PC.</summary>
    private static uint? RemovableAncestor(uint node)
    {
        for (int level = 0; level < 4; level++)
        {
            if (CM_Get_DevNode_Status(out uint status, out _, node, 0) == CR_SUCCESS && (status & DN_REMOVABLE) != 0) return node;
            if (CM_Get_Parent(out node, node, 0) != CR_SUCCESS) return null;
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_DEVICE_NUMBER
    {
        public uint DeviceType;
        public uint DeviceNumber;
        public uint PartitionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition,
        uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, int inputSize,
        out STORAGE_DEVICE_NUMBER output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, int index, ref SP_DEVICE_INTERFACE_DATA data);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail,
        int detailSize, out int required, ref SP_DEVINFO_DATA info);

    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint parent, uint node, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Request_Device_EjectW(uint node, out int vetoType, StringBuilder vetoName, int nameLength, uint flags);
}
