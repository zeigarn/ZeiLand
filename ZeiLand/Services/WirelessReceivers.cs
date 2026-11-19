using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ZeiLand.Native;

namespace ZeiLand.Services;

/// <summary>A receiver (or a device plugged in directly), spoken to in its brand's protocol. Only questions are
/// sent: nothing on the device is ever changed.</summary>
internal abstract partial class WirelessReceiver : IDisposable
{
    private readonly Action<WirelessDevice> _report;
    private readonly HashSet<string> _ids = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lock = new();
    private (Func<byte[], bool?> Match, TaskCompletionSource<byte[]?> Done)? _pending;

    protected WirelessReceiver(Action<WirelessDevice> report) => _report = report;

    protected CancellationTokenSource Stopping { get; } = new();

    /// <summary>The receiver for this interface's collections, if its brand and protocol are known.</summary>
    public static WirelessReceiver? Create(IReadOnlyList<HidInterface> interfaces, Action<WirelessDevice> report)
    {
        HidInterface? Find(ushort page, ushort usage) => interfaces.FirstOrDefault(i => i.UsagePage == page && i.Usage == usage);
        return interfaces[0].VendorId switch
        {
            0x046D when Find(0xFF00, 0x0002) is { OutputLength: 20 } hidpp => new LogitechReceiver(hidpp, Find(0xFF00, 0x0001), report),
            0x1B1C when Find(0xFF42, 0x0001) is { OutputLength: > 5 } bragi => new CorsairReceiver(bragi, Find(0xFF42, 0x0002), report),
            0x05AC when Find(0xFF60, 0x0061) is { OutputLength: 33 } sonix => new SonixReceiver(sonix, report),
            _ => null,
        };
    }

    public bool Owns(string id)
    {
        lock (_ids) return _ids.Contains(id);
    }

    /// <summary>Opens the receiver and reads what's on it.</summary>
    public abstract Task StartAsync();

    /// <summary>Asks for the batteries again.</summary>
    public abstract Task RefreshAsync();

    public virtual void Dispose() => Stopping.Cancel();

    protected void Report(WirelessDevice device)
    {
        lock (_ids) _ids.Add(device.Id);
        _report(device);
    }

    /// <summary>
    /// Sends a report and waits for its answer. <paramref name="match"/> says true for the answer, false for an
    /// error answer to it, null for anything else (which is then something the device said by itself).
    /// </summary>
    protected async Task<byte[]?> AskAsync(HidChannel channel, byte[] request, Func<byte[], bool?> match, int milliseconds = 1000)
    {
        try
        {
            await _gate.WaitAsync(Stopping.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        try
        {
            var done = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock) _pending = (match, done);
            if (!await channel.WriteAsync(request)) return null;
            var finished = await Task.WhenAny(done.Task, Task.Delay(milliseconds, Stopping.Token));
            return finished == done.Task ? done.Task.Result : null;
        }
        finally
        {
            lock (_lock) _pending = null;
            _gate.Release();
        }
    }

    /// <summary>An input report: the answer being waited for, or else something the device says by itself.</summary>
    protected void Received(byte[] report)
    {
        TaskCompletionSource<byte[]?>? done = null;
        bool? verdict = null;
        lock (_lock)
        {
            if (_pending is { } pending && (verdict = pending.Match(report)) is not null) done = pending.Done;
        }
        if (done is not null) done.TrySetResult(verdict == true ? report : null);
        else OnNotification(report);
    }

    protected virtual void OnNotification(byte[] report)
    {
    }

    protected static WirelessKind KindFromName(string name) =>
        HeadsetName().IsMatch(name) ? WirelessKind.Headset
        : MouseName().IsMatch(name) ? WirelessKind.Mouse
        : KeyboardName().IsMatch(name) ? WirelessKind.Keyboard
        : WirelessKind.Other;

    [GeneratedRegex(@"HEADSET|VOID|VIRTUOSO|\bHS\d", RegexOptions.IgnoreCase)]
    private static partial Regex HeadsetName();

    [GeneratedRegex(@"MOUSE|DARK ?CORE|HARPOON|IRONCLAW|SABRE|KATAR|SCIMITAR|NIGHTSWORD|\bM\d{2}", RegexOptions.IgnoreCase)]
    private static partial Regex MouseName();

    [GeneratedRegex(@"KEYBOARD|\bK\d{2}", RegexOptions.IgnoreCase)]
    private static partial Regex KeyboardName();
}

/// <summary>
/// A HID collection opened twice: one handle always has a read pending (it costs nothing while the device is
/// quiet, and catches what it says by itself), the other writes.
/// </summary>
internal sealed class HidChannel : IDisposable
{
    private readonly FileStream _reader;
    private readonly FileStream? _writer;
    private readonly int _outputLength;
    private readonly CancellationToken _stopping;

    private HidChannel(FileStream reader, FileStream? writer, int outputLength, CancellationToken stopping)
    {
        _reader = reader;
        _writer = writer;
        _outputLength = outputLength;
        _stopping = stopping;
    }

    public static HidChannel? Open(HidInterface hid, Action<byte[]> received, CancellationToken stopping)
    {
        if (HidNative.Open(hid.Path) is not { } reader) return null;
        var channel = new HidChannel(reader, hid.OutputLength > 0 ? HidNative.Open(hid.Path) : null, hid.OutputLength, stopping);
        _ = channel.ReadAsync(hid.InputLength, received);
        stopping.Register(channel.Dispose);
        return channel;
    }

    public async Task<bool> WriteAsync(byte[] report)
    {
        if (_writer is null) return false;
        var padded = new byte[_outputLength];
        Array.Copy(report, padded, Math.Min(report.Length, padded.Length));
        try
        {
            await _writer.WriteAsync(padded, _stopping);
            return true;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _reader.Dispose();
        _writer?.Dispose();
    }

    private async Task ReadAsync(int length, Action<byte[]> received)
    {
        var buffer = new byte[Math.Max(length, 1)];
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                int read = await _reader.ReadAsync(buffer, _stopping);
                if (read == 0) break;
                received(buffer[..read]);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Unplugged or closed.
        }
    }
}

/// <summary>
/// Logitech's HID++ 2.0 (Lightspeed, Unifying and Bolt receivers, or a device on its cable): name and type from
/// feature 0x0005, the battery from 0x1004 (or the older 0x1000 / 0x1001). Devices say when they are switched on or
/// off (notification 0x41) and when the battery changes, so most news needs no question.
/// </summary>
internal sealed class LogitechReceiver(HidInterface hidpp, HidInterface? shortHidpp, Action<WirelessDevice> report)
    : WirelessReceiver(report)
{
    private const byte ShortReport = 0x10, LongReport = 0x11, SoftwareId = 0x0A;
    private const ushort NameFeature = 0x0005, UnifiedBattery = 0x1004, BatteryStatus = 0x1000, BatteryVoltage = 0x1001;

    private readonly bool _isReceiver = hidpp.Product.Contains("Receiver", StringComparison.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<byte, Slot> _slots = new();
    private HidChannel? _channel;

    private sealed class Slot
    {
        public string Name = "";
        public WirelessKind Kind = WirelessKind.Other;
        public byte BatteryIndex;
        public ushort BatteryFeature;
        public WirelessDevice? Last;
    }

    private IEnumerable<byte> Indexes => _isReceiver ? [1, 2, 3, 4, 5, 6] : [0xFF];

    public override async Task StartAsync()
    {
        _channel = HidChannel.Open(hidpp, Received, Stopping.Token);
        // Errors and the "switched on / off" notifications come as short reports, on their own collection.
        if (shortHidpp is not null) HidChannel.Open(shortHidpp, Received, Stopping.Token);
        if (_channel is null) return;
        foreach (var index in Indexes) await ProbeAsync(index);
    }

    public override async Task RefreshAsync()
    {
        foreach (var index in Indexes)
        {
            if (_slots.TryGetValue(index, out var slot)) await ReadBatteryAsync(index, slot);
            else await ProbeAsync(index); // switched on since, and its notification missed
        }
    }

    protected override void OnNotification(byte[] r)
    {
        if (r.Length < 5) return;
        byte index = r[1];
        if (r[0] == ShortReport && r[2] == 0x41)
        {
            _ = LinkChangedAsync(index, linked: (r[4] & 0x40) == 0);
            return;
        }
        // A battery feature's event (function 0, no software ID) when the level or charging changes.
        if (r[0] == LongReport && (r[3] & 0x0F) == 0 && _slots.TryGetValue(index, out var slot)
            && slot.BatteryIndex != 0 && r[2] == slot.BatteryIndex && r.Length >= 7)
        {
            var (battery, charging) = ParseBattery(slot.BatteryFeature, r);
            Publish(index, slot, true, battery, charging);
        }
    }

    private async Task LinkChangedAsync(byte index, bool linked)
    {
        if (!linked)
        {
            if (_slots.TryGetValue(index, out var gone) && gone.Last is { } last) Publish(index, gone, false, last.Battery, false);
            return;
        }
        try
        {
            await Task.Delay(1500, Stopping.Token); // it answers once it has settled
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (!_slots.TryGetValue(index, out var known)) await ProbeAsync(index);
        else if (!await ReadBatteryAsync(index, known)) Publish(index, known, true, null, false); // on, battery not told yet
    }

    private async Task ProbeAsync(byte index)
    {
        if (await FeatureIndexAsync(index, NameFeature) is not { } names) return; // nothing there, or switched off
        var slot = new Slot();
        if (await CallAsync(index, names, 0) is { } count)
        {
            int length = Math.Min((int)count[4], 64);
            var name = new StringBuilder();
            for (int offset = 0; offset < length; offset += 16)
            {
                if (await CallAsync(index, names, 1, (byte)offset) is not { } part) break;
                name.Append(Encoding.ASCII.GetString(part, 4, Math.Min(16, length - offset)));
            }
            slot.Name = name.ToString().TrimEnd('\0', ' ');
        }
        if (await CallAsync(index, names, 2) is { } type)
            slot.Kind = type[4] switch
            {
                0 or 2 => WirelessKind.Keyboard,
                3 or 4 or 5 => WirelessKind.Mouse,
                8 => WirelessKind.Headset,
                _ => WirelessKind.Other,
            };
        foreach (var feature in new[] { UnifiedBattery, BatteryStatus, BatteryVoltage })
        {
            if (await FeatureIndexAsync(index, feature) is not { } found) continue;
            slot.BatteryIndex = found;
            slot.BatteryFeature = feature;
            break;
        }
        _slots[index] = slot;
        await ReadBatteryAsync(index, slot);
    }

    /// <summary>False when there was no answer: asleep or switched off, which the 0x41 notifications tell.</summary>
    private async Task<bool> ReadBatteryAsync(byte index, Slot slot)
    {
        if (slot.BatteryIndex == 0) return true; // no battery (e.g. a mouse on its cable): nothing to show
        if (await CallAsync(index, slot.BatteryIndex, slot.BatteryFeature == UnifiedBattery ? (byte)1 : (byte)0) is not { } reply) return false;
        var (battery, charging) = ParseBattery(slot.BatteryFeature, reply);
        Publish(index, slot, true, battery, charging);
        return true;
    }

    private void Publish(byte index, Slot slot, bool connected, int? battery, bool charging)
    {
        string id = "logitech:" + (slot.Name.Length > 0 ? slot.Name : $"{hidpp.ProductId:X4}:{index}");
        var device = new WirelessDevice(id, slot.Name, slot.Kind, connected, battery ?? slot.Last?.Battery, connected && charging);
        slot.Last = device;
        Report(device);
    }

    /// <summary>Where the device keeps a feature (root feature 0x0000, function 0); null if it hasn't got it.</summary>
    private async Task<byte?> FeatureIndexAsync(byte index, ushort feature) =>
        await CallAsync(index, 0x00, 0, (byte)(feature >> 8), (byte)feature) is { } reply && reply[4] != 0 ? reply[4] : null;

    private Task<byte[]?> CallAsync(byte index, byte feature, byte function, params byte[] parameters)
    {
        byte call = (byte)(function << 4 | SoftwareId);
        var request = new byte[20];
        request[0] = LongReport;
        request[1] = index;
        request[2] = feature;
        request[3] = call;
        parameters.CopyTo(request, 4);
        return AskAsync(_channel!, request, r =>
        {
            if (r.Length < 5 || r[1] != index) return null;
            if (r[0] == LongReport && r[2] == feature && r[3] == call) return true;
            if (r[0] == LongReport && r[2] == 0xFF && r[3] == feature && r[4] == call) return false; // HID++ 2.0 error
            if (r[0] == ShortReport && r[2] == 0x8F && r[3] == feature && r[4] == call) return false; // HID++ 1.0 error
            return null;
        });
    }

    /// <summary>The level and charging from a battery feature's answer or event (its data starts at byte 4).</summary>
    private static (int? Percent, bool Charging) ParseBattery(ushort feature, byte[] r) => feature switch
    {
        // State of charge, level flags, charging status (1 charging, 2 slowly), external power.
        UnifiedBattery => (r[4] is > 0 and <= 100 ? r[4] : null, r[6] is 1 or 2),
        // Level, next level, status (1 recharging, 2 almost full, 4 slow recharge).
        BatteryStatus => (r[4] is > 0 and <= 100 ? r[4] : null, r[6] is 1 or 2 or 4),
        // Millivolts, then flags (bit 7: charging).
        BatteryVoltage => (FromVoltage(r[4] << 8 | r[5]), (r[6] & 0x80) != 0),
        _ => (null, false),
    };

    // A lithium cell's voltage against its charge (the curve Solaar uses for Logitech devices).
    private static readonly (int Millivolts, int Percent)[] Curve =
    [
        (4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50),
        (3778, 40), (3751, 30), (3717, 20), (3671, 10), (3646, 5), (3579, 2), (3500, 0),
    ];

    private static int? FromVoltage(int millivolts)
    {
        if (millivolts <= 0) return null;
        if (millivolts >= Curve[0].Millivolts) return 100;
        for (int i = 1; i < Curve.Length; i++)
        {
            var (high, low) = (Curve[i - 1], Curve[i]);
            if (millivolts < low.Millivolts) continue;
            return low.Percent + (millivolts - low.Millivolts) * (high.Percent - low.Percent) / (high.Millivolts - low.Millivolts);
        }
        return 0;
    }
}

/// <summary>
/// Corsair's newer wireless receivers (Bragi): property 0x0F is the battery in tenths of a percent, 0x10 its state
/// (1 charging). The headset is endpoint 0x09 behind the receiver; it doesn't answer while switched off.
/// </summary>
internal sealed class CorsairReceiver : WirelessReceiver
{
    private const byte Device = 0x09, Get = 0x02, BatteryLevel = 0x0F, BatteryState = 0x10;

    private readonly HidInterface _commands;
    private readonly HidInterface? _events;
    private readonly string _name;
    private readonly WirelessKind _kind;
    private HidChannel? _channel;
    private WirelessDevice? _last;
    private int _eventRefreshPending;

    public CorsairReceiver(HidInterface commands, HidInterface? events, Action<WirelessDevice> report) : base(report)
    {
        _commands = commands;
        _events = events;
        _name = Regex.Replace(commands.Product, @"\s*(Gaming\s+)?(Receiver|Dongle)\s*$", "", RegexOptions.IgnoreCase).Trim();
        _kind = KindFromName(_name);
    }

    public override async Task StartAsync()
    {
        _channel = HidChannel.Open(_commands, Received, Stopping.Token);
        // The receiver's event collection: something changed (switched on / off, the battery): ask again.
        if (_events is not null) HidChannel.Open(_events, _ => OnEvent(), Stopping.Token);
        await RefreshAsync();
    }

    public override async Task RefreshAsync()
    {
        if (_channel is null || await GetAsync(BatteryLevel) is not { } level) return;
        var (status, value) = level;
        bool connected = status == 0;
        int? battery = connected ? Math.Clamp((value + 5) / 10, 0, 100) : null;
        bool charging = connected && await GetAsync(BatteryState) is (0, 1);
        _last = new WirelessDevice("corsair:" + _name, _name, _kind, connected, battery ?? _last?.Battery, charging);
        Report(_last);
    }

    private async void OnEvent()
    {
        // Events come in bursts; one question once they've settled.
        if (Interlocked.Exchange(ref _eventRefreshPending, 1) == 1) return;
        try
        {
            await Task.Delay(1500, Stopping.Token);
            Interlocked.Exchange(ref _eventRefreshPending, 0);
            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<(int Status, int Value)?> GetAsync(byte property)
    {
        var reply = await AskAsync(_channel!, [_commands.OutputReportId, Device, Get, property, 0x00],
            r => r.Length > 5 && r[2] == Get ? true : null);
        return reply is null ? null : (reply[3], reply[4] | reply[5] << 8);
    }
}

/// <summary>
/// The SONiX 2.4 GHz receivers of Aula (F75 Max and others), Epomaker and AJAZZ keyboards: "20 01" asks for the
/// battery, the last byte of the report is the sum of the others, and the answer's fifth byte is the percent.
/// </summary>
internal sealed class SonixReceiver(HidInterface hid, Action<WirelessDevice> report) : WirelessReceiver(report)
{
    private readonly string _id = $"sonix:{hid.VendorId:X4}:{hid.ProductId:X4}";
    // The receiver names itself ("2.4G Dongle"), not the keyboard: then the kind names it.
    private readonly string _name = Regex.IsMatch(hid.Product, "dongle|receiver|2\\.4G", RegexOptions.IgnoreCase) ? "" : hid.Product;
    private HidChannel? _channel;
    private int? _lastBattery;

    public override async Task StartAsync()
    {
        _channel = HidChannel.Open(hid, Received, Stopping.Token);
        await RefreshAsync();
    }

    public override async Task RefreshAsync()
    {
        if (_channel is null) return;
        var request = new byte[hid.OutputLength];
        request[0] = hid.OutputReportId;
        request[1] = 0x20;
        request[2] = 0x01;
        request[^1] = (byte)request.Sum(b => b);
        if (await AskAsync(_channel, request, r => r.Length > 4 && r[1] == 0x20 && r[2] == 0x01 ? true : null) is not { } reply) return;
        bool connected = reply[4] is > 0 and <= 100;
        if (connected) _lastBattery = reply[4];
        Report(new WirelessDevice(_id, _name, WirelessKind.Keyboard, connected, _lastBattery, false));
    }
}
