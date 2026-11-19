using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ZeiLand.Services;

/// <summary>
/// Caps Lock and Num Lock pressed: whether it is now on or off. Windows hands this window the keyboard's raw input
/// (RIDEV_INPUTSINK, without taking it from anyone); of each key only its code is looked at, and only these two
/// keys are acted on. Nothing is polled and nothing is kept.
/// </summary>
internal sealed class LockKeys : IDisposable
{
    public const int CapsLock = 0x14, NumLock = 0x90;

    private const int WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003;
    private const int RIM_TYPEKEYBOARD = 1;
    private const int RIDEV_REMOVE = 0x1, RIDEV_INPUTSINK = 0x100;
    private const ushort RI_KEY_BREAK = 1;
    private const ushort GenericDesktop = 0x01, Keyboard = 0x06;

    private static readonly int HeaderSize = Marshal.SizeOf<RAWINPUTHEADER>();

    private readonly HwndSource _host;
    private readonly HwndSourceHook _hook;
    private readonly DispatcherTimer _settle;
    private readonly byte[] _input = new byte[64];
    private readonly HashSet<int> _released = [];
    private bool _enabled;

    public LockKeys(HwndSource host)
    {
        _host = host;
        _hook = WndProc;
        // The key's new state, read once Windows has taken the press in.
        _settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            foreach (int key in _released) Changed?.Invoke(key, (GetKeyState(key) & 1) != 0);
            _released.Clear();
        };
    }

    /// <summary>The key (<see cref="CapsLock"/> or <see cref="NumLock"/>) and whether it is on now.</summary>
    public event Action<int, bool>? Changed;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (value == _enabled) return;
            var device = new RAWINPUTDEVICE
            {
                UsagePage = GenericDesktop,
                Usage = Keyboard,
                Flags = value ? RIDEV_INPUTSINK : RIDEV_REMOVE,
                Target = value ? _host.Handle : IntPtr.Zero,
            };
            if (!RegisterRawInputDevices([device], 1, Marshal.SizeOf<RAWINPUTDEVICE>()) && value) return;
            _enabled = value;
            if (value) _host.AddHook(_hook);
            else _host.RemoveHook(_hook);
        }
    }

    public void Dispose() => Enabled = false;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_INPUT) return IntPtr.Zero;
        int size = _input.Length;
        // Left to DefWindowProc afterwards (not handled), which frees the input.
        if (GetRawInputData(lParam, RID_INPUT, _input, ref size, HeaderSize) <= 0
            || BitConverter.ToInt32(_input, 0) != RIM_TYPEKEYBOARD) return IntPtr.Zero;
        ushort flags = BitConverter.ToUInt16(_input, HeaderSize + 2), key = BitConverter.ToUInt16(_input, HeaderSize + 6);
        if (key is not (CapsLock or NumLock) || (flags & RI_KEY_BREAK) == 0) return IntPtr.Zero;
        _released.Add(key);
        _settle.Stop();
        _settle.Start();
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort UsagePage;
        public ushort Usage;
        public int Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public int Type;
        public int Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, int count, int size);

    [DllImport("user32.dll")]
    private static extern int GetRawInputData(IntPtr input, uint command, byte[] data, ref int size, int headerSize);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);
}
