using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using EasyPDF.Resources;
using EasyPDF.Services;
using Size = System.Windows.Size;

namespace EasyPDF.Views;

/// <summary>
/// Fits the header to the window. The search box takes the free width; as the window narrows the
/// header gives way in steps, keeping the search box usable at each one:
///
///   1. everything, with the label on "Open";
///   2. "Open" as an icon;
///   3. the document name gone, the app icon alone;
///   4. buttons moving, rightmost first, into the "more" menu;
///   5. the page count's "of N" and the separator gone too.
///
/// Each step is tried in turn and the first that leaves the search box enough room wins, so the
/// layout follows the real widths of the text in whichever language is showing.
/// </summary>
public partial class MainWindow
{
    private const double SearchRoomWithLabels = 300;
    private const double SearchRoomIconsOnly = 240;
    private const double SearchRoomMinimum = 200;
    private const double SearchMargins = 24 + 16;

    private List<Button>? _overflowOrder;
    private readonly HashSet<Button> _overflowed = new();

    private void OnHeaderSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) FitHeader();
    }

    private void FitHeader()
    {
        try
        {
            var total = HeaderGrid.ActualWidth;
            if (total <= 0) return;

            _overflowOrder ??= [PrintButton, RotateButton, PagesButton, FitButton, SidebarButton, ZoomInButton, ZoomOutButton];
            var fixedWidth = MeasuredWidth(CaptionButtons) + SearchMargins;

            if (Fits(label: true, title: true, overflow: 0, SearchRoomWithLabels)) return;
            if (Fits(label: false, title: true, overflow: 0, SearchRoomIconsOnly)) return;
            if (Fits(label: false, title: false, overflow: 0, SearchRoomIconsOnly)) return;
            for (var overflow = 1; overflow <= _overflowOrder.Count; overflow++)
            {
                if (Fits(label: false, title: false, overflow, SearchRoomMinimum)) return;
            }
            Apply(label: false, title: false, _overflowOrder.Count, compact: true);

            bool Fits(bool label, bool title, int overflow, double searchRoom)
            {
                Apply(label, title, overflow, compact: false);
                var room = total - fixedWidth - MeasuredWidth(BrandPanel) - MeasuredWidth(ToolbarPanel);
                return room >= searchRoom;
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"Fitting the header failed: {ex.Message}");
        }
    }

    private void Apply(bool label, bool title, int overflow, bool compact)
    {
        PageCountText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ToolbarSeparator.Visibility = compact || !HasDocument ? Visibility.Collapsed : Visibility.Visible;
        OpenLabel.Visibility = label ? Visibility.Visible : Visibility.Collapsed;
        if (IconOf(OpenButton) is { } icon) icon.Margin = label ? new Thickness(0, 0, 6, 0) : new Thickness(0);
        if (label) OpenButton.ClearValue(PaddingProperty);
        else OpenButton.Padding = new Thickness(8, 0, 8, 0);

        BrandTitle.Visibility = title ? Visibility.Visible : Visibility.Collapsed;

        _overflowed.Clear();
        for (var i = 0; i < _overflowOrder!.Count; i++)
        {
            var button = _overflowOrder[i];
            var hidden = i < overflow;
            if (hidden) _overflowed.Add(button);
            button.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>The width the element wants right now, measured afresh (see DynatecRDM's header for why).</summary>
    private static double MeasuredWidth(FrameworkElement element)
    {
        InvalidateMeasureDown(element);
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return element.DesiredSize.Width;
    }

    private static void InvalidateMeasureDown(DependencyObject node)
    {
        if (node is UIElement element) element.InvalidateMeasure();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            InvalidateMeasureDown(VisualTreeHelper.GetChild(node, i));
    }

    private static IconPath? IconOf(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is IconPath icon) return icon;
            if (child is DependencyObject inner && IconOf(inner) is { } found) return found;
        }
        return null;
    }

    // ===================================================================== the "more" menu

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        try
        {
            BuildMoreMenu().IsOpen = true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("The more menu could not be opened.", ex);
        }
    }

    internal ContextMenu BuildMoreMenu()
    {
        var menu = new ContextMenu
        {
            PlacementTarget = MoreButton,
            Placement = PlacementMode.Bottom,
            VerticalOffset = -6,
        };
        var hasDocument = _document is not null;

        // What the toolbar had no room for, in toolbar order.
        var any = false;
        foreach (var button in _overflowOrder?.AsEnumerable().Reverse() ?? [])
        {
            if (!_overflowed.Contains(button)) continue;
            var item = new MenuItem { Header = AutomationProperties.GetName(button), IsEnabled = hasDocument };
            var target = button;
            item.Click += (_, _) => target.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, target));
            if (IconOf(button) is { } source)
            {
                var glyph = new IconPath { Width = 14, Height = 14, Data = source.Data, StrokeThickness = source.StrokeThickness };
                glyph.SetResourceReference(IconPath.ForegroundProperty, "TextSecondaryBrush");
                item.Icon = glyph;
            }
            menu.Items.Add(item);
            any = true;
        }
        if (any) menu.Items.Add(new Separator());

        Add(Strings.More_Back, View.GoBack, "Alt+Left", hasDocument && View.History.CanGoBack);
        Add(Strings.More_Forward, View.GoForward, "Alt+Right", hasDocument && View.History.CanGoForward);
        menu.Items.Add(new Separator());
        Add(Strings.More_RotateLeft, () => RotateView(-1), "Ctrl+Shift+-", hasDocument);
        var display = new MenuItem { Header = Strings.Main_PageDisplay, IsEnabled = hasDocument };
        if (hasDocument) AddPageDisplayItems(display.Items, () => menu.IsOpen = false);
        menu.Items.Add(display);
        Add(Strings.More_FullScreen, ToggleFullScreen, "F11", hasDocument, isChecked: _fullScreen);
        menu.Items.Add(new Separator());
        Add(Strings.More_MatchCase, () => SetSearchOptions(!_matchCase, _wholeWord), null, hasDocument, isChecked: _matchCase);
        Add(Strings.More_WholeWords, () => SetSearchOptions(_matchCase, !_wholeWord), null, hasDocument, isChecked: _wholeWord);
        menu.Items.Add(new Separator());
        menu.Items.Add(BuildRecentMenu());
        Add(Strings.More_NewWindow, () => _app.OpenEmptyWindow(this), "Ctrl+N", true);
        Add(Strings.More_Reload, () => _ = Reload(), "F5", hasDocument);
        Add(Strings.More_ShowInFolder, ShowInFolder, null, hasDocument);
        Add(Strings.More_Properties, ShowProperties, "Ctrl+D", hasDocument);
        menu.Items.Add(new Separator());
        Add(Strings.More_CloseDocument, CloseDocument, null, hasDocument);
        Add(Strings.More_CloseWindow, Close, "Ctrl+W", true);
        return menu;

        void Add(string header, Action action, string? gesture, bool enabled, bool isChecked = false)
        {
            var item = new MenuItem
            {
                Header = header,
                InputGestureText = gesture ?? string.Empty,
                IsEnabled = enabled,
                IsChecked = isChecked,
            };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
    }

    /// <summary>The recent list, where it can be reached with a document open: pinned files first, then the rest, and a way to empty it.</summary>
    internal MenuItem BuildRecentMenu()
    {
        var recent = new MenuItem { Header = Strings.More_Recent };
        var items = RecentItems(RecentShown);
        foreach (var entry in items)
        {
            // A TextBlock, so an underscore in a file name is not taken for an access key.
            var item = new MenuItem
            {
                Header = new TextBlock { Text = entry.Name },
                ToolTip = entry.Path,
                InputGestureText = entry.IsAway ? entry.When : string.Empty,
                Opacity = entry.IsAway ? 0.6 : 1,
            };
            if (entry.Pinned)
            {
                var pin = new IconPath { Width = 14, Height = 14, Data = (System.Windows.Media.Geometry)FindResource("GeoPin"), StrokeThickness = 1.8 };
                pin.SetResourceReference(IconPath.ForegroundProperty, "AccentBrush");
                item.Icon = pin;
            }
            var path = entry.Path;
            item.Click += (_, _) => _app.Open(path, this);
            recent.Items.Add(item);
        }
        if (items.Count == 0) recent.Items.Add(new MenuItem { Header = Strings.More_Recent_None, IsEnabled = false });

        recent.Items.Add(new Separator());
        var clear = new MenuItem { Header = Strings.More_ClearRecent, IsEnabled = items.Any(i => !i.Pinned), ToolTip = Strings.Main_Recent_Clear_Tooltip };
        clear.Click += (_, _) => SettingsStore.Current.ClearRecent();
        recent.Items.Add(clear);
        return recent;
    }

    private void ShowInFolder()
    {
        if (_document is null) return;
        var file = _document.FilePath;

        // Off the UI thread: a file on a network share can take long to answer.
        _ = Task.Run(() =>
        {
            try
            {
                // Explorer by its full path: by name alone, Windows would look in the current folder first.
                var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                if (File.Exists(file))
                    Process.Start(new ProcessStartInfo(explorer, $"/select,\"{file}\"") { UseShellExecute = false })?.Dispose();
            }
            catch (Exception ex)
            {
                AppLog.Warn("The folder could not be shown.", ex);
            }
        });
    }
}
