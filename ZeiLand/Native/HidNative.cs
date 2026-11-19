using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace ZeiLand.Native;

/// <summary>One HID collection (a top-level usage of a device's interface), as Windows lists it.</summary>
/// <param name="Group">The path up to the collection: collections of the same interface share it.</param>
internal sealed record HidInterface(string Path, string Group, ushort VendorId, ushort ProductId, ushort UsagePage, ushort Usage,
    int InputLength, int OutputLength, byte OutputReportId, string Product);

/// <summary>Finding and opening HID devices (SetupAPI + hid.dll).</summary>
internal static partial class HidNative
{
    public static readonly Guid InterfaceGuid = new("4d1e55b2-f16f-11cf-88cb-001111000030");

    /// <summary>The present HID collections of the given vendors, each described by its own report descriptor.</summary>
    public static List<HidInterface> Enumerate(IReadOnlySet<ushort> vendors)
    {
        var list = new List<HidInterface>();
        foreach (var path in InterfacePaths())
        {
            // The vendor is in the path; only those are opened.
            var match = IdPattern().Match(path);
            if (!match.Success) continue;
            ushort vendor = Convert.ToUInt16(match.Groups[1].Value, 16), product = Convert.ToUInt16(match.Groups[2].Value, 16);
            if (!vendors.Contains(vendor)) continue;
            if (Describe(path, vendor, product) is { } found) list.Add(found);
        }
        return list;
    }

    /// <summary>Read/write access for reports, asynchronous (a pending read costs nothing).</summary>
    public static FileStream? Open(string path)
    {
        var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }
        return new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: true);
    }

    /// <summary>WM_DEVICECHANGE to this window whenever a HID interface arrives or goes away.</summary>
    public static IntPtr RegisterForArrivals(IntPtr window)
    {
        var filter = new DEV_BROADCAST_DEVICEINTERFACE
        {
            dbcc_size = Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
            dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
            dbcc_classguid = InterfaceGuid,
        };
        return RegisterDeviceNotification(window, ref filter, DEVICE_NOTIFY_WINDOW_HANDLE);
    }

    public static void UnregisterForArrivals(IntPtr registration)
    {
        if (registration != IntPtr.Zero) UnregisterDeviceNotification(registration);
    }

    public const int WM_DEVICECHANGE = 0x0219;
    public const int DBT_DEVICEARRIVAL = 0x8000, DBT_DEVICEREMOVECOMPLETE = 0x8004, DBT_DEVTYP_DEVICEINTERFACE = 5;
    private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEV_BROADCAST_DEVICEINTERFACE
    {
        public int dbcc_size;
        public int dbcc_devicetype;
        public int dbcc_reserved;
        public Guid dbcc_classguid;
        public char dbcc_name;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterDeviceNotification(IntPtr recipient, ref DEV_BROADCAST_DEVICEINTERFACE filter, int flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);

    private static HidInterface? Describe(string path, ushort vendor, ushort product)
    {
        // No access needed for the descriptor and the strings, even for keyboards and mice Windows holds on to.
        using var handle = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid || !HidD_GetPreparsedData(handle, out var data)) return null;
        try
        {
            if (HidP_GetCaps(data, out var caps) != HIDP_STATUS_SUCCESS) return null;
            var name = new char[128];
            string productName = HidD_GetProductString(handle, name, name.Length * 2) ? new string(name).TrimEnd('\0') : "";
            int collection = path.IndexOf("&col", StringComparison.OrdinalIgnoreCase);
            string group = collection < 0 ? path : path[..collection];
            return new HidInterface(path, group, vendor, product, caps.UsagePage, caps.Usage, caps.InputReportByteLength,
                caps.OutputReportByteLength, OutputReportId(data), productName);
        }
        finally
        {
            HidD_FreePreparsedData(data);
        }
    }

    /// <summary>The ID that starts an output report of this collection (0 if it has none).</summary>
    private static byte OutputReportId(IntPtr data)
    {
        // HIDP_VALUE_CAPS and HIDP_BUTTON_CAPS are both 72 bytes, with the report ID at offset 2.
        var caps = new byte[72 * 16];
        ushort count = 16;
        if (HidP_GetValueCaps(HidP_Output, caps, ref count, data) == HIDP_STATUS_SUCCESS && count > 0) return caps[2];
        count = 16;
        if (HidP_GetButtonCaps(HidP_Output, caps, ref count, data) == HIDP_STATUS_SUCCESS && count > 0) return caps[2];
        return 0;
    }

    private static List<string> InterfacePaths()
    {
        var paths = new List<string>();
        var guid = InterfaceGuid;
        var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) return paths;
        try
        {
            var item = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref item); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref item, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                if (size == 0) continue;
                var detail = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6); // SP_DEVICE_INTERFACE_DETAIL_DATA.cbSize
                    if (SetupDiGetDeviceInterfaceDetail(set, ref item, detail, size, out _, IntPtr.Zero))
                        paths.Add(Marshal.PtrToStringUni(detail + 4)!);
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return paths;
    }

    [GeneratedRegex(@"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex IdPattern();

    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
    private const int DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
    private const int HidP_Output = 1;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, int index, ref SP_DEVICE_INTERFACE_DATA data);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail,
        int size, out int required, IntPtr info);

    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition,
        uint flags, IntPtr template);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FreePreparsedData(IntPtr data);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetProductString(SafeFileHandle handle, char[] buffer, int length);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);

    [DllImport("hid.dll")]
    private static extern int HidP_GetValueCaps(int type, byte[] caps, ref ushort length, IntPtr data);

    [DllImport("hid.dll")]
    private static extern int HidP_GetButtonCaps(int type, byte[] caps, ref ushort length, IntPtr data);
}
