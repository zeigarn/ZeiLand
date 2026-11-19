using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using ZeiLand.Native;

namespace ZeiLand.Services;

/// <summary>A drive just plugged in: its root ("E:\"), its name (may be empty), its room, and whether it can be ejected.</summary>
internal sealed record DriveArrival(string Root, string Label, long Free, long Total, bool CanEject);

/// <summary>
/// USB sticks, USB disks and memory cards as they are plugged in or taken out. Windows tells every window when a
/// drive letter comes or goes (WM_DEVICECHANGE with a volume), so nothing is polled. Network drives, drive letters
/// made with "subst" and disks built into the PC (a BitLocker drive unlocked, say) are left out.
/// </summary>
internal sealed class DriveWatcher : IDisposable
{
    private const int DBT_DEVTYP_VOLUME = 2;
    private const short DBTF_NET = 2;

    private readonly HwndSource _host;
    private readonly HwndSourceHook _hook;
    private bool _enabled;

    public DriveWatcher(HwndSource host)
    {
        _host = host;
        _hook = WndProc;
    }

    /// <summary>A drive was plugged in. Raised on the dispatcher.</summary>
    public event Action<DriveArrival>? Arrived;

    /// <summary>A drive went away (taken out or ejected): its root. Raised on the dispatcher.</summary>
    public event Action<string>? Removed;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (value == _enabled) return;
            _enabled = value;
            if (value) _host.AddHook(_hook);
            else _host.RemoveHook(_hook);
        }
    }

    public void Dispose() => Enabled = false;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == HidNative.WM_DEVICECHANGE && (int)wParam is HidNative.DBT_DEVICEARRIVAL or HidNative.DBT_DEVICEREMOVECOMPLETE
            && lParam != IntPtr.Zero && Marshal.ReadInt32(lParam, 4) == DBT_DEVTYP_VOLUME
            && (Marshal.ReadInt16(lParam, 16) & DBTF_NET) == 0)
        {
            int letters = Marshal.ReadInt32(lParam, 12);
            bool arrived = (int)wParam == HidNative.DBT_DEVICEARRIVAL;
            for (int i = 0; i < 26; i++)
            {
                if ((letters & (1 << i)) == 0) continue;
                string root = $"{(char)('A' + i)}:\\";
                if (arrived) Announce(root);
                else Removed?.Invoke(root);
            }
        }
        return IntPtr.Zero;
    }

    private async void Announce(string root)
    {
        // A freshly plugged drive can take a moment to be readable.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0) await Task.Delay(700);
            var arrival = await Task.Run(() => Read(root));
            if (!_enabled) return;
            if (arrival is not null)
            {
                Arrived?.Invoke(arrival);
                return;
            }
        }
    }

    private static DriveArrival? Read(string root)
    {
        try
        {
            if (IsSubstituted(root)) return null;
            var drive = new DriveInfo(root);
            if (drive.DriveType is not (DriveType.Removable or DriveType.Fixed) || !drive.IsReady) return null;
            bool canEject = DriveNative.CanEject(root);
            if (drive.DriveType == DriveType.Fixed && !canEject) return null;
            return new DriveArrival(root, drive.VolumeLabel, drive.AvailableFreeSpace, drive.TotalSize, canEject);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A letter made with "subst" points at a folder ("\??\C:\…"), not at a device.</summary>
    private static bool IsSubstituted(string root)
    {
        var target = new StringBuilder(1024);
        return QueryDosDevice(root[..2], target, target.Capacity) > 0 && target.ToString().StartsWith(@"\??\", StringComparison.Ordinal);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int QueryDosDevice(string device, StringBuilder target, int size);
}
