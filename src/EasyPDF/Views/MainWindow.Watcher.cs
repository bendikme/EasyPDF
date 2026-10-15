using System.IO;
using System.Windows.Threading;
using EasyPDF.Pdf;
using EasyPDF.Resources;
using EasyPDF.Services;

namespace EasyPDF.Views;

/// <summary>
/// Keeps the window showing the file as it is on disk. The file is never locked, so whatever
/// writes it - a LaTeX build, a report generator, a sync client - can replace it while it is
/// open; when it changes, the new version is opened at the same place. A file in the middle of
/// being written fails to open, so the reload waits for it to settle and tries a few times.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(350);
    private const int ReloadAttempts = 8;

    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _reloadTimer;
    private string? _watchedPath;
    private int _watchGeneration;
    private int _reloadAttempt;
    private bool _reloading;
    private bool _checkingChange;

    private void InitializeWatcher()
    {
        _reloadTimer = new DispatcherTimer(SettleTime, DispatcherPriority.Background, (_, _) => OnReloadTimer(), Dispatcher);
        _reloadTimer.Stop();
    }

    /// <summary>
    /// Starts watching the file. Setting a watch up asks the folder's server, and taking one down
    /// tells it - on a network share that is slow or gone, either can take long - so both happen
    /// off the UI thread; a watch that is no longer wanted when it is ready is taken down again.
    /// </summary>
    private void WatchFile(string path)
    {
        if (string.Equals(_watchedPath, path, StringComparison.OrdinalIgnoreCase)) return;
        StopWatching();
        _watchedPath = path;
        var generation = _watchGeneration;

        _ = Task.Run(() =>
        {
            FileSystemWatcher? watcher = null;
            try
            {
                var folder = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

                watcher = new FileSystemWatcher(folder, Path.GetFileName(path))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                    IncludeSubdirectories = false,
                };
                watcher.Changed += OnFileEvent;
                watcher.Created += OnFileEvent;
                watcher.Renamed += OnFileEvent;
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                // A folder that cannot be watched (some network shares) just means no automatic reload.
                AppLog.Info($"Not watching {path} for changes: {ex.Message}");
                if (watcher is not null) Release(watcher);
                return;
            }

            Dispatcher.BeginInvoke(() =>
            {
                if (_closed || generation != _watchGeneration) Release(watcher);
                else _watcher = watcher;
            });
        });
    }

    private void StopWatching()
    {
        _watchGeneration++;
        _reloadTimer?.Stop();
        var watcher = _watcher;
        _watcher = null;
        _watchedPath = null;
        if (watcher is not null) _ = Task.Run(() => Release(watcher));
    }

    private void Release(FileSystemWatcher watcher)
    {
        try
        {
            watcher.Changed -= OnFileEvent;
            watcher.Created -= OnFileEvent;
            watcher.Renamed -= OnFileEvent;
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Info($"Stopping a file watch failed: {ex.Message}");
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (e is RenamedEventArgs renamed && !string.Equals(renamed.FullPath, _watchedPath, StringComparison.OrdinalIgnoreCase)) return;

        // A burst of events while the file is written restarts the wait each time.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_closed || _document is null) return;
            _reloadAttempt = 0;
            if (_reloadTimer is null) return;
            _reloadTimer.Stop();
            _reloadTimer.Interval = SettleTime;
            _reloadTimer.Start();
        });
    }

    private async void OnReloadTimer()
    {
        _reloadTimer?.Stop();
        if (_reloading || _checkingChange || _closed || _document is not { } current) return;

        // Nothing actually changed (a touch, or an event for a write that was undone)? Asked off
        // the UI thread: the file may be on a share that is slow to answer.
        bool unchanged;
        _checkingChange = true;
        try
        {
            var (path, length, written, failed) = (current.FilePath, current.FileLength, current.FileWriteTimeUtc, current.FileReadFailed);
            unchanged = await Task.Run(() =>
            {
                try
                {
                    var info = new FileInfo(path);
                    return info.Exists && info.Length == length && info.LastWriteTimeUtc == written && !failed;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return false;
                }
            }).ConfigureAwait(true);
        }
        finally
        {
            _checkingChange = false;
        }
        if (unchanged || _closed || !ReferenceEquals(_document, current)) return;

        await Reload(silent: true).ConfigureAwait(true);
    }

    /// <summary>Opens the file again, at the same place. Silent reloads retry while the file is being written.</summary>
    internal async Task Reload(bool silent = false)
    {
        if (_reloading || _document is not { } current) return;
        _reloading = true;
        try
        {
            var state = View.CaptureState();
            var result = await PdfDocument.OpenAsync(current.FilePath, _password).ConfigureAwait(true);
            if (_closed || !ReferenceEquals(_document, current))
            {
                if (result.Document is { } stale) await stale.CloseAsync().ConfigureAwait(true);
                return;
            }

            if (result.Document is { } fresh)
            {
                _reloadAttempt = 0;
                ShowDocument(fresh, state, reload: true);
                return;
            }

            if (silent && _reloadAttempt++ < ReloadAttempts)
            {
                // Still being written, or gone for a moment while it is replaced: wait and try again.
                _reloadTimer!.Interval = TimeSpan.FromMilliseconds(400 + (_reloadAttempt * 250));
                _reloadTimer.Start();
                return;
            }

            _reloadTimer!.Interval = SettleTime;
            if (!silent) ShowNotice(Strings.Reload_Failed_Title, DescribeFailure(result, current.FilePath));
        }
        finally
        {
            _reloading = false;
        }
    }
}
