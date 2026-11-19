using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using ZeiLand.Native;
using ZeiLand.Services;

namespace ZeiLand;

// A download finishing, a USB drive plugged in, Caps Lock / Num Lock pressed. The first two show a card with what
// to do (open; show in its folder or eject) that stays while the pointer is on it; the lock keys a short notice.
public partial class IslandWindow
{
    private const string DriveGlyphText = "", WarningGlyphText = "";
    // The keyboard symbols for Caps Lock and Num Lock (ISO 9995-7), which the icon font doesn't have.
    private const string CapsLockGlyphText = "⇪", NumLockGlyphText = "⇭";
    private static readonly FontFamily SymbolFont = new("Segoe UI Symbol");

    private DownloadWatcher? _downloads;
    private DriveWatcher? _drives;
    private LockKeys? _lockKeys;

    // What the card's two buttons (and a click on its icon) do, and the drive it shows, if it shows one.
    private Action? _actionOpen, _actionSecond;
    private string? _actionDrive;

    internal void AttachExtras(DownloadWatcher downloads, DriveWatcher drives, LockKeys lockKeys)
    {
        _downloads = downloads;
        _drives = drives;
        _lockKeys = lockKeys;
        downloads.Finished += OnDownloadFinished;
        drives.Arrived += OnDriveArrived;
        drives.Removed += OnDriveRemoved;
        lockKeys.Changed += OnLockKeyChanged;
        ActionTarget.MouseLeftButtonUp += (_, _) => _actionOpen?.Invoke();
        ActionOpenButton.Click += (_, _) => _actionOpen?.Invoke();
        ActionSecondButton.Click += (_, _) => _actionSecond?.Invoke();
        ApplyExtras();
    }

    /// <summary>Each watcher runs only while its notices are on.</summary>
    private void ApplyExtras()
    {
        if (_downloads is not null) _downloads.Enabled = _settings.ShowDownloads;
        if (_drives is not null) _drives.Enabled = _settings.ShowDrives;
        if (_lockKeys is not null) _lockKeys.Enabled = _settings.ShowLockKeys;
    }

    private bool CanShowCard => !_expanded && !_timerDone && _transient is not (View.Screenshot or View.Assistant);

    // ---- Downloads ---------------------------------------------------------------------------

    private void OnDownloadFinished(string path)
    {
        if (!CanShowCard) return;
        long size = 0;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        ShowCard(AppIcons.ForFile(path, 64), "", Path.GetFileName(path), $"{Loc.T("DownloadFinished")} · {FormatSize(size)}",
            Loc.T("Folder"), Loc.T("ShowInFolder"), null,
            open: () =>
            {
                if (OpenWithShell($"\"{path}\"", path)) EndTransient();
            },
            second: () =>
            {
                if (OpenWithShell($"/select,\"{path}\"", path)) EndTransient();
            });
    }

    /// <summary>Through Explorer, so it opens as the user even when the island runs as administrator.</summary>
    private bool OpenWithShell(string arguments, string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            ShowNotice(WarningGlyphText, Loc.T("FileGone"), 2500);
            return false;
        }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = false })?.Dispose();
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    // ---- Drives ------------------------------------------------------------------------------

    private void OnDriveArrived(DriveArrival drive)
    {
        if (!CanShowCard) return;
        string name = $"{(drive.Label.Length > 0 ? drive.Label : Loc.T("UsbDrive"))} ({drive.Root[..2]})";
        ShowCard(null, DriveGlyphText, name, Loc.F("DriveFree", FormatSize(drive.Free), FormatSize(drive.Total)),
            drive.CanEject ? Loc.T("Eject") : null, null, drive.Root,
            open: () =>
            {
                if (OpenWithShell($"\"{drive.Root}\"", drive.Root)) EndTransient();
            },
            second: () => EjectDrive(drive.Root, name));
    }

    private void OnDriveRemoved(string root)
    {
        if (_transient == View.Action && _actionDrive == root) EndTransient();
    }

    private async void EjectDrive(string root, string name)
    {
        ActionOpenButton.IsEnabled = ActionSecondButton.IsEnabled = false;
        ActionDetail.Text = Loc.T("Ejecting");
        _transientTimer.Stop(); // stays until there is an answer
        var result = await Task.Run(() => DriveNative.Eject(root));
        if (result == EjectResult.Ejected) ShowNotice(CheckGlyphText, Loc.F("DriveEjected", name), 3000);
        else ShowNotice(WarningGlyphText, Loc.T(result == EjectResult.InUse ? "DriveInUse" : "DriveEjectFailed"), 4000);
    }

    // ---- The card ----------------------------------------------------------------------------

    /// <summary>The card: an icon (or glyph), a title, a detail, "Open" and, if there's a label for it, a second button.</summary>
    private void ShowCard(ImageSource? icon, string glyph, string title, string detail, string? secondLabel, string? secondTip,
        string? drive, Action open, Action second)
    {
        ActionIcon.Source = icon;
        ActionGlyph.Text = icon is null ? glyph : "";
        ActionTitle.Text = title;
        ActionTitle.ToolTip = title;
        ActionDetail.Text = detail;
        ActionOpenButton.Content = Loc.T("Open");
        ActionSecondButton.Content = secondLabel;
        ActionSecondButton.ToolTip = secondTip;
        ActionSecondButton.Visibility = secondLabel is null ? Visibility.Collapsed : Visibility.Visible;
        ActionOpenButton.IsEnabled = ActionSecondButton.IsEnabled = true;
        _actionOpen = open;
        _actionSecond = second;
        _actionDrive = drive;
        ShowTransient(View.Action, 6000);
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{(bytes / 1073741824.0).ToString("0.0", Loc.Culture)} GB"
        : bytes >= 1L << 20 ? $"{(bytes / 1048576.0).ToString("0.0", Loc.Culture)} MB"
        : $"{Math.Max(1, (bytes + 1023) >> 10)} KB";

    // ---- Caps Lock, Num Lock -----------------------------------------------------------------

    private void OnLockKeyChanged(int key, bool on)
    {
        if (_expanded || _timerDone || IsLingering) return;
        bool caps = key == LockKeys.CapsLock;
        string text = Loc.T(caps ? on ? "CapsLockOn" : "CapsLockOff" : on ? "NumLockOn" : "NumLockOff");
        ShowNotice(caps ? CapsLockGlyphText : NumLockGlyphText, text, 1500, on ? Brushes.White : TertiaryBrush, SymbolFont);
    }
}
