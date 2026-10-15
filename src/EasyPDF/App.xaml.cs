using System.IO;
using System.Windows;
using System.Windows.Threading;
using EasyPDF.Pdf;
using EasyPDF.Resources;
using EasyPDF.Services;
using EasyPDF.Views;

namespace EasyPDF;

/// <summary>
/// The process: one per Windows session, any number of windows, one document per window. A file
/// handed over by a later launch opens in a new window here; a file that is already open brings
/// its window forward instead of opening twice.
/// </summary>
public partial class App : Application
{
    private const double CascadeStep = 28;

    private readonly string[] _files;
    private readonly string? _firstPath;
    private readonly Task<OpenResult>? _first;
    private readonly bool _claimInstance;
    private readonly Task<SettingsStore>? _settings;
    private bool _firstUsed;
    private bool _shuttingDown;

    public App()
        : this([], null, null, claimInstance: false)
    {
    }

    /// <param name="settings">The settings, being read in parallel with WPF's start-up.</param>
    public App(string[] files, string? firstPath, Task<OpenResult>? first, bool claimInstance, Task<SettingsStore>? settings = null)
    {
        _files = files;
        _firstPath = firstPath;
        _first = first;
        _claimInstance = claimInstance;
        _settings = settings;
    }

    /// <summary>
    /// Tests: the process is a test host, so the app's own start-up - settings, language, theme,
    /// the single-instance claim, the first window - is left to it. (WPF queues OnStartup from the
    /// Application constructor; a host that pumps the dispatcher itself would otherwise run it.)
    /// </summary>
    internal static bool HostedByTests { get; set; }

    /// <summary>Tests: windows open off screen and never take the foreground.</summary>
    internal static bool QuietWindows { get; set; }

    /// <summary>The open windows, most recently active first.</summary>
    public IEnumerable<MainWindow> DocumentWindows => Windows.OfType<MainWindow>();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (HostedByTests) return;

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => AppLog.Error("Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        StartupTrace.Mark("startup");
        SettingsStore settings;
        try
        {
            settings = _settings?.GetAwaiter().GetResult() ?? SettingsStore.Load();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Reading the settings failed.", ex);
            settings = SettingsStore.Load();
        }
        UiLanguage.Apply(settings.Settings.Language);
        StartupTrace.Mark("settings");
        ThemeService.Start(this, null);
        StartupTrace.Mark("theme");
        if (_claimInstance) SingleInstance.Claim(OnHandOver);

        // The recent and pinned files are looked for once the window is up - in the background,
        // so a share that does not answer holds up nothing - and the lists mark what is gone.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            FileAvailability.Check(SettingsStore.Current.Settings.Recent.Select(r => r.Path).ToList()));

        // Updates are looked for once everything else is done (the first check is half a minute on).
        Updater.Current = new Updater(Dispatcher);
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => Updater.Current?.Start());

        // Restarted by an update: the documents that were open, open again.
        if (_files.Length == 0 && ReopenAfterUpdate()) return;

        if (_files.Length == 0)
        {
            OpenEmptyWindow(null);
            return;
        }

        for (var i = 0; i < _files.Length; i++) Open(_files[i], null);
        // Nothing could be opened at all (every file missing): still show a window to say so in.
        if (!DocumentWindows.Any()) OpenEmptyWindow(null);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (HostedByTests)
        {
            base.OnExit(e);
            return;
        }
        _shuttingDown = true;
        SingleInstance.Stop();
        // A downloaded update installs now, silently: the next start is the new version.
        if (!_restartingToUpdate) Updater.Current?.InstallOnExit();
        Updater.Current?.Stop();
        SettingsStore.Current.Flush();
        AppLog.Shutdown();
        base.OnExit(e);
    }

    private bool _restartingToUpdate;

    /// <summary>
    /// Installs the downloaded update now: the documents open in every window are noted for the
    /// new version to open again, the installer starts (with its progress window, and it starts
    /// EasyPDF when it is done), and every window closes. Returns false, changing nothing, when the
    /// installer could not be started.
    /// </summary>
    public bool RestartToUpdate()
    {
        if (Updater.Current is not { State: UpdateState.Ready } updater) return false;
        var settings = SettingsStore.Current;
        settings.Settings.ReopenAfterUpdate = DocumentWindows.Select(w => w.DocumentPath).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
        settings.Save();
        settings.Flush();

        if (!updater.InstallNow())
        {
            settings.Settings.ReopenAfterUpdate = null;
            settings.Save();
            return false;
        }

        _restartingToUpdate = true;
        foreach (var window in DocumentWindows.ToArray()) window.Close();
        if (!HostedByTests) Shutdown();
        return true;
    }

    /// <summary>Opens the documents noted by <see cref="RestartToUpdate"/>, once. Returns whether a window opened.</summary>
    internal bool ReopenAfterUpdate()
    {
        var settings = SettingsStore.Current;
        if (settings.Settings.ReopenAfterUpdate is not { Count: > 0 } files) return false;
        settings.Settings.ReopenAfterUpdate = null;
        settings.Save();
        foreach (var file in files) Open(file, null);
        return DocumentWindows.Any();
    }

    /// <summary>Another launch handed over its command line. Runs on the pipe thread.</summary>
    private bool OnHandOver(string currentDirectory, string[] args)
    {
        try
        {
            return Dispatcher.Invoke(() =>
            {
                if (_shuttingDown || Dispatcher.HasShutdownStarted) return false;

                var files = args.Where(a => a.Length > 0 && !a.StartsWith("--", StringComparison.Ordinal))
                    .Select(a => Path.IsPathFullyQualified(a) ? a : Path.Combine(currentDirectory, a))
                    .ToArray();

                if (files.Length == 0)
                {
                    OpenEmptyWindow(null);
                    return true;
                }

                foreach (var file in files) Open(file, null);
                return true;
            });
        }
        catch (Exception ex) when (ex is TaskCanceledException or InvalidOperationException)
        {
            return false;
        }
    }

    // ===================================================================== windows

    public MainWindow OpenEmptyWindow(MainWindow? from)
    {
        var window = CreateWindow(from);
        window.ShowStartPage();
        window.Show();
        window.Activate();
        return window;
    }

    /// <summary>
    /// Opens a PDF: brings forward the window that already shows it, or uses
    /// <paramref name="target"/>, or an empty window, or a new one. <paramref name="opened"/> runs
    /// once the document is showing.
    /// </summary>
    public void Open(string path, MainWindow? target, Action<MainWindow>? opened = null)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            AppLog.Warn($"Not a usable path: {path}");
            return;
        }

        // A window that shows the file, or is still opening it, is the one: what was to follow the
        // opening waits for it there.
        var existing = DocumentWindows.FirstOrDefault(w => w.Shows(fullPath));
        if (existing is not null)
        {
            BringForward(existing);
            if (opened is not null) existing.WhenShowing(fullPath, opened);
            return;
        }

        var window = target is { IsLoaded: true }
            ? target
            : DocumentWindows.FirstOrDefault(w => !w.HasDocument && !w.IsBusyOpening) ?? CreateWindow(target ?? ActiveDocumentWindow());
        Task<OpenResult>? preopened = null;
        if (!_firstUsed && _first is not null && string.Equals(fullPath, _firstPath, StringComparison.OrdinalIgnoreCase))
        {
            _firstUsed = true;
            preopened = _first;
        }

        // Opening starts before the window is first laid out: if the document is ready (it was
        // opened while WPF started), the very first frame shows it - not an empty window first.
        _ = OpenIn(window, fullPath, preopened, opened);
        if (!window.IsVisible)
        {
            window.Show();
            StartupTrace.Mark("window-shown");
        }
        BringForward(window);
    }

    private static async Task OpenIn(MainWindow window, string path, Task<OpenResult>? preopened, Action<MainWindow>? opened)
    {
        try
        {
            if (await window.OpenAsync(path, null, preopened).ConfigureAwait(true)) opened?.Invoke(window);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Opening {path} failed.", ex);
        }
    }

    private MainWindow? ActiveDocumentWindow() =>
        DocumentWindows.FirstOrDefault(w => w.IsActive) ?? DocumentWindows.LastOrDefault();

    private static void BringForward(Window window)
    {
        if (QuietWindows) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        // Windows only lets a process that already has the foreground take it; flicking Topmost
        // brings the window up when the hand-over came from another process.
        window.Topmost = true;
        window.Topmost = false;
    }

    private MainWindow CreateWindow(MainWindow? from)
    {
        StartupTrace.Mark("window-creating");
        var window = new MainWindow(this);
        StartupTrace.Mark("window-created");
        if (QuietWindows)
        {
            window.ShowActivated = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -24000;
            window.Top = -24000;
            window.Width = 1100;
            window.Height = 820;
        }
        else
        {
            Place(window, from);
        }
        window.Closed += (_, _) =>
        {
            if (!DocumentWindows.Any(w => !ReferenceEquals(w, window)))
            {
                _shuttingDown = true;
                SingleInstance.Stop();
            }
        };
        return window;
    }

    /// <summary>The first window where the last one was; later ones step down and right from the window they came from.</summary>
    private void Place(MainWindow window, MainWindow? from)
    {
        var saved = SettingsStore.Current.Settings.Window;
        Rect bounds;
        var maximized = false;

        if (from is { IsLoaded: true } && from.WindowState == WindowState.Normal)
        {
            bounds = new Rect(from.Left + CascadeStep, from.Top + CascadeStep, from.Width, from.Height);
        }
        else if (saved is not null)
        {
            bounds = new Rect(saved.Left, saved.Top, saved.Width, saved.Height);
            maximized = saved.Maximized && DocumentWindows.Count() <= 1;
            if (DocumentWindows.Count() > 1) bounds.Offset(CascadeStep, CascadeStep);
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        // Only somewhere the user can see: the monitors may have changed since.
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        bounds.Width = Math.Min(bounds.Width, screen.Width);
        bounds.Height = Math.Min(bounds.Height, screen.Height);
        if (bounds.Right > screen.Right) bounds.X = Math.Max(screen.Left, screen.Right - bounds.Width);
        if (bounds.Bottom > screen.Bottom) bounds.Y = Math.Max(screen.Top, screen.Bottom - bounds.Height);
        if (bounds.Left < screen.Left || bounds.Top < screen.Top)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Width = bounds.Width;
            window.Height = bounds.Height;
            return;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = bounds.Left;
        window.Top = bounds.Top;
        window.Width = bounds.Width;
        window.Height = bounds.Height;
        if (maximized) window.WindowState = WindowState.Maximized;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
        try
        {
            var dialog = new NoticeDialog(Strings.App_Error_Title, UiLanguage.Format(Strings.App_Error, e.Exception.Message, AppLog.LogPath));
            if (DocumentWindows.FirstOrDefault(w => w.IsVisible) is { } owner) dialog.Owner = owner;
            dialog.ShowDialog();
        }
        catch
        {
            // The error could not even be shown; it is in the log.
        }
    }
}
