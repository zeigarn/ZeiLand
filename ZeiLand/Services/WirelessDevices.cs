using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using ZeiLand.Native;

namespace ZeiLand.Services;

internal enum WirelessKind { Mouse, Keyboard, Headset, Other }

/// <param name="Id">The same across restarts (it is what the settings remember), e.g. "logitech:PRO X 2".</param>
/// <param name="Name">The device's own name; empty when its receiver doesn't tell (then the kind names it).</param>
/// <param name="Battery">Percent; null when the device doesn't say, or is off.</param>
internal sealed record WirelessDevice(string Id, string Name, WirelessKind Kind, bool Connected, int? Battery, bool Charging);

/// <summary>
/// Wireless mice, keyboards and headsets on 2.4 GHz receivers, and their batteries. Windows has no common way to
/// read these (only Bluetooth devices report a battery), so each brand's receiver is asked in its own protocol.
/// Logitech (HID++), Corsair (Bragi) and the SONiX receivers of Aula-type keyboards are spoken to directly: they
/// are found by themselves, again whenever a receiver is plugged in or out (WM_DEVICECHANGE), Logitech devices
/// announce being switched on / off and battery changes, Corsair receivers say when something changed. Every
/// other brand comes from BatteryHub, a separate program shipped next to ZeiLand (see
/// <see cref="BatteryHubReceiver"/>). On top of the announcements every device is asked when the island opens
/// (at most once a minute), every ten minutes and when a receiver is plugged in.
/// </summary>
internal sealed class WirelessDevices : IDisposable
{
    // Brands with a receiver protocol here: Logitech, Corsair, and the vendor ID SONiX receivers borrow.
    private static readonly HashSet<ushort> Vendors = [0x046D, 0x1B1C, 0x05AC];
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(1);

    private readonly HwndSource _host;
    private readonly HwndSourceHook _hook;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _rescanTimer;
    private readonly Dictionary<string, WirelessReceiver> _receivers = [];
    private readonly Dictionary<string, WirelessDevice> _devices = [];
    private BatteryHubReceiver? _hub;
    private IntPtr _notification;
    private DateTime _lastRefresh;
    private bool _enabled, _scanning;

    public WirelessDevices(HwndSource host, Dispatcher dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;
        _hook = WndProc;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Interval };
        _refreshTimer.Tick += (_, _) => Refresh();
        // A receiver plugged in or out raises a burst of interface arrivals: one look once it settles.
        _rescanTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
        _rescanTimer.Tick += (_, _) =>
        {
            _rescanTimer.Stop();
            Rescan();
        };
    }

    /// <summary>A device appeared or changed; the previous state is null the first time it's seen.</summary>
    public event Action<WirelessDevice, WirelessDevice?>? Updated;

    /// <summary>The list or a reading in it changed.</summary>
    public event Action? Changed;

    /// <summary>False until the first look at the receivers is over: devices found then were there already.</summary>
    public bool Settled { get; private set; }

    public IReadOnlyList<WirelessDevice> Devices =>
        _devices.Values.OrderBy(d => d.Kind).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (value == _enabled) return;
            _enabled = value;
            if (value) Start();
            else Stop();
        }
    }

    /// <summary>The island opened: fresh readings, unless the last ones are recent.</summary>
    public void RefreshIfStale()
    {
        if (_enabled && Settled && DateTime.UtcNow - _lastRefresh > Stale) Refresh();
    }

    public void Dispose() => Stop();

    private void Start()
    {
        _host.AddHook(_hook);
        _notification = HidNative.RegisterForArrivals(_host.Handle);
        _hub = BatteryHubReceiver.Find() is { } exe ? new BatteryHubReceiver(exe, Report, Remove) : null;
        _refreshTimer.Start();
        Rescan();
    }

    private void Stop()
    {
        _host.RemoveHook(_hook);
        HidNative.UnregisterForArrivals(_notification);
        _notification = IntPtr.Zero;
        _refreshTimer.Stop();
        _rescanTimer.Stop();
        foreach (var receiver in _receivers.Values) receiver.Dispose();
        _receivers.Clear();
        _hub?.Dispose();
        _hub = null;
        _devices.Clear();
        Settled = false;
        Changed?.Invoke();
    }

    /// <summary>Opens new receivers, closes the ones that are gone; then BatteryHub looks for the rest.</summary>
    private async void Rescan()
    {
        if (_scanning)
        {
            _rescanTimer.Start(); // once this look is over
            return;
        }
        _scanning = true;
        try
        {
            var groups = await Task.Run(() => HidNative.Enumerate(Vendors).GroupBy(i => i.Group, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase));
            if (!_enabled) return;

            foreach (var key in _receivers.Keys.Where(k => !groups.ContainsKey(k)).ToList())
            {
                var receiver = _receivers[key];
                _receivers.Remove(key);
                receiver.Dispose();
                foreach (var id in _devices.Keys.Where(receiver.Owns).ToList()) Remove(id);
            }

            var started = new List<Task>();
            foreach (var (key, interfaces) in groups)
            {
                if (_receivers.ContainsKey(key) || WirelessReceiver.Create(interfaces, Report, Remove) is not { } receiver) continue;
                _receivers[key] = receiver;
                started.Add(receiver.StartAsync());
            }
            await Task.WhenAll(started);
            _lastRefresh = DateTime.UtcNow;
            // The devices spoken to directly are known by now, so BatteryHub's copies of them can be left out.
            if (_hub is not null) await _hub.RefreshAsync();
        }
        finally
        {
            _scanning = false;
            Settled = true;
        }
    }

    private async void Refresh()
    {
        if (!_enabled) return;
        _lastRefresh = DateTime.UtcNow;
        await Task.WhenAll(_receivers.Values.Append(_hub).OfType<WirelessReceiver>().Select(r => r.RefreshAsync()));
    }

    private bool IsOwned(string id) => _receivers.Values.Any(r => r.Owns(id)) || _hub?.Owns(id) == true;

    /// <summary>Called by the receivers, from any thread.</summary>
    private void Report(WirelessDevice device) => _dispatcher.InvokeAsync(() =>
    {
        if (!_enabled || !IsOwned(device.Id)) return;
        if (BatteryHubReceiver.IsCopy(device, _devices.Values))
        {
            // Spoken to directly already: that one, with its announcements, is the one shown.
            if (_devices.Remove(device.Id)) Changed?.Invoke();
            return;
        }
        foreach (var copy in _devices.Values.Where(d => BatteryHubReceiver.IsCopy(d, [device])).Select(d => d.Id).ToList())
            _devices.Remove(copy);

        _devices.TryGetValue(device.Id, out var previous);
        if (device == previous) return;
        _devices[device.Id] = device;
        Updated?.Invoke(device, previous);
        Changed?.Invoke();
    });

    /// <summary>Gone: its receiver was unplugged, or BatteryHub doesn't find it any more. Called from any thread.</summary>
    private void Remove(string id) => _dispatcher.InvokeAsync(() =>
    {
        if (!_devices.Remove(id, out var previous)) return;
        Updated?.Invoke(previous with { Connected = false, Battery = null }, previous);
        Changed?.Invoke();
    });

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == HidNative.WM_DEVICECHANGE && (int)wParam is HidNative.DBT_DEVICEARRIVAL or HidNative.DBT_DEVICEREMOVECOMPLETE
            && lParam != IntPtr.Zero && Marshal.ReadInt32(lParam, 4) == HidNative.DBT_DEVTYP_DEVICEINTERFACE)
        {
            _rescanTimer.Stop();
            _rescanTimer.Start();
        }
        return IntPtr.Zero;
    }
}
