using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using EasyPDF.Pdf;
using EasyPDF.Resources;
using EasyPDF.Services;
using EasyPDF.Viewer;

namespace EasyPDF.Views;

/// <summary>A recent document on the start page.</summary>
/// <summary>A row of the recent list: <paramref name="When"/> is when it was opened, or why it cannot be (<paramref name="IsAway"/>).</summary>
public sealed record RecentItem(string Path, string Name, string Folder, string When, bool Pinned, bool IsAway)
{
    public string PinText => Pinned ? Strings.Main_Recent_Unpin : Strings.Main_Recent_Pin;

    /// <summary>A file that is not there is shown faded.</summary>
    public double Fade => IsAway ? 0.55 : 1;
}

/// <summary>
/// One window, one document. The window owns the document's lifetime: it opens it, hands it to
/// the page view and the sidebar, watches the file and reloads it when it changes, and closes it
/// when the window closes or another document takes its place.
/// </summary>
public partial class MainWindow : Window
{
    private readonly App _app;
    private readonly DispatcherTimer _loadingDelay;
    private PdfDocument? _document;
    private string? _loadingPath;
    private int _openGeneration;
    private bool _closed;

    public MainWindow(App app)
    {
        _app = app;
        StartupTrace.Mark("ctor");
        InitializeComponent();
        StartupTrace.Mark("ctor-xaml");

        _loadingDelay = new DispatcherTimer(TimeSpan.FromMilliseconds(180), DispatcherPriority.Normal, (_, _) =>
        {
            _loadingDelay!.Stop();
            if (_loadingPath is not null) LoadingText.Visibility = Visibility.Visible;
        }, Dispatcher);
        _loadingDelay.Stop();

        View.ViewChanged += (_, _) => UpdateViewState();
        if (StartupTrace.Enabled)
        {
            View.ScreenRendered += (_, _) =>
            {
                if (!StartupTrace.FirstScreen()) return;
                if (StartupTrace.ExitAfterFirstScreen) Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => System.Windows.Application.Current.Shutdown());
            };
            ContentRendered += (_, _) => StartupTrace.Mark("window-rendered");
        }
        View.LinkHovered += OnLinkHovered;
        View.LinkActivated += OnLinkActivated;
        View.History.Changed += (_, _) => UpdateViewState();
        InitializeTrail();

        SettingsStore.Current.RecentChanged += OnRecentChanged;
        FileAvailability.Changed += OnAvailabilityChanged;

        InitializeSidebar();
        InitializeWatcher();
        UpdateViewState();
        StartupTrace.Mark("ctor-done");
    }

    /// <summary>The document's file, or the one being opened; null for an empty window.</summary>
    public string? DocumentPath => _document?.FilePath ?? _loadingPath;

    public bool HasDocument => _document is not null;

    public bool IsBusyOpening => _loadingPath is not null;

    /// <summary>Whether this window shows <paramref name="fullPath"/>, or is opening it.</summary>
    internal bool Shows(string fullPath) =>
        string.Equals(_loadingPath, fullPath, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_document?.FilePath, fullPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>What to do once the file being opened is showing (a jump into it, say); dropped if it does not open.</summary>
    private List<Action<MainWindow>>? _whenOpened;

    /// <summary>Runs <paramref name="action"/> once this window shows <paramref name="fullPath"/>: now, or when the opening finishes.</summary>
    internal void WhenShowing(string fullPath, Action<MainWindow> action)
    {
        if (string.Equals(_loadingPath, fullPath, StringComparison.OrdinalIgnoreCase)) (_whenOpened ??= new List<Action<MainWindow>>()).Add(action);
        else if (string.Equals(_document?.FilePath, fullPath, StringComparison.OrdinalIgnoreCase)) action(this);
    }

    internal PdfDocument? Document => _document;

    internal PdfView PageView => View;

    // ===================================================================== opening

    /// <summary>
    /// Opens a PDF in this window, replacing what it shows. <paramref name="opening"/> is an open
    /// already under way (started before the window existed), <paramref name="state"/> where to
    /// start reading. Returns whether the document is now showing.
    /// </summary>
    public async Task<bool> OpenAsync(string path, ViewState? state = null, Task<OpenResult>? opening = null)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ShowNotice(Strings.Open_Failed_Title, UiLanguage.Format(Strings.Open_NotFound, path));
            return false;
        }

        var generation = ++_openGeneration;
        if (!string.Equals(_loadingPath, fullPath, StringComparison.OrdinalIgnoreCase)) _whenOpened = null;
        _loadingPath = fullPath;
        _loadingDelay.Stop();
        _loadingDelay.Start();
        if (_document is null) StartPage.Visibility = Visibility.Collapsed;

        try
        {
            var result = await (opening ?? PdfDocument.OpenAsync(fullPath)).ConfigureAwait(true);

            // A password is asked for until it is right or the user gives up.
            string? password = null;
            while (result.Failure is OpenFailure.PasswordRequired or OpenFailure.WrongPassword && !_closed)
            {
                if (generation != _openGeneration) return false;
                password = AskPassword(Path.GetFileName(fullPath), result.Failure == OpenFailure.WrongPassword);
                if (password is null) break;
                result = await PdfDocument.OpenAsync(fullPath, password).ConfigureAwait(true);
            }

            if (generation != _openGeneration || _closed)
            {
                if (result.Document is { } stale) await stale.CloseAsync().ConfigureAwait(true);
                return false;
            }

            if (result.Document is not { } document)
            {
                if (result.Failure is not (OpenFailure.PasswordRequired or OpenFailure.WrongPassword))
                {
                    ShowNotice(Strings.Open_Failed_Title, DescribeFailure(result, fullPath));
                    if (result.Failure == OpenFailure.NotFound)
                    {
                        FileAvailability.Report(fullPath, Availability.Missing);
                        SettingsStore.Current.ForgetUnlessPinned(fullPath);
                    }
                }
                return false;
            }

            _password = password;
            FileAvailability.Report(fullPath, Availability.Present);
            state ??= RememberedState(fullPath, document) ?? new ViewState(0, 0, DefaultGrid is { PerSheet: > 1 } ? ZoomMode.FitPage : ZoomMode.FitWidth, 1, 0, DefaultGrid);
            StartupTrace.Mark("document-open");
            ShowDocument(document, state);

            var waiting = _whenOpened;
            _whenOpened = null;
            if (waiting is not null)
            {
                foreach (var action in waiting)
                {
                    try
                    {
                        action(this);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn($"What was to follow opening {fullPath} failed.", ex);
                    }
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Opening {fullPath} failed.", ex);
            if (generation == _openGeneration) ShowNotice(Strings.Open_Failed_Title, UiLanguage.Format(Strings.Open_Unknown, Path.GetFileName(fullPath), ex.Message));
            return false;
        }
        finally
        {
            if (generation == _openGeneration)
            {
                _whenOpened = null;
                _loadingPath = null;
                _loadingDelay.Stop();
                LoadingText.Visibility = Visibility.Collapsed;
                if (_document is null) ShowStartPage();
            }
        }
    }

    /// <summary>The password the document was opened with, kept for reloading it when the file changes.</summary>
    private string? _password;

    private static ViewState? RememberedState(string path, PdfDocument document)
    {
        if (SettingsStore.Current.FindRecent(path) is not { } recent) return null;
        var mode = recent.ZoomMode switch
        {
            "FitPage" => ZoomMode.FitPage,
            "Custom" => ZoomMode.Custom,
            _ => ZoomMode.FitWidth,
        };
        var page = Math.Clamp(recent.Page, 0, document.PageCount - 1);
        return new ViewState(page, recent.PageOffset, mode, recent.Zoom, recent.Rotation, PageGrid.Parse(recent.Grid));
    }

    internal static string DescribeFailure(OpenResult result, string path)
    {
        var name = Path.GetFileName(path);
        return result.Failure switch
        {
            OpenFailure.NotFound => UiLanguage.Format(Strings.Open_NotFound, path),
            OpenFailure.AccessDenied => UiLanguage.Format(Strings.Open_AccessDenied, name),
            OpenFailure.NotPdf => UiLanguage.Format(Strings.Open_NotPdf, name),
            OpenFailure.Damaged => UiLanguage.Format(Strings.Open_Damaged, name),
            OpenFailure.UnsupportedSecurity => UiLanguage.Format(Strings.Open_UnsupportedSecurity, name),
            OpenFailure.Empty => UiLanguage.Format(Strings.Open_Empty, name),
            _ => UiLanguage.Format(Strings.Open_Unknown, name, result.Detail ?? string.Empty),
        };
    }

    private void ShowDocument(PdfDocument document, ViewState? state, bool reload = false)
    {
        var previous = _document;
        if (previous is not null && !reload) RememberDocument();

        EndSearch(clearText: !reload);
        _document = document;
        if (!reload)
        {
            StopPageIndicator();
            _trailDismissed = false;
        }
        _sections = [];
        View.SetDocument(document, state, keepHistory: reload);
        _ = previous?.CloseAsync();

        Title = $"{document.FileName} - {AppIdentity.Name}";
        BrandTitle.Text = document.FileName;
        StartPage.Visibility = Visibility.Collapsed;
        DocumentTools.IsEnabled = true;
        DocumentTools.Visibility = Visibility.Visible;
        ToolbarSeparator.Visibility = Visibility.Visible;
        SearchBox.IsEnabled = true;
        FitHeader();

        LoadSidebar(document);
        WatchFile(document.FilePath);
        if (!reload) RememberDocument();
        RefreshTrail();
        UpdateViewState();
        View.Focus();

        if (reload && SearchBox.Text.Length > 0) StartSearch(immediate: true, reveal: false);
    }

    /// <summary>Back to the start page: no document.</summary>
    public void CloseDocument()
    {
        if (_document is not { } document) return;
        RememberDocument();
        EndSearch(clearText: true);
        _document = null;
        _password = null;
        StopPageIndicator();
        _sections = [];
        View.SetDocument(null);
        RefreshTrail();
        UnloadSidebar();
        StopWatching();
        _ = document.CloseAsync();
        ShowStartPage();
        UpdateViewState();
    }

    /// <summary>Records where the document was left, so it opens there next time.</summary>
    private void RememberDocument()
    {
        if (_document is not { } document || View.CaptureState() is not { } state) return;
        SettingsStore.Current.Remember(new RecentFile
        {
            Path = document.FilePath,
            OpenedUtc = DateTime.UtcNow,
            Page = state.Page,
            PageOffset = state.PageOffset,
            ZoomMode = state.Mode.ToString(),
            Zoom = state.Zoom,
            Rotation = state.Rotation,
            Grid = state.Grid?.ToString(),
            SinglePage = state.Grid is not null,
        });
    }

    /// <summary>The start page, for a window that has nothing to open. A window opening a file never builds it.</summary>
    internal void ShowStartPage()
    {
        Title = AppIdentity.Name;
        BrandTitle.Text = AppIdentity.Name;
        DocumentTools.IsEnabled = false;
        DocumentTools.Visibility = Visibility.Collapsed;
        ToolbarSeparator.Visibility = Visibility.Collapsed;
        SearchBox.IsEnabled = false;
        FitHeader();
        PageBox.Text = string.Empty;
        PageCountText.Text = string.Empty;
        ZoomText.Text = "100%";
        RefreshRecent();
        StartPage.Visibility = Visibility.Visible;
        SetSidebar(false, remember: false);
    }

    /// <summary>Unpinned files on the start page; pinned ones are all shown, above them.</summary>
    private const int RecentShown = 10;

    private void RefreshRecent()
    {
        var items = RecentItems(RecentShown);
        RecentList.ItemsSource = ViewOf(items);
        RecentCard.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearRecentButton.Visibility = items.Any(i => !i.Pinned) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// The recent list as shown: pinned files, then the most recent others, each marked when it is
    /// known not to be there. Whether it is there is asked in the background (never here: a
    /// network share can take half a minute to say), and the list is shown again as answers come.
    /// </summary>
    internal List<RecentItem> RecentItems(int unpinned)
    {
        var now = DateTime.UtcNow;
        var ordered = SettingsStore.Current.Ordered();
        var shown = ordered.Where(r => r.Pinned).Concat(ordered.Where(r => !r.Pinned).Take(unpinned)).ToList();
        FileAvailability.Check(shown.Select(r => r.Path));

        var unknown = false;
        var items = new List<RecentItem>(shown.Count);
        foreach (var r in shown)
        {
            var availability = FileAvailability.Get(r.Path);
            unknown |= availability == Availability.Unknown;
            var away = availability is Availability.Missing or Availability.Unavailable;
            items.Add(new RecentItem(
                r.Path,
                Path.GetFileName(r.Path),
                Path.GetDirectoryName(r.Path) ?? string.Empty,
                availability switch
                {
                    Availability.Missing => Strings.Main_Recent_Missing,
                    Availability.Unavailable => Strings.Main_Recent_Unavailable,
                    _ => RelativeTimeConverter.Describe(r.OpenedUtc, now),
                },
                r.Pinned,
                away));
        }

        // A drive or share that has not answered by then is shown as unavailable.
        if (unknown) ScheduleAvailabilityRefresh(FileAvailability.Patience + TimeSpan.FromMilliseconds(100));
        return items;
    }

    private DispatcherTimer? _availabilityTimer;
    private bool _availabilityRefreshQueued;

    private void ScheduleAvailabilityRefresh(TimeSpan after)
    {
        if (_closed) return;
        _availabilityTimer ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
        _availabilityTimer.Stop();
        _availabilityTimer.Interval = after;
        _availabilityTimer.Tick -= OnAvailabilityTimer;
        _availabilityTimer.Tick += OnAvailabilityTimer;
        _availabilityTimer.Start();
    }

    private void OnAvailabilityTimer(object? sender, EventArgs e)
    {
        _availabilityTimer?.Stop();
        if (!_closed && StartPage.Visibility == Visibility.Visible) RefreshRecent();
    }

    /// <summary>An answer came in, on a background thread: the list is shown again, once for a burst of answers.</summary>
    private void OnAvailabilityChanged()
    {
        if (_availabilityRefreshQueued) return;
        _availabilityRefreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _availabilityRefreshQueued = false;
            if (!_closed && StartPage.Visibility == Visibility.Visible) RefreshRecent();
        });
    }

    private void OnRecentChanged(object? sender, EventArgs e)
    {
        if (StartPage.Visibility == Visibility.Visible) RefreshRecent();
    }

    private void OnRecentPinClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // not also a click on the row it sits on
        if (sender is FrameworkElement { Tag: string path })
            SettingsStore.Current.SetPinned(path, SettingsStore.Current.FindRecent(path) is not { Pinned: true });
    }

    private void OnRecentClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path }) _app.Open(path, this);
    }

    private void OnRecentRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path }) SettingsStore.Current.Forget(path);
    }

    private void OnClearRecentClick(object sender, RoutedEventArgs e) => SettingsStore.Current.ClearRecent();

    private void OnOpenClick(object sender, RoutedEventArgs e) => ShowOpenDialog();

    public void ShowOpenDialog()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = $"{Strings.Open_Filter_Pdf}|*.pdf|{Strings.Open_Filter_All}|*.*",
            Multiselect = true,
            CheckFileExists = true,
            Title = Strings.Open_Dialog_Title,
        };
        if (_document is not null) dialog.InitialDirectory = Path.GetDirectoryName(_document.FilePath);
        if (dialog.ShowDialog(this) != true) return;

        foreach (var (file, i) in dialog.FileNames.Select((f, i) => (f, i)))
            _app.Open(file, i == 0 ? this : null);
    }

    // ===================================================================== header state

    private void UpdateViewState()
    {
        var document = _document;
        if (document is null)
        {
            SidebarSummary.Text = string.Empty;
            return;
        }

        var page = View.CurrentPage;
        if (!PageBox.IsKeyboardFocusWithin) PageBox.Text = (page + 1).ToString(UiLanguage.Culture);
        PageCountText.Text = UiLanguage.Format(Strings.Main_PageOf, document.PageCount);
        ZoomText.Text = FormatZoom(View.Zoom);
        FitButton.Tag = View.Mode == ZoomMode.FitWidth ? "on" : null;
        SidebarButton.Tag = IsSidebarOpen ? "on" : null;
        SidebarSummary.Text = UiLanguage.Format(Strings.Main_Sidebar_Summary, page + 1, document.PageCount, FormatSize(document.FileLength));
        SyncSidebarPage(page);
        UpdateTrailHere(page);
        UpdatePagesButton();
        TrackPageForIndicator(document.PageCount);
    }

    internal static string FormatZoom(double zoom) =>
        UiLanguage.Format("{0:0}%", Math.Round(zoom * 100));

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => UiLanguage.Format(Strings.Size_Bytes, bytes),
        < 1024 * 1024 => UiLanguage.Format(Strings.Size_KB, bytes / 1024.0),
        < 1024L * 1024 * 1024 => UiLanguage.Format(Strings.Size_MB, bytes / (1024.0 * 1024)),
        _ => UiLanguage.Format(Strings.Size_GB, bytes / (1024.0 * 1024 * 1024)),
    };

    private void OnPageBoxFocus(object sender, KeyboardFocusChangedEventArgs e) => PageBox.SelectAll();

    private void OnPageBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdateViewState();

    private void OnPageBoxKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                if (_document is not null &&
                    int.TryParse(PageBox.Text.Trim(), System.Globalization.NumberStyles.Integer, UiLanguage.Culture, out var number))
                {
                    View.GoToPage(Math.Clamp(number, 1, _document.PageCount) - 1);
                }
                View.Focus();
                UpdateViewState();
                break;
            case Key.Escape:
                e.Handled = true;
                View.Focus();
                UpdateViewState();
                break;
        }
    }

    public void FocusPageBox()
    {
        if (_document is null) return;
        PageBox.Focus();
        PageBox.SelectAll();
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e) => View.ZoomIn();

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => View.ZoomOut();

    private void OnFitClick(object sender, RoutedEventArgs e) =>
        View.SetMode(View.Mode == ZoomMode.FitWidth ? ZoomMode.FitPage : ZoomMode.FitWidth);

    private void OnRotateClick(object sender, RoutedEventArgs e) => RotateView(1);

    private void RotateView(int quarterTurns)
    {
        View.Rotate(quarterTurns);
        ReloadThumbnails();
    }

    private void OnPrintClick(object sender, RoutedEventArgs e) => Print();

    private void OnZoomMenuClick(object sender, RoutedEventArgs e) => BuildZoomMenu().IsOpen = true;

    internal ContextMenu BuildZoomMenu()
    {
        var menu = new ContextMenu
        {
            PlacementTarget = ZoomButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            VerticalOffset = -6,
        };

        AddItem(Strings.Main_FitWidth, () => View.SetMode(ZoomMode.FitWidth), View.Mode == ZoomMode.FitWidth, "Ctrl+2");
        AddItem(Strings.Main_FitPage, () => View.SetMode(ZoomMode.FitPage), View.Mode == ZoomMode.FitPage, "Ctrl+0");
        menu.Items.Add(new Separator());
        foreach (var zoom in new[] { 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4 })
        {
            var z = zoom;
            AddItem(FormatZoom(z), () => View.SetZoom(z), View.Mode == ZoomMode.Custom && Math.Abs(View.Zoom - z) < 0.001, z == 1 ? "Ctrl+1" : null);
        }
        return menu;

        void AddItem(string header, Action action, bool isChecked, string? gesture)
        {
            var item = new MenuItem { Header = header, IsChecked = isChecked, InputGestureText = gesture ?? string.Empty };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
    }

    // ===================================================================== link status

    private void OnLinkHovered(object? sender, LinkEventArgs e)
    {
        if (e.Target is null)
        {
            StatusPill.Visibility = Visibility.Collapsed;
            return;
        }
        StatusText.Text = PdfView.LinkText(e.Target);
        StatusIcon.Visibility = Visibility.Visible;
        StatusPill.Visibility = StatusText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private DispatcherTimer? _toastTimer;

    /// <summary>A short message in the status pill, such as "Copied".</summary>
    public void ShowToast(string message)
    {
        StatusText.Text = message;
        StatusIcon.Visibility = Visibility.Collapsed;
        StatusPill.Visibility = Visibility.Visible;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(1600), DispatcherPriority.Normal, (_, _) =>
        {
            _toastTimer!.Stop();
            if (StatusIcon.Visibility == Visibility.Collapsed) StatusPill.Visibility = Visibility.Collapsed;
        }, Dispatcher);
        _toastTimer.Start();
    }

    // ===================================================================== dialogs

    // Tests answer the window's questions through these instead of modal dialogs.
    internal static Func<string, bool, string?>? PasswordPrompt { get; set; }

    internal static Func<string, bool>? ConfirmPrompt { get; set; }

    internal static Action<string, string>? NoticeSink { get; set; }

    private string? AskPassword(string fileName, bool wrongBefore)
    {
        if (PasswordPrompt is { } prompt) return prompt(fileName, wrongBefore);
        var dialog = new PasswordDialog(fileName, wrongBefore) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Password : null;
    }

    private bool Confirm(string title, string message, string target, string confirmText)
    {
        if (ConfirmPrompt is { } prompt) return prompt(target);
        return new ConfirmDialog(title, message, target, confirmText) { Owner = this }.ShowDialog() == true;
    }

    public void ShowNotice(string title, string message)
    {
        if (_closed) return;
        if (NoticeSink is { } sink)
        {
            sink(title, message);
            return;
        }
        var dialog = new NoticeDialog(title, message);
        if (IsVisible) dialog.Owner = this;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
    }

    private void ShowProperties()
    {
        if (_document is null) return;
        new PropertiesDialog(_document) { Owner = this }.ShowDialog();
    }

    // ===================================================================== keyboard

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        var ctrl = mods.HasFlag(ModifierKeys.Control);
        var shift = mods.HasFlag(ModifierKeys.Shift);
        var alt = mods.HasFlag(ModifierKeys.Alt);
        var inText = Keyboard.FocusedElement is TextBox;

        bool Do(Action action)
        {
            action();
            return true;
        }

        var handled = (key, ctrl, shift, alt) switch
        {
            (Key.O, true, false, false) => Do(ShowOpenDialog),
            (Key.N, true, false, false) => Do(() => _app.OpenEmptyWindow(this)),
            (Key.W, true, false, false) => Do(Close),
            (Key.P, true, false, false) when HasDocument => Do(Print),
            (Key.F, true, false, false) when HasDocument => Do(FocusSearch),
            (Key.G, true, false, false) when HasDocument => Do(FocusPageBox),
            (Key.D, true, false, false) when HasDocument => Do(ShowProperties),
            (Key.F3, false, false, false) when HasDocument => Do(() => MoveMatch(+1)),
            (Key.F3, false, true, false) when HasDocument => Do(() => MoveMatch(-1)),
            (Key.F4, false, false, false) when HasDocument => Do(() => SetSidebar(!IsSidebarOpen, remember: true)),
            (Key.F5, false, false, false) when HasDocument => Do(() => _ = Reload()),
            (Key.F11, false, false, false) when HasDocument => Do(ToggleFullScreen),
            (Key.Escape, false, false, false) when _fullScreen => Do(ExitFullScreen),
            (Key.Left, false, false, true) when HasDocument => Do(View.GoBack),
            (Key.Right, false, false, true) when HasDocument => Do(View.GoForward),
            (Key.Add or Key.OemPlus, true, false, false) when HasDocument => Do(() => View.ZoomIn()),
            (Key.Subtract or Key.OemMinus, true, false, false) when HasDocument => Do(() => View.ZoomOut()),
            (Key.Add or Key.OemPlus, true, true, false) when HasDocument => Do(() => RotateView(1)),
            (Key.Subtract or Key.OemMinus, true, true, false) when HasDocument => Do(() => RotateView(-1)),
            (Key.D0 or Key.NumPad0, true, false, false) when HasDocument => Do(() => View.SetMode(ZoomMode.FitPage)),
            (Key.D1 or Key.NumPad1, true, false, false) when HasDocument => Do(() => View.SetZoom(1)),
            (Key.D2 or Key.NumPad2, true, false, false) when HasDocument => Do(() => View.SetMode(ZoomMode.FitWidth)),
            (Key.C, true, false, false) when HasDocument && !inText => Do(CopySelection),
            (Key.A, true, false, false) when HasDocument && !inText => Do(View.SelectAll),
            _ => false,
        };

        if (handled) e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _document is null || Keyboard.FocusedElement is TextBox) return;

        // Reading keys reach the pages wherever focus is (a toolbar button just clicked, say).
        if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right or Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Space)
        {
            if (Keyboard.FocusedElement is not PdfView && View.HandleKey(e.Key, Keyboard.Modifiers))
            {
                View.Focus();
                e.Handled = true;
            }
        }
    }

    private async void CopySelection()
    {
        try
        {
            if (await View.CopySelectionAsync().ConfigureAwait(true)) ShowToast(Strings.Main_Copied);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Copying failed.", ex);
        }
    }

    // ===================================================================== drag and drop

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        UpdateDrag(e);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        UpdateDrag(e);
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);
        DropHint.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// The files being dragged in. This runs on every move of a drag, so it asks the file system
    /// only about files on the computer's own disks; anything else (a network share, a stick) is
    /// taken as it is, and a folder among them simply fails to open.
    /// </summary>
    private static string[] DroppedFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(LooksLikeFile).ToArray()
            : [];

    private static bool LooksLikeFile(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || string.IsNullOrEmpty(Path.GetFileName(path))) return false;
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            return new DriveInfo(path[..1]).DriveType is not (DriveType.Fixed or DriveType.Ram) || File.Exists(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void UpdateDrag(DragEventArgs e)
    {
        var any = DroppedFiles(e).Length > 0;
        e.Effects = any ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        DropHint.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        DropHint.Visibility = Visibility.Collapsed;
        var files = DroppedFiles(e);
        e.Handled = true;
        for (var i = 0; i < files.Length; i++) _app.Open(files[i], i == 0 ? this : null);
        Activate();
    }

    // ===================================================================== lifetime

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        try
        {
            RememberDocument();
            SaveWindowPlacement();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Saving the window's state failed.", ex);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _openGeneration++;
        SettingsStore.Current.RecentChanged -= OnRecentChanged;
        FileAvailability.Changed -= OnAvailabilityChanged;
        _availabilityTimer?.Stop();
        StopPageIndicator();
        EndSearch(clearText: false);
        StopWatching();
        var document = _document;
        _document = null;
        View.SetDocument(null);
        UnloadSidebar();
        _ = document?.CloseAsync();
        base.OnClosed(e);
    }

    private void SaveWindowPlacement()
    {
        if (_fullScreen) return;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.Width < 200 || bounds.Height < 200 || !double.IsFinite(bounds.Left)) return;
        SettingsStore.Current.Settings.Window = new WindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = WindowState == WindowState.Maximized,
        };
        SettingsStore.Current.Save();
    }
}
