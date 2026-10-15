using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EasyPDF.Pdf;
using EasyPDF.Services;

namespace EasyPDF.Views;

/// <summary>An outline entry as the tree shows it.</summary>
public sealed class OutlineNode : INotifyPropertyChanged
{
    private bool _isExpanded;

    public OutlineNode(OutlineItem item)
    {
        Title = item.Title.Length > 0 ? item.Title : "—";
        Target = item.Target;
        PageLabel = item.PageIndex >= 0 ? (item.PageIndex + 1).ToString(UiLanguage.Culture) : string.Empty;
        _isExpanded = item.StartsOpen;
        Children = item.Children.Count == 0 ? Array.Empty<OutlineNode>() : item.Children.Select(c => new OutlineNode(c)).ToArray();
    }

    public string Title { get; }

    public string PageLabel { get; }

    public LinkTarget? Target { get; }

    public IReadOnlyList<OutlineNode> Children { get; }

    /// <summary>
    /// The children as the tree shows them: a view of its own. Given the list itself, WPF would
    /// make a view of it and keep both for ever in its binding engine's table of views.
    /// </summary>
    public ListCollectionView? ChildrenView => Children.Count == 0 ? null : _childrenView ??= new ListCollectionView((System.Collections.IList)Children);

    private ListCollectionView? _childrenView;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A page in the thumbnail strip.</summary>
public sealed record ThumbnailItem(int Index, string Label, double Width, double Height, ThumbnailSource Source);

public partial class MainWindow
{
    /// <summary>Thumbnail sizes: normal, and compact - more pages in sight.</summary>
    private double ThumbnailWidth => _compactSidebar ? 72 : 120;

    private double ThumbnailMaxHeight => _compactSidebar ? 108 : 180;

    private bool _compactSidebar;

    private enum SidebarMode
    {
        Outline,
        Pages,
    }

    private SidebarMode _sidebarMode = SidebarMode.Outline;
    private bool _sidebarOpen;
    private bool _sidebarModeChosen;
    private int _outlineGeneration;
    private ThumbnailSource? _thumbnails;
    private List<ThumbnailItem>? _thumbnailItems;
    private bool _syncingThumbnails;
    private bool _settingMode;

    public bool IsSidebarOpen => _sidebarOpen;

    internal ThumbnailSource? Thumbnails => _thumbnails;

    internal void ShowPagesInSidebar() => ShowSidebarMode(SidebarMode.Pages);

    private void InitializeSidebar()
    {
        ApplySidebarDensity(SettingsStore.Current.Settings.SidebarCompact);
        var width = SettingsStore.Current.Settings.SidebarWidth;
        SidebarColumn.Width = new GridLength(width);
        ShowSidebarMode(SidebarMode.Outline);
        SetSidebar(false, remember: false);
    }

    public void SetSidebar(bool open, bool remember)
    {
        _sidebarOpen = open && _document is not null;
        if (_sidebarOpen)
        {
            SidebarColumn.MinWidth = 180;
            SidebarColumn.Width = new GridLength(SettingsStore.Current.Settings.SidebarWidth);
            Sidebar.Visibility = Visibility.Visible;
            SidebarSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            SidebarColumn.MinWidth = 0;
            SidebarColumn.Width = new GridLength(0);
            Sidebar.Visibility = Visibility.Collapsed;
            SidebarSplitter.Visibility = Visibility.Collapsed;
        }

        if (remember)
        {
            SettingsStore.Current.Settings.SidebarOpen = open;
            SettingsStore.Current.Save();
        }
        SidebarButton.Tag = _sidebarOpen ? "on" : null;
        if (_sidebarOpen) SyncSidebarPage(View.CurrentPage);
    }

    private void OnSidebarClick(object sender, RoutedEventArgs e) => SetSidebar(!_sidebarOpen, remember: true);

    private void OnSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        var width = SidebarColumn.ActualWidth;
        if (width < 180) return;
        SettingsStore.Current.Settings.SidebarWidth = Math.Round(width);
        SettingsStore.Current.Save();
    }

    private void OnSidebarModeChanged(object sender, RoutedEventArgs e)
    {
        if (_settingMode) return;
        var mode = PagesChip.IsChecked == true ? SidebarMode.Pages : SidebarMode.Outline;
        if (IsLoaded) _sidebarModeChosen = true;
        ShowSidebarMode(mode);
    }

    private void ShowSidebarMode(SidebarMode mode)
    {
        _sidebarMode = mode;
        var outline = mode == SidebarMode.Outline;
        _settingMode = true;
        try
        {
            if (OutlineChip.IsChecked != outline) OutlineChip.IsChecked = outline;
            if (PagesChip.IsChecked == outline) PagesChip.IsChecked = !outline;
        }
        finally
        {
            _settingMode = false;
        }

        OutlineTree.Visibility = outline ? Visibility.Visible : Visibility.Collapsed;
        ThumbnailList.Visibility = outline ? Visibility.Collapsed : Visibility.Visible;
        OutlineEmpty.Visibility = outline && OutlineTree.Items.Count == 0 && _document is not null ? Visibility.Visible : Visibility.Collapsed;

        if (!outline && ThumbnailList.ItemsSource is null && _thumbnailItems is not null) ThumbnailList.ItemsSource = ViewOf(_thumbnailItems);
        SyncSidebarPage(View.CurrentPage);
    }

    private void LoadSidebar(PdfDocument document)
    {
        OutlineTree.ItemsSource = null;
        ThumbnailList.ItemsSource = null;
        _thumbnailItems = null;
        _thumbnails = null;

        // The sidebar takes its width now, so the page is laid out once, at its final size; its
        // lists are filled after the window has drawn - the page comes first.
        var generation = ++_outlineGeneration;
        SetSidebar(SettingsStore.Current.Settings.SidebarOpen, remember: false);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (generation != _outlineGeneration || !ReferenceEquals(document, _document)) return;
            BuildThumbnails(document);
            SyncSidebarPage(View.CurrentPage);
            _ = LoadOutline();
        }));

        async Task LoadOutline()
        {
            IReadOnlyList<OutlineItem> items;
            try
            {
                items = await document.LoadOutlineAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppLog.Warn("The outline could not be read.", ex);
                items = Array.Empty<OutlineItem>();
            }
            if (generation != _outlineGeneration || !ReferenceEquals(document, _document)) return;

            OutlineTree.ItemsSource = new ListCollectionView(items.Select(i => new OutlineNode(i)).ToArray());
            IndexSections(items);
            // With no outline the pages are the useful view - unless the user chose otherwise.
            var mode = _sidebarModeChosen ? _sidebarMode : items.Count > 0 ? SidebarMode.Outline : SidebarMode.Pages;
            ShowSidebarMode(mode);
        }
    }

    private void BuildThumbnails(PdfDocument document)
    {
        var rotation = View.Rotation;
        _thumbnails = new ThumbnailSource(document, rotation);
        var items = new List<ThumbnailItem>(document.PageCount);
        for (var i = 0; i < document.PageCount; i++)
        {
            var size = document.PageSizes[i];
            var (w, h) = (rotation & 1) == 1 ? (size.Height, size.Width) : (size.Width, size.Height);
            var width = ThumbnailWidth;
            var height = Math.Round(width * h / w);
            if (height > ThumbnailMaxHeight)
            {
                height = ThumbnailMaxHeight;
                width = Math.Max(24, Math.Round(height * w / h));
            }
            items.Add(new ThumbnailItem(i, (i + 1).ToString(UiLanguage.Culture), width, Math.Max(24, height), _thumbnails));
        }
        _thumbnailItems = items;
        if (_sidebarMode == SidebarMode.Pages) ThumbnailList.ItemsSource = ViewOf(items);
    }

    /// <summary>After rotating, the thumbnails are drawn again the new way round.</summary>
    private void ReloadThumbnails()
    {
        if (_document is null) return;
        ThumbnailList.ItemsSource = null;
        BuildThumbnails(_document);
        SyncSidebarPage(View.CurrentPage);
    }

    /// <summary>Normal rows, or compact ones: shorter, tighter, smaller thumbnails - more of the outline and more pages in sight.</summary>
    internal void SetCompactSidebar(bool compact)
    {
        if (compact == _compactSidebar) return;
        ApplySidebarDensity(compact);
        SettingsStore.Current.Settings.SidebarCompact = compact;
        SettingsStore.Current.Save();
        if (_thumbnailItems is not null) ReloadThumbnails();
    }

    internal bool IsSidebarCompact => _compactSidebar;

    private void ApplySidebarDensity(bool compact)
    {
        _compactSidebar = compact;
        Resources["NavigatorRowHeight"] = compact ? 22.0 : 32.0;
        Resources["NavigatorRowPadding"] = compact ? new Thickness(6, 0, 6, 0) : new Thickness(6, 3, 6, 3);
        Resources["NavigatorFontSize"] = compact ? 12.0 : 13.0;
        Resources["ThumbnailRowPadding"] = compact ? new Thickness(0, 4, 0, 3) : new Thickness(0, 8, 0, 6);
        CompactButton.Tag = compact ? "on" : null;
    }

    private void OnCompactClick(object sender, RoutedEventArgs e) => SetCompactSidebar(!_compactSidebar);

    private void UnloadSidebar()
    {
        _outlineGeneration++;
        OutlineTree.ItemsSource = null;
        ThumbnailList.ItemsSource = null;
        _thumbnailItems = null;
        _thumbnails = null;
    }

    /// <summary>The thumbnail strip follows the page being read.</summary>
    private void SyncSidebarPage(int page)
    {
        if (!_sidebarOpen || _sidebarMode != SidebarMode.Pages || _thumbnailItems is null || ThumbnailList.ItemsSource is null) return;
        if ((uint)page >= (uint)_thumbnailItems.Count || ThumbnailList.SelectedIndex == page) return;

        _syncingThumbnails = true;
        try
        {
            ThumbnailList.SelectedIndex = page;
            ThumbnailList.ScrollIntoView(_thumbnailItems[page]);
        }
        finally
        {
            _syncingThumbnails = false;
        }
    }

    private void OnThumbnailClick(object sender, MouseButtonEventArgs e)
    {
        if (_syncingThumbnails) return;
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is { DataContext: ThumbnailItem item })
        {
            View.GoToPage(item.Index);
            View.Focus();
        }
    }

    private void OnThumbnailKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ThumbnailList.SelectedItem is ThumbnailItem item)
        {
            View.GoToPage(item.Index);
            e.Handled = true;
        }
    }

    private void OnOutlineClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        // The arrow opens and closes the entry; only the entry itself goes anywhere.
        if (FindAncestor<ToggleButton>(source) is not null) return;
        if (FindAncestor<TreeViewItem>(source) is { DataContext: OutlineNode node }) FollowOutline(node);
    }

    private void OnOutlineKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && OutlineTree.SelectedItem is OutlineNode node)
        {
            FollowOutline(node);
            e.Handled = true;
        }
    }

    /// <summary>
    /// The list wrapped in a view of its own. Given a plain list, WPF makes a default view and
    /// keeps it in a table of its binding engine long after the list is shown - and through the
    /// thumbnails, the document with it. A view supplied by the window is used as it is and goes
    /// when the window does.
    /// </summary>
    /// <summary>
    /// A list's own view, for an ItemsSource: given a list itself, WPF makes a view of it and keeps
    /// both, for ever, in its binding engine's table of views - one more for every list shown.
    /// </summary>
    internal static ListCollectionView ViewOf(System.Collections.IList items) => new(items);

    internal void FollowOutline(OutlineNode node)
    {
        if (node.Target is null) return;
        View.Activate(node.Target);
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match) return match;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
}
