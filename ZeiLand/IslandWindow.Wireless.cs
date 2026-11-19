using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZeiLand.Services;

namespace ZeiLand;

// Wireless mice, keyboards and headsets: a battery chip each in the open island, and notices when a receiver is
// plugged in, a device comes back after a long while switched off, or a battery runs low.
public partial class IslandWindow
{
    private const string MouseGlyphText = "", KeyboardGlyphText = "", DeviceGlyphText = "";

    // A device switched off for less than this (asleep, a quick restart) isn't announced when it comes back.
    private static readonly TimeSpan AnnounceAfter = TimeSpan.FromMinutes(10);

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

    private void ShowWirelessDevices()
    {
        var devices = VisibleWirelessDevices();
        foreach (var panel in new[] { HomeDevices, MediaDevices })
        {
            panel.Children.Clear();
            foreach (var device in devices) panel.Children.Add(DeviceChip(device));
            panel.Visibility = devices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateExpandedSizes();
    }

    private FrameworkElement DeviceChip(WirelessDevice device)
    {
        var icon = (Style)FindResource("Icon");
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Style = icon, Text = GlyphOf(device.Kind), FontSize = 13, Margin = new Thickness(0, 0, 6, 0) });
        bool low = device.Battery <= 20 && !device.Charging;
        content.Children.Add(new TextBlock
        {
            Text = device.Battery is { } battery ? Loc.Percent(battery / 100.0) : "–",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = low ? RedBrush : Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (device.Charging)
            content.Children.Add(new TextBlock { Style = icon, Text = "", FontSize = 10, Margin = new Thickness(3, 0, 0, 0), Foreground = GreenBrush });
        return new Border
        {
            Background = SelectedTabBrush,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(9, 3, 10, 3),
            Margin = new Thickness(0, 0, 6, 0),
            Child = content,
            Opacity = device.Connected ? 1 : 0.45,
            ToolTip = device.Connected ? NameOf(device) : $"{NameOf(device)} · {Loc.T("WirelessOff")}",
        };
    }

    private void OnWirelessUpdated(WirelessDevice device, WirelessDevice? previous)
    {
        var now = DateTime.UtcNow;
        if (!device.Connected)
        {
            if (previous is not { Connected: false }) _wirelessOffSince[device.Id] = now;
            return;
        }
        if (_settings.HiddenWirelessDevices.Contains(device.Id, StringComparer.OrdinalIgnoreCase) || _expanded) return;

        // Here again: its receiver was plugged in, or it was off for a good while.
        bool back = previous is null
            ? _wireless?.Settled == true
            : !previous.Connected && (!_wirelessOffSince.TryGetValue(device.Id, out var since) || now - since >= AnnounceAfter);
        _wirelessOffSince.Remove(device.Id);
        if (back)
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
