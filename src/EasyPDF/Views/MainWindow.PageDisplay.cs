using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using EasyPDF.Resources;
using EasyPDF.Services;
using EasyPDF.Viewer;

namespace EasyPDF.Views;

/// <summary>
/// How pages are shown: one continuous column, or a fixed number at a time - one page, or a grid
/// of them (two side by side, a book's spreads, 2 x 2 and so on). A fixed number at a time,
/// turning is not scrolling - nothing slides by to show where the reader has got to - so each turn
/// shows, for a moment, which pages these are, of how many, and a line for how far through the
/// document they are.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan IndicatorIn = TimeSpan.FromMilliseconds(90);
    private static readonly TimeSpan IndicatorHold = TimeSpan.FromMilliseconds(1100);
    private static readonly TimeSpan IndicatorOut = TimeSpan.FromMilliseconds(1600);

    private int _indicatorPage = -1;

    /// <summary>Whether the page now shown is the document's first since it was opened: a page turned after that shows its number.</summary>
    private bool _indicatorArmed;

    /// <summary>Continuous (null), or so many pages at a time: for this document, and for documents opened for the first time from now on.</summary>
    internal void SetPageDisplay(PageGrid? grid)
    {
        if (_document is null || View.Grid == grid) return;
        View.SetGrid(grid);
        _indicatorPage = View.CurrentSheet.First;
        var settings = SettingsStore.Current.Settings;
        settings.Grid = grid?.ToString();
        settings.SinglePage = grid is not null;
        RememberDocument();
        SettingsStore.Current.Save();
        UpdateViewState();
    }

    /// <summary>One page at a time, or continuous.</summary>
    internal void SetSinglePage(bool single) => SetPageDisplay(single ? PageGrid.One : null);

    /// <summary>How documents opened for the first time are shown.</summary>
    private static PageGrid? DefaultGrid => PageGrid.Parse(SettingsStore.Current.Settings.Grid);

    /// <summary>On every change of the view: a turned page (or sheet) shows which it is - a fixed number at a time, or turned fast.</summary>
    private void TrackPageForIndicator(int count)
    {
        var (first, last) = View.CurrentSheet;
        if (!_indicatorArmed || !(View.IsFixed || View.FastFlipping))
        {
            // A document just opened, or continuous: no page has been turned.
            _indicatorArmed = true;
            _indicatorPage = first;
            return;
        }
        if (first == _indicatorPage || first < 0) return;
        _indicatorPage = first;
        ShowPageIndicator(first, last, count);
    }

    private void ShowPageIndicator(int first, int last, int count)
    {
        PageIndicatorNumber.Text = last > first
            ? string.Create(UiLanguage.Culture, $"{first + 1}–{last + 1}")
            : (first + 1).ToString(UiLanguage.Culture);
        PageIndicatorTotal.Text = UiLanguage.Format(Strings.Main_PageOf, count);
        PageIndicatorScale.ScaleX = count <= 1 ? 1 : (double)(last + 1) / count;
        PageIndicator.Visibility = Visibility.Visible;

        // In from where it is (a second turn keeps it up), held, then out.
        var fade = new DoubleAnimationUsingKeyFrames { Duration = IndicatorOut };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(IndicatorIn)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(IndicatorHold)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(IndicatorOut)));
        fade.Freeze();
        PageIndicator.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Takes the indicator away at once (and its animation with it, which would otherwise hold the window a moment longer).</summary>
    private void StopPageIndicator()
    {
        _indicatorArmed = false;
        PageIndicator.BeginAnimation(OpacityProperty, null);
        PageIndicator.Opacity = 0;
        PageIndicator.Visibility = Visibility.Collapsed;
    }

    /// <summary>Whether the page indicator is showing. For tests.</summary>
    internal bool PageIndicatorShown => PageIndicator.Visibility == Visibility.Visible;

    internal string PageIndicatorText => $"{PageIndicatorNumber.Text} {PageIndicatorTotal.Text}";

    // ===================================================================== the choices

    /// <summary>What a preset is called in the menus.</summary>
    internal static string GridName(PageGrid grid) => (grid.Columns, grid.Rows, grid.CoverAlone) switch
    {
        (1, 1, _) => Strings.Main_SinglePage,
        (2, 1, false) => Strings.Grid_TwoPages,
        (2, 1, true) => Strings.Grid_TwoPagesCover,
        (1, 2, _) => Strings.Grid_TwoStacked,
        (2, 2, _) => Strings.Grid_Four,
        (3, 2, _) => Strings.Grid_Six,
        (3, 3, _) => Strings.Grid_Nine,
        _ => GridSize(grid),
    };

    /// <summary>"3 x 2", or with the cover alone, "2 x 1, cover alone".</summary>
    internal static string GridSize(PageGrid grid) =>
        UiLanguage.Format(grid.CoverAlone ? Strings.Grid_SizeCover : Strings.Grid_Size, grid.Columns, grid.Rows);

    private void OnPagesClick(object sender, RoutedEventArgs e) => BuildPageDisplayMenu().IsOpen = true;

    /// <summary>The toolbar's page display menu: continuous, the presets, and a grid of any size.</summary>
    internal ContextMenu BuildPageDisplayMenu()
    {
        var menu = new ContextMenu
        {
            // From the More menu when the toolbar had no room for the button.
            PlacementTarget = PagesButton.IsVisible ? PagesButton : MoreButton,
            Placement = PlacementMode.Bottom,
            VerticalOffset = -6,
        };
        AddPageDisplayItems(menu.Items, () => menu.IsOpen = false);
        return menu;
    }

    /// <summary>The page display choices, into a menu; <paramref name="close"/> closes it once a grid is picked from the picker.</summary>
    internal void AddPageDisplayItems(ItemCollection items, Action close)
    {
        var current = View.Grid;
        Add(Strings.Main_Continuous, null, current is null);
        foreach (var preset in PageGrid.Presets) Add(GridName(preset), preset, current == preset);

        var other = new MenuItem { Header = Strings.Grid_Other, IsChecked = current is { } c && !PageGrid.Presets.Contains(c) };
        var picker = new GridPicker(current);
        picker.Picked += grid =>
        {
            close();
            SetPageDisplay(grid);
        };
        other.Items.Add(new MenuItem { Header = picker, StaysOpenOnClick = true, Template = BareItem() });
        items.Add(other);

        void Add(string header, PageGrid? grid, bool isChecked)
        {
            var item = new MenuItem { Header = header, IsChecked = isChecked, Icon = GridIcon(grid) };
            item.Click += (_, _) => SetPageDisplay(grid);
            items.Add(item);
        }
    }

    /// <summary>A menu item that is just what it holds: no highlight, no check, no padding of its own.</summary>
    private static ControlTemplate BareItem()
    {
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        var template = new ControlTemplate(typeof(MenuItem)) { VisualTree = presenter };
        template.Seal();
        return template;
    }

    private static IconPath GridIcon(PageGrid? grid)
    {
        var icon = new IconPath { Width = 16, Height = 16, Data = GridGeometry(grid), StrokeThickness = 1.5 };
        icon.SetResourceReference(IconPath.ForegroundProperty, "TextSecondaryBrush");
        return icon;
    }

    private static readonly Dictionary<string, Geometry> GridGeometries = new();

    /// <summary>
    /// An icon of the arrangement, on the 24-unit icon grid: a page's foot and the next one's top
    /// for continuous, the cells of the grid otherwise - the cover's empty neighbour left out.
    /// </summary>
    internal static Geometry GridGeometry(PageGrid? grid)
    {
        var key = grid?.ToString() ?? "continuous";
        if (GridGeometries.TryGetValue(key, out var cached)) return cached;

        string figures;
        if (grid is not { } g)
        {
            figures = "M6,3 L6,9.5 L18,9.5 L18,3 M6,21 L6,14.5 L18,14.5 L18,21";
        }
        else
        {
            const double from = 3, size = 18, gap = 2;
            // Cells shaped like pages: taller than wide, unless that would not fit.
            var w = (size - ((g.Columns - 1) * gap)) / g.Columns;
            var h = (size - ((g.Rows - 1) * gap)) / g.Rows;
            w = Math.Min(w, h * 0.78);
            var left = from + ((size - ((g.Columns * w) + ((g.Columns - 1) * gap))) / 2);
            var top = from + ((size - ((g.Rows * h) + ((g.Rows - 1) * gap))) / 2);
            var sb = new System.Text.StringBuilder();
            for (var r = 0; r < g.Rows; r++)
            {
                for (var c = 0; c < g.Columns; c++)
                {
                    if (g.CoverAlone && r == 0 && c == 0) continue;
                    var x = left + (c * (w + gap));
                    var y = top + (r * (h + gap));
                    sb.Append(CultureInfo.InvariantCulture, $"M{x:0.##},{y:0.##} h{w:0.##} v{h:0.##} h{-w:0.##} Z ");
                }
            }
            figures = sb.ToString();
        }

        var geometry = Geometry.Parse(figures);
        geometry.Freeze();
        GridGeometries[key] = geometry;
        return geometry;
    }

    /// <summary>The toolbar button shows the arrangement in use.</summary>
    private void UpdatePagesButton()
    {
        PagesIcon.Data = GridGeometry(View.Grid);
        PagesButton.Tag = View.IsFixed ? "on" : null;
    }
}

/// <summary>
/// Cells to point at, as a table is picked in a word processor: columns across and rows down,
/// the pages a sheet holds. Pointing shows the grid and its size; clicking picks it.
/// </summary>
internal sealed class GridPicker : StackPanel
{
    private const double CellWidth = 16;
    private const double CellHeight = 21;
    private const double CellGap = 3;

    private readonly Border[,] _cells = new Border[PageGrid.MaxColumns, PageGrid.MaxRows];
    private readonly Canvas _board = new();
    private readonly TextBlock _label = new() { Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly PageGrid? _current;
    private PageGrid? _pointed;

    public GridPicker(PageGrid? current)
    {
        _current = current;
        Margin = new Thickness(10, 8, 10, 8);
        _board.Width = (PageGrid.MaxColumns * (CellWidth + CellGap)) - CellGap;
        _board.Height = (PageGrid.MaxRows * (CellHeight + CellGap)) - CellGap;
        _board.Background = Brushes.Transparent;
        for (var c = 0; c < PageGrid.MaxColumns; c++)
        {
            for (var r = 0; r < PageGrid.MaxRows; r++)
            {
                var cell = new Border { Width = CellWidth, Height = CellHeight, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2) };
                Canvas.SetLeft(cell, c * (CellWidth + CellGap));
                Canvas.SetTop(cell, r * (CellHeight + CellGap));
                _cells[c, r] = cell;
                _board.Children.Add(cell);
            }
        }
        _label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        Children.Add(_board);
        Children.Add(_label);

        _board.MouseMove += (_, e) => Point(CellAt(e.GetPosition(_board)));
        _board.MouseLeave += (_, _) => Point(null);
        _board.MouseLeftButtonUp += (_, e) =>
        {
            if (CellAt(e.GetPosition(_board)) is { } grid)
            {
                e.Handled = true;
                Picked?.Invoke(grid);
            }
        };
        Point(null);
    }

    /// <summary>Raised with the grid clicked.</summary>
    public event Action<PageGrid>? Picked;

    /// <summary>The grid pointed at, or that showing. For tests.</summary>
    internal PageGrid? Shown => _pointed ?? _current;

    /// <summary>Picks a grid as a click would. For tests.</summary>
    internal void Pick(PageGrid grid) => Picked?.Invoke(grid);

    private static PageGrid? CellAt(System.Windows.Point p)
    {
        if (p.X < 0 || p.Y < 0) return null;
        var c = (int)(p.X / (CellWidth + CellGap));
        var r = (int)(p.Y / (CellHeight + CellGap));
        if (c >= PageGrid.MaxColumns || r >= PageGrid.MaxRows) return null;
        return new PageGrid(c + 1, r + 1);
    }

    private void Point(PageGrid? grid)
    {
        _pointed = grid;
        var shown = grid ?? _current;
        for (var c = 0; c < PageGrid.MaxColumns; c++)
        {
            for (var r = 0; r < PageGrid.MaxRows; r++)
            {
                var on = shown is { } s && c < s.Columns && r < s.Rows;
                var cell = _cells[c, r];
                cell.SetResourceReference(Border.BorderBrushProperty, on ? "AccentBrush" : "BorderBrushStrong");
                cell.SetResourceReference(Border.BackgroundProperty, on ? "AccentSubtleBrush" : "SurfaceBrush");
            }
        }
        _label.Text = shown is { } g
            ? UiLanguage.Format(Strings.Grid_Pages, MainWindow.GridSize(g), g.PerSheet)
            : Strings.Grid_Point;
    }
}
