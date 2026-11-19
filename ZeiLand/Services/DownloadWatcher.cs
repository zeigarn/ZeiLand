using System.IO;
using System.Windows.Threading;
using ZeiLand.Native;

namespace ZeiLand.Services;

/// <summary>
/// Downloads finishing in the Downloads folder. Browsers write a download under a temporary name (Chrome, Edge,
/// Brave, Opera, Vivaldi: ".crdownload"; Firefox: ".part") and give it its own name once it is complete: that
/// rename is the moment. Windows reports it (FileSystemWatcher, i.e. ReadDirectoryChangesW), so nothing is polled
/// and nothing runs while nothing is downloaded. Files saved there any other way (an app saving a document) are
/// left alone.
/// </summary>
internal sealed class DownloadWatcher(Dispatcher dispatcher) : IDisposable
{
    private static readonly string[] Partial = [".crdownload", ".part", ".partial", ".opdownload"];

    private FileSystemWatcher? _watcher;

    /// <summary>A download finished: the file's full path. Raised on the dispatcher.</summary>
    public event Action<string>? Finished;

    public bool Enabled
    {
        get => _watcher is not null;
        set
        {
            if (value == Enabled) return;
            if (value) Start();
            else Stop();
        }
    }

    public void Dispose() => Stop();

    private void Start()
    {
        if (KnownFolders.Get(KnownFolders.Downloads) is not { } folder || !Directory.Exists(folder)) return;
        try
        {
            _watcher = new FileSystemWatcher(folder) { NotifyFilter = NotifyFilters.FileName };
            _watcher.Renamed += OnRenamed;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Stop();
        }
    }

    private void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (!IsPartial(e.OldName) || IsPartial(e.Name)) return;
        string path = e.FullPath;
        dispatcher.InvokeAsync(() =>
        {
            if (_watcher is not null) Finished?.Invoke(path);
        });
    }

    private static bool IsPartial(string? name) =>
        name is not null && Partial.Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
}
