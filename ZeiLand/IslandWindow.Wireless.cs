using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZeiLand.Controls;
using ZeiLand.Services;

namespace ZeiLand;

// Wireless mice, keyboards and headsets: a battery ring each in the open island, and notices when one is switched
// on or off (or its receiver plugged in or out) and when a battery runs low.
public partial class IslandWindow
{
    private const string MouseGlyphText = "", KeyboardGlyphText = "", DeviceGlyphText = "";

    // Off for less than this (a blip in the radio, a quick restart) is neither "off" nor "back".
    private static readonly TimeSpan OffGrace = TimeSpan.FromSeconds(5);

    private WirelessDevices? _wireless;
    private readonly Dictionary<string, DateTime> _wirelessOffSince = [];
    private readonly Dictionary<string, int> _wirelessWarned = []; // the lowest battery threshold already announced

    internal void AttachWireless(WirelessDevices wireless)
    {
        _wireless = wireless;
        wireless.Updated += OnWirelessUpdated;
        wireless.Changed += ShowWirelessDevices;
        wireless.Enabled = _settings.ShowWirelessDevices;
        ShowWirelessDevices(); // in case it had found them already
    }

    /// <summary>Shown on the island: found, and not unticked in the settings.</summary>
    private List<WirelessDevice> VisibleWirelessDevices() =>
        _wireless is { Enabled: true } wireless
            ? wireless.Devices.Where(d => !_settings.HiddenWirelessDevices.Contains(d.Id, StringComparer.OrdinalIgnoreCase)
                && (d.Connected || d.Battery is not null)).ToList()
            : [];

    private const double RingSize = 24, RingRowGap = 8;
    private static readonly Brush RingBrush = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)).Frozen();

    private void ShowWirelessDevices()
    {
        var devices = VisibleWirelessDevices();
        foreach (var panel in new[] { HomeDevices, MediaDevices })
        {
            panel.Children.Clear();
            // Three to a row, under the CPU, GPU and RAM columns; more go on another row.
            var gap = new Thickness(0, 0, 0, devices.Count > 3 ? RingRowGap : 0);
            foreach (var device in devices) panel.Children.Add(DeviceItem(device, gap));
            panel.Margin = new Thickness(0, 0, 0, -gap.Bottom);
        }
        var visibility = devices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HomeDevicesRow.Visibility = MediaDevicesRow.Visibility = visibility;
        ConfigureStats();
        UpdateExpandedSizes();
    }

    /// <summary>How many rows of devices the open island shows (three to a row).</summary>
    private int WirelessRows => (HomeDevices.Children.Count + 2) / 3;

    /// <summary>The stats sit under the separator, or under the devices' row (which then has the separator).</summary>
    private void ConfigureStats()
    {
        bool devices = HomeDevices.Children.Count > 0;
        HomeStats.Configure(_settings, separated: !devices);
        MediaStats.Configure(_settings, separated: !devices);
        HomeStats.Margin = new Thickness(0, devices ? 12 : 0, 0, 0);
        MediaStats.Margin = new Thickness(0, devices ? 12 : 8, 0, 0);
    }

    /// <summary>A ring for the battery with the device's icon in it, and the percentage beside it.</summary>
    private FrameworkElement DeviceItem(WirelessDevice device, Thickness margin)
    {
        var icon = (Style)FindResource("Icon");
        bool low = device.Battery <= 20 && !device.Charging;
        var ring = new Grid { Width = RingSize, Height = RingSize };
        ring.Children.Add(new BatteryRing
        {
            Level = (device.Battery ?? 0) / 100.0,
            Brush = device.Charging ? GreenBrush : low ? RedBrush : RingBrush,
        });
        ring.Children.Add(new TextBlock
        {
            Style = icon,
            Text = GlyphOf(device.Kind),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = RingBrush,
        });

        var item = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = margin,
            Background = Brushes.Transparent, // the tooltip shows over the gaps too
            Opacity = device.Connected ? 1 : 0.45,
            ToolTip = device.Connected ? NameOf(device) : $"{NameOf(device)} · {Loc.T("WirelessOff")}",
        };
        item.Children.Add(ring);
        item.Children.Add(new TextBlock
        {
            Text = device.Battery is { } battery ? Loc.Percent(battery / 100.0) : "–",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = low ? RedBrush : Brushes.White,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (device.Charging)
            item.Children.Add(new TextBlock { Style = icon, Text = "", FontSize = 10, Margin = new Thickness(3, 0, 0, 0), Foreground = GreenBrush });
        return item;
    }

    private void OnWirelessUpdated(WirelessDevice device, WirelessDevice? previous)
    {
        var now = DateTime.UtcNow;
        if (!device.Connected)
        {
            if (previous is not { Connected: false })
            {
                _wirelessOffSince[device.Id] = now;
                if (previous is { Connected: true }) AnnounceOff(device, now);
            }
            return;
        }
        bool hidden = _settings.HiddenWirelessDevices.Contains(device.Id, StringComparer.OrdinalIgnoreCase);

        // Switched on (or its receiver plugged in), unless it was off only for a moment.
        bool back = previous is null
            ? _wireless?.Settled == true
            : !previous.Connected && (!_wirelessOffSince.TryGetValue(device.Id, out var since) || now - since >= OffGrace);
        _wirelessOffSince.Remove(device.Id);
        if (hidden || _expanded) return;
        if (back && _settings.AnnounceWirelessConnections)
        {
            ShowDevice(GlyphOf(device.Kind), NameOf(device), null, Loc.T("Connected"), device.Battery, 3500);
            return;
        }

        // Low: once at 20%, once more at 10%; again after it has been charged.
        if (device.Charging || device.Battery is not { } battery || battery > 25)
        {
            _wirelessWarned.Remove(device.Id);
            return;
        }
        int threshold = battery <= 10 ? 10 : battery <= 20 ? 20 : 0;
        if (threshold == 0 || (_wirelessWarned.TryGetValue(device.Id, out var warned) && warned <= threshold)) return;
        _wirelessWarned[device.Id] = threshold;
        ShowDevice(GlyphOf(device.Kind), NameOf(device), null, Loc.T("BatteryLow"), battery, 4000);
    }

    /// <summary>"Disconnected", once it has stayed off for a few seconds.</summary>
    private async void AnnounceOff(WirelessDevice device, DateTime since)
    {
        await Task.Delay(OffGrace);
        // Back on meanwhile (or off again since): nothing to say.
        if (!_wirelessOffSince.TryGetValue(device.Id, out var off) || off != since) return;
        if (!_settings.AnnounceWirelessConnections || _expanded
            || _settings.HiddenWirelessDevices.Contains(device.Id, StringComparer.OrdinalIgnoreCase)) return;
        ShowDevice(GlyphOf(device.Kind), NameOf(device), null, Loc.T("Disconnected"), null, 3000);
    }

    internal static string NameOf(WirelessDevice device) => device.Name.Length > 0 ? device.Name : Loc.T(device.Kind switch
    {
        WirelessKind.Mouse => "WirelessMouse",
        WirelessKind.Keyboard => "WirelessKeyboard",
        WirelessKind.Headset => "WirelessHeadset",
        _ => "WirelessOther",
    });

    internal static string GlyphOf(WirelessKind kind) => kind switch
    {
        WirelessKind.Mouse => MouseGlyphText,
        WirelessKind.Keyboard => KeyboardGlyphText,
        WirelessKind.Headset => HeadphonesGlyphText,
        _ => DeviceGlyphText,
    };
}
