using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using EasyPDF.Pdf;
using EasyPDF.Services;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace EasyPDF.Viewer;

public enum ZoomMode
{
    FitWidth,
    FitPage,
    Custom,
}

/// <summary>Everything needed to put a document back the way it was being read.</summary>
/// <param name="Grid">The pages shown at a time; null for one continuous column.</param>
public sealed record ViewState(int Page, double PageOffset, ZoomMode Mode, double Zoom, int Rotation, PageGrid? Grid = null);

/// <summary>
/// The page view: every page of a document in one continuous column - or a fixed number at a
/// time, one page or a grid of them (<see cref="PageGrid"/>) - scrolled by a ScrollViewer through
/// <see cref="IScrollInfo"/>.
///
/// It is drawn with a handful of visuals - one per page on screen, holding that page's tiles -
/// positioned in device pixels under a transform that undoes the DPI scale, so that each tile's
/// pixels land exactly on the screen's. Scrolling only moves those visuals; nothing is re-laid
/// out and nothing is re-rendered until a tile that was not on screen is needed.
/// </summary>
public sealed partial class PdfView : FrameworkElement, IScrollInfo
{
    public const double MinZoom = 0.1;
    public const double MaxZoom = 64;

    public static readonly double[] ZoomSteps =
    [
        0.1, 0.25, 1.0 / 3, 0.5, 2.0 / 3, 0.75, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5, 6, 8, 12, 16, 24, 32, 48, 64,
    ];

    /// <summary>Between pages and around the document, in device-independent pixels.</summary>
    private const double GapDip = 10;
    private const double MarginDip = 14;

    /// <summary>How long the zoom must be still before tiles are rendered at the new scale.</summary>
    private static readonly TimeSpan ZoomSettle = TimeSpan.FromMilliseconds(70);

    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(PdfView),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly ContainerVisual _root = new();
    private readonly ScaleTransform _rootScale = new(1, 1);
    private readonly TileRenderer _tiles;
    private readonly DispatcherTimer _settleTimer;

    private PdfDocument? _document;
    private PageLayout? _layout;
    private double _zoom = 1;
    private ZoomMode _mode = ZoomMode.FitWidth;
    private int _rotation;
    private double _dpi = 1;
    private double _offsetX;
    private double _offsetY;
    private int _viewportWidth;
    private int _viewportHeight;
    private int _currentPage;
    private PageGrid? _grid;

    /// <summary>
    /// A sheet at a time, the page a jump went to (a link's, a page number's, Back's): the current
    /// page while it is on the sheet in view and as much in sight as any other, rather than the
    /// sheet's first. -1 for none.
    /// </summary>
    private int _preferredPage = -1;
    private ViewState? _pendingState;
    private bool _settling;
    private double _lastScrollDirection = 1;

    public PdfView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        _root.Transform = _rootScale;
        _root.Children.Add(_pageLayer);
        _root.Children.Add(_fadeLayer);
        AddVisualChild(_root);

        _tiles = new TileRenderer(Dispatcher);
        _tiles.TilesArrived += OnTilesArrived;

        _settleTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = ZoomSettle };
        _settleTimer.Tick += (_, _) =>
        {
            _settleTimer.Stop();
            _settling = false;
            UpdateViewport();
        };

        Loaded += (_, _) =>
        {
            if (ThemeService.Current is { } theme) theme.PaletteChanged += OnPaletteChanged;
        };
        Unloaded += (_, _) =>
        {
            if (ThemeService.Current is { } theme) theme.PaletteChanged -= OnPaletteChanged;
            StopAnimation();
            StopFlash();
            EndTurnFade();
        };

        InitializeInput();
    }

    public Brush Background
    {
        get => (Brush)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public PdfDocument? Document => _document;

    public double Zoom => _zoom;

    public ZoomMode Mode => _mode;

    /// <summary>Quarter turns clockwise, 0-3.</summary>
    public int Rotation => _rotation;

    /// <summary>The pages shown at a time - one, or a grid of them; null for one continuous column.</summary>
    public PageGrid? Grid => _grid;

    /// <summary>A fixed number of pages at a time, rather than one continuous column.</summary>
    public bool IsFixed => _grid is not null;

    /// <summary>The pages in view: a sheet at a time, the sheet's; continuous, the current page.</summary>
    public (int First, int Last) CurrentSheet
    {
        get
        {
            if (_document is null) return (-1, -1);
            if (_layout is not { Fixed: true } layout || (uint)_currentPage >= (uint)layout.Count) return (_currentPage, _currentPage);
            var sheet = layout.SheetOf(_currentPage);
            return (layout.FirstOfSheet(sheet), layout.LastOfSheet(sheet));
        }
    }

    /// <summary>The page showing most, zero-based; -1 with no document.</summary>
    public int CurrentPage => _document is null ? -1 : _currentPage;

    public int PageCount => _document?.PageCount ?? 0;

    public NavigationHistory History { get; } = new();

    internal TileRenderer Tiles => _tiles;

    internal PageLayout? Layout => _layout;

    /// <summary>Raised when the current page, zoom, mode or rotation changes.</summary>
    public event EventHandler? ViewChanged;

    // ===================================================================== document

    /// <summary>Shows a document (or nothing), optionally where it was left.</summary>
    /// <param name="keepHistory">The same file, reloaded: Back and Forward still lead where they did.</param>
    public void SetDocument(PdfDocument? document, ViewState? state = null, bool keepHistory = false)
    {
        StopAnimation();
        StopFlash();
        EndPointerGesture();
        ClearPageVisuals();
        ResetText();

        _document = document;
        _tiles.SetDocument(document);
        if (!keepHistory || document is null) History.Clear();
        _offsetX = 0;
        _offsetY = 0;
        _currentPage = 0;
        _preferredPage = -1;
        _layout = null;

        if (document is null)
        {
            _pendingState = null;
            ScrollOwner?.InvalidateScrollInfo();
            InvalidateVisual();
            ViewChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        state ??= new ViewState(0, 0, ZoomMode.FitWidth, 1, 0);
        _mode = state.Mode;
        _zoom = Math.Clamp(state.Zoom, MinZoom, MaxZoom);
        _rotation = ((state.Rotation % 4) + 4) % 4;
        _grid = state.Grid;
        _pendingState = state;

        if (_viewportWidth > 0 && _viewportHeight > 0) ApplyPendingState();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Where the reader is, for remembering the document.</summary>
    public ViewState? CaptureState()
    {
        if (_document is null) return null;
        if (_pendingState is { } pending) return pending;
        var position = CurrentPosition();
        return new ViewState(position.Page, position.Offset, _mode, _zoom, _rotation, _grid);
    }

    /// <summary>
    /// Shows a fixed number of pages at a time - one, or a grid of them - or (null) one continuous
    /// column, staying on the page being read. A grid of several pages that would not fit the
    /// window at its width is fitted whole: a grid is chosen to see its pages together.
    /// </summary>
    public void SetGrid(PageGrid? grid)
    {
        if (grid == _grid) return;
        _grid = grid;
        if (_pendingState is { } pending) _pendingState = pending with { Grid = grid };
        if (_layout is null || _document is null)
        {
            ViewChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var page = _currentPage;
        StopAnimation();
        EndTurnFade();
        if (grid is { PerSheet: > 1 } && _mode == ZoomMode.FitWidth && FitZoom(ZoomMode.FitPage) < FitZoom(ZoomMode.FitWidth) * 0.999)
            _mode = ZoomMode.FitPage;
        if (_mode != ZoomMode.Custom) _zoom = FitZoom(_mode);
        Relayout();
        _preferredPage = page;
        _offsetY = _layout!.SlotTop(page);
        _offsetX = XShowing(page, _offsetX);
        ClampOffsets(page);
        BeginSettle();
        UpdateViewport();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyPendingState()
    {
        if (_pendingState is not { } state || _document is null) return;
        _pendingState = null;

        _zoom = _mode != ZoomMode.Custom ? FitZoom(_mode) : ClampZoom(_zoom);
        Relayout();
        var page = Math.Clamp(state.Page, 0, _document.PageCount - 1);
        if (page == 0 && state.PageOffset <= 0) ScrollTo(0, 0, animate: false);
        else ScrollToPosition(new ViewPosition(page, state.PageOffset), animate: false);
        UpdateViewport();
    }

    // ===================================================================== layout

    private int Px(double dip) => (int)Math.Round(dip * _dpi);

    private void Relayout()
    {
        if (_document is null)
        {
            _layout = null;
            return;
        }

        var scale = _zoom * PageLayout.PointsToDip * _dpi;
        _layout = new PageLayout(_document.PageSizes, scale, _rotation, Px(GapDip), Px(MarginDip), _viewportWidth,
            _grid is null ? 0 : Math.Max(1, _viewportHeight), _grid ?? default);
    }

    /// <summary>The size most pages have: the median, so one odd page does not set the zoom for all.</summary>
    private (double Width, double Height) TypicalPage()
    {
        var sizes = _document!.PageSizes;
        var count = sizes.Count;
        if (count == 1) return Oriented(sizes[0]);

        // A sample is plenty for the median of a long document.
        var step = Math.Max(1, count / 512);
        var widths = new List<double>();
        var heights = new List<double>();
        for (var i = 0; i < count; i += step)
        {
            var (w, h) = Oriented(sizes[i]);
            widths.Add(w);
            heights.Add(h);
        }
        widths.Sort();
        heights.Sort();
        return (widths[widths.Count / 2], heights[heights.Count / 2]);

        (double, double) Oriented(PageSize s) => (_rotation & 1) == 1 ? (s.Height, s.Width) : (s.Width, s.Height);
    }

    /// <summary>The zoom at which the typical page fits the viewport's width, or all of it - a grid's typical sheet, with a grid.</summary>
    private double FitZoom(ZoomMode mode)
    {
        if (_document is null || _viewportWidth <= 0 || _viewportHeight <= 0) return _zoom;

        var (w, h) = TypicalPage();
        var margin = Px(MarginDip);
        var gap = Px(GapDip);
        var columns = _grid?.Columns ?? 1;
        var rows = _grid?.Rows ?? 1;
        // Each page a hair under its share, so rounding never adds a pixel and a horizontal scroll bar.
        var across = Math.Floor(Math.Max(columns, _viewportWidth - (2 * margin) - ((columns - 1) * gap)) / (double)columns);
        var widthScale = (across - 0.01) / w;
        var scale = widthScale;
        if (mode == ZoomMode.FitPage)
        {
            var down = Math.Floor(Math.Max(rows, _viewportHeight - (2 * margin) - ((rows - 1) * gap)) / (double)rows);
            var heightScale = (down - 0.01) / h;
            scale = Math.Min(widthScale, heightScale);
        }
        return ClampZoom(scale / (PageLayout.PointsToDip * _dpi));
    }

    private double ClampZoom(double zoom)
    {
        if (!double.IsFinite(zoom)) return 1;
        var max = MaxZoom;
        if (_document is not null)
        {
            // No page may be laid out past what a bitmap and PDFium's integers can take.
            var largest = _document.PageSizes.Max(s => Math.Max(s.Width, s.Height));
            max = Math.Min(max, PageLayout.MaxPagePixels / (largest * PageLayout.PointsToDip * Math.Max(1, _dpi)));
        }
        return Math.Clamp(zoom, MinZoom, Math.Max(MinZoom, max));
    }

    // ===================================================================== zoom and rotation

    public void SetZoom(double zoom, Point? anchor = null)
    {
        if (_document is null) return;
        ChangeView(ZoomMode.Custom, ClampZoom(zoom), _rotation, anchor);
    }

    public void SetMode(ZoomMode mode)
    {
        if (_document is null) return;
        if (mode == ZoomMode.Custom)
        {
            ChangeView(ZoomMode.Custom, _zoom, _rotation, null);
            return;
        }
        // Fitting keeps the top of the view where it is, as opposed to a zoom, which keeps the middle.
        ChangeView(mode, null, _rotation, new Point(RenderSize.Width / 2, 0));
    }

    public void ZoomIn(Point? anchor = null) => SetZoom(NextStep(_zoom, +1), anchor);

    public void ZoomOut(Point? anchor = null) => SetZoom(NextStep(_zoom, -1), anchor);

    public static double NextStep(double zoom, int direction)
    {
        if (direction > 0)
        {
            foreach (var step in ZoomSteps)
                if (step > zoom * 1.001) return step;
            return MaxZoom;
        }

        for (var i = ZoomSteps.Length - 1; i >= 0; i--)
            if (ZoomSteps[i] < zoom / 1.001) return ZoomSteps[i];
        return MinZoom;
    }

    public void Rotate(int quarterTurns)
    {
        if (_document is null) return;
        var rotation = (((_rotation + quarterTurns) % 4) + 4) % 4;
        ChangeView(_mode, null, rotation, new Point(RenderSize.Width / 2, RenderSize.Height / 2));
    }

    /// <summary>
    /// Changes zoom, fit or rotation keeping the point under <paramref name="anchor"/> (view
    /// coordinates; the middle of the view when null) where it is on screen.
    /// </summary>
    private void ChangeView(ZoomMode mode, double? zoom, int rotation, Point? anchor)
    {
        if (_document is null) return;
        if (_layout is null || _viewportWidth <= 0)
        {
            _mode = mode;
            _rotation = rotation;
            if (zoom is { } z) _zoom = z;
            if (_pendingState is { } p) _pendingState = p with { Mode = mode, Zoom = _zoom, Rotation = rotation };
            ViewChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var at = anchor ?? new Point(RenderSize.Width / 2, RenderSize.Height / 2);
        var viewX = Math.Clamp(at.X, 0, RenderSize.Width) * _dpi;
        var viewY = Math.Clamp(at.Y, 0, RenderSize.Height) * _dpi;
        var (page, fx, fy) = PointAt(viewX, viewY);

        var oldRotation = _rotation;
        _mode = mode;
        _rotation = rotation;
        _zoom = mode == ZoomMode.Custom ? zoom ?? _zoom : FitZoom(mode);
        Relayout();

        if (oldRotation != rotation)
        {
            var (ux, uy) = PageLayout.Unrotate(fx, fy, oldRotation);
            (fx, fy) = PageLayout.Rotate(ux, uy, rotation);
        }

        var layout = _layout!;
        StopAnimation();
        _offsetX = layout.Left(page) + (fx * layout.Width(page)) - viewX;
        _offsetY = layout.Top(page) + (fy * layout.Height(page)) - viewY;
        ClampOffsets(page);

        BeginSettle();
        UpdateViewport();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Holds rendering back until the zoom stops changing; scaled tiles fill in meanwhile.</summary>
    private void BeginSettle()
    {
        // Waiting is only worth it with something to show meanwhile: on a first layout, or after
        // a jump to pages never rendered, the new tiles are wanted straight away.
        if (!HasStandIns())
        {
            _settleTimer.Stop();
            _settling = false;
            return;
        }

        _settling = true;
        _settleTimer.Stop();
        _settleTimer.Start();
    }

    /// <summary>Whether any page now on screen holds tiles that can be stretched to stand in at the new scale.</summary>
    private bool HasStandIns()
    {
        var layout = _layout;
        if (layout is null || _viewportHeight <= 0) return false;
        var top = Math.Round(_offsetY);
        var (first, last) = layout.VisibleRange(top, top + _viewportHeight);
        if (first < 0) return false;
        for (var page = first; page <= last; page++)
        {
            foreach (var tile in _tiles.TilesFor(page))
            {
                if (tile.Key.Rotation == layout.Rotation && tile.Bitmap is not null) return true;
            }
        }
        return false;
    }

    /// <summary>The page under a viewport point in device pixels, and where on it (fractions of the page as shown).</summary>
    private (int Page, double X, double Y) PointAt(double viewX, double viewY)
    {
        var layout = _layout!;
        // The same whole-pixel offsets the pages are drawn at, so a point means what is under it.
        var x = Math.Round(_offsetX) + viewX;
        var y = Math.Round(_offsetY) + viewY;
        var page = layout.PageAt(x, y);
        var fx = (x - layout.Left(page)) / layout.Width(page);
        var fy = (y - layout.Top(page)) / layout.Height(page);
        return (page, fx, fy);
    }

    // ===================================================================== navigation

    /// <summary>
    /// Where the view is: the page at its top edge, and how far down that page the edge is as a
    /// fraction of the page's height - negative in the margin or gap above it. The exact inverse
    /// of <see cref="ScrollToPosition"/>, at any zoom: going back returns to the very pixel.
    /// </summary>
    public ViewPosition CurrentPosition()
    {
        if (_layout is null || _document is null) return new ViewPosition(_pendingState?.Page ?? 0, _pendingState?.PageOffset ?? 0);
        // A sheet at a time, the sheet's first page.
        var top = Math.Round(_offsetY);
        var page = _layout.PageAt(top);
        return new ViewPosition(page, (top - _layout.Top(page)) / _layout.Height(page), _currentPage);
    }

    public void ScrollToPosition(ViewPosition position, bool animate)
    {
        if (_layout is null || _document is null) return;
        var page = Math.Clamp(position.Page, 0, _layout.Count - 1);
        var offset = double.IsFinite(position.Offset) ? Math.Clamp(position.Offset, -1, 2) : 0;
        _preferredPage = position.Seen;
        ScrollTo(XShowing(page, _offsetX), _layout.Top(page) + (offset * _layout.Height(page)), animate, page);
    }

    /// <summary>
    /// A horizontal offset at which <paramref name="page"/> - a sheet at a time, its sheet - is in
    /// view: <paramref name="x"/> when it already is. After scrolling right on a wide page, a jump to
    /// a narrow one must not leave the view beside it, showing nothing.
    /// </summary>
    private double XShowing(int page, double x)
    {
        if (_layout is not { } layout || (uint)page >= (uint)layout.Count) return x;
        var (sheetLeft, sheetWidth) = layout.SheetSpan(layout.SheetOf(page));
        double left = sheetLeft - layout.Margin;
        double right = sheetLeft + sheetWidth + layout.Margin;
        // The page fits: x may go only as far as keeps all of it in view. It does not: the view stays on it.
        var lo = Math.Min(left, right - _viewportWidth);
        var hi = Math.Max(left, right - _viewportWidth);
        return Math.Clamp(x, lo, hi);
    }

    /// <summary>Remembers where the view is for Back - named by the page a link was followed from, when it was.</summary>
    private void RecordJump(int fromPage)
    {
        var here = CurrentPosition();
        History.Record((uint)fromPage < (uint)PageCount ? here with { Seen = fromPage } : here);
    }

    /// <summary>Shows a page from its top, with the gap above it - the first page from the very top.</summary>
    /// <param name="fromPage">The page of the link that leads there, if one does.</param>
    public void GoToPage(int page, bool record = true, int fromPage = -1)
    {
        if (_layout is null || _document is null) return;
        page = Math.Clamp(page, 0, _document.PageCount - 1);
        if (record) RecordJump(fromPage);
        _preferredPage = page;
        ScrollTo(XShowing(page, _offsetX), _layout.SlotTop(page), animate: false);
        if (record) History.Arrived(page);
    }

    /// <summary>
    /// A sheet at a time: the next sheet (or previous one, or <paramref name="steps"/> on), shown at
    /// once - from its top, or from its bottom when going back a line at a time. Continuous: the
    /// next page from its top, as <see cref="GoToPage"/>.
    /// </summary>
    public void FlipPage(int direction, bool fromBottom = false, int steps = 1)
    {
        if (_layout is not { } layout || _document is null || direction == 0) return;
        var sheet = layout.Fixed ? layout.SheetAt(_animating ? _targetY : _offsetY) : _currentPage;
        var next = Math.Clamp(sheet + (Math.Sign(direction) * Math.Max(1, steps)), 0, layout.SheetCount - 1);
        if (next == sheet) return;
        var page = layout.FirstOfSheet(next);
        var y = fromBottom && layout.Fixed ? layout.SheetBottom(next) - _viewportHeight : layout.SheetTop(next);
        _preferredPage = -1;
        ScrollTo(XShowing(page, _offsetX), y, animate: false, page);
    }

    private long _lastFastFlip;
    private int _fastFlipDirection;
    private int _fastFlips;

    /// <summary>Whether pages are being turned fast (Shift+Down/Up held, or pressed in quick succession).</summary>
    internal bool FastFlipping => _lastFastFlip != 0 && Environment.TickCount64 - _lastFastFlip < 400;

    /// <summary>
    /// Shift+Down/Up: on to the next sheet (or page) at once, however far the one shown could still
    /// scroll - and, held down, faster and faster: one at a time at first, then 2, 5, 10 and 25.
    /// </summary>
    private void FastFlip(int direction)
    {
        var now = Environment.TickCount64;
        _fastFlips = now - _lastFastFlip < 300 && direction == _fastFlipDirection ? _fastFlips + 1 : 0;
        _fastFlipDirection = direction;
        _lastFastFlip = now;
        var steps = _fastFlips switch
        {
            < 6 => 1,
            < 14 => 2,
            < 24 => 5,
            < 36 => 10,
            _ => 25,
        };
        FlipPage(direction, steps: steps);
    }

    /// <summary>A sheet at a time: whether the sheet being shown can still scroll further that way (it is taller than the window).</summary>
    private bool CanScrollWithinPage(int direction)
    {
        if (_layout is not { Fixed: true } layout) return true;
        var y = _animating ? _targetY : _offsetY;
        var sheet = layout.SheetAt(y);
        return direction > 0 ? y < layout.SheetBottom(sheet) - _viewportHeight - 0.5 : y > layout.SheetTop(sheet) + 0.5;
    }

    /// <summary>A line or a screen down (or up): within the sheet being shown, or on to the next one at its edge.</summary>
    private void StepVertically(double step, bool fromBottomGoingBack)
    {
        var direction = Math.Sign(step);
        if (_layout is { Fixed: true } && !CanScrollWithinPage(direction))
        {
            FlipPage(direction, fromBottom: direction < 0 && fromBottomGoingBack);
            return;
        }
        ScrollBy(0, step, animate: true);
    }

    /// <summary>
    /// Goes to a destination in this document: its page, and the point on it when the destination
    /// names one. <paramref name="flash"/> marks the place for a moment, as a followed link does.
    /// </summary>
    /// <param name="fromPage">The page of the link followed, if one was.</param>
    public async Task NavigateAsync(PdfDestination destination, bool record = true, bool flash = false, int fromPage = -1)
    {
        var document = _document;
        if (document is null) return;

        var resolved = await document.ResolveAsync(destination).ConfigureAwait(true);
        if (resolved is not { } target || !ReferenceEquals(document, _document)) return;
        ShowDestination(target, record, flash, fromPage);
    }

    public void ShowDestination(ResolvedDestination target, bool record = true, bool flash = false, int fromPage = -1)
    {
        if (_layout is null || _document is null) return;
        var page = Math.Clamp(target.PageIndex, 0, _document.PageCount - 1);
        if (record) RecordJump(fromPage);
        _preferredPage = page;

        var layout = _layout;
        var x = XShowing(page, _offsetX);
        double y;
        if (target.X is null && target.Y is null)
        {
            y = layout.Top(page) - layout.Gap;
        }
        else
        {
            // Rotate the named point into the page as shown; an axis left open keeps the view's.
            var (gx, gy) = PageLayout.Rotate(target.X ?? 0, target.Y ?? 0, _rotation);
            var namesShownY = (_rotation & 1) == 0 ? target.Y is not null : target.X is not null;
            var namesShownX = (_rotation & 1) == 0 ? target.X is not null : target.Y is not null;
            y = namesShownY ? layout.Top(page) + (gy * layout.Height(page)) - Px(8) : layout.Top(page) - layout.Gap;
            if (namesShownX && layout.Width(page) > _viewportWidth)
                x = layout.Left(page) + (gx * layout.Width(page)) - Px(8);
        }
        ScrollTo(x, y, animate: false, page);

        // What the link points at must be in sight, not just its page (a /Fit jump shows the top).
        if (flash && target.Area is { } area && area != new PageRect(0, 0, 1, 1)) BringIntoView(page, area);
        if (record) History.Arrived(page);
        if (flash) Flash(page, target);
    }

    /// <summary>Back to the place the last jump left - on another page than this one.</summary>
    public void GoBack() => GoBy(History.StepsAway(-1, _currentPage));

    public void GoForward() => GoBy(History.StepsAway(1, _currentPage));

    /// <summary>Back (negative) or forward through the places links have led, several at once.</summary>
    public void GoBy(int steps)
    {
        if (History.Go(steps, CurrentPosition()) is not { } p) return;
        ScrollToPosition(p, animate: false);
        History.Arrived(p.ShownPage);
    }

    /// <summary>Brings a rectangle on a page into view, centred if it is not already comfortably visible.</summary>
    public void BringIntoView(int page, PageRect rect)
    {
        if (_layout is null || _document is null || (uint)page >= (uint)_layout.Count) return;
        var (rx, ry, rw, rh) = _layout.ToPagePixels(page, rect);
        var left = _layout.Left(page) + rx;
        var top = _layout.Top(page) + ry;

        var margin = Px(40);
        var x = _offsetX;
        var y = _offsetY;
        _preferredPage = page;
        if (top < _offsetY + margin || top + rh > _offsetY + _viewportHeight - margin)
            y = top + (rh / 2) - (_viewportHeight / 2.0);
        if (left < _offsetX + margin || left + rw > _offsetX + _viewportWidth - margin)
            x = left + (rw / 2) - (_viewportWidth / 2.0);
        ScrollTo(x, y, animate: false, page);
    }

    // ===================================================================== scrolling

    /// <param name="page">A sheet at a time: the page whose sheet to stay on; by default the sheet whose slot the offset is in.</param>
    private void ClampOffsets(int? page = null)
    {
        if (_layout is null)
        {
            _offsetX = _offsetY = 0;
            return;
        }
        (_offsetX, _offsetY) = Clamped(_offsetX, _offsetY, page);
    }

    /// <summary>
    /// An offset the view can be at. A sheet at a time, it is inside one sheet's slot - the one
    /// <paramref name="y"/> is in, or <paramref name="page"/>'s - so nothing of another comes into sight.
    /// </summary>
    private (double X, double Y) Clamped(double x, double y, int? page = null)
    {
        if (_layout is not { } layout) return (0, 0);
        var cx = Math.Clamp(x, 0, Math.Max(0, layout.ExtentWidth - _viewportWidth));
        if (layout.Fixed && layout.Count > 0)
        {
            var sheet = page is { } p ? layout.SheetOf(Math.Clamp(p, 0, layout.Count - 1)) : layout.SheetAt(y);
            var top = layout.SheetTop(sheet);
            return (cx, Math.Clamp(y, top, Math.Max(top, layout.SheetBottom(sheet) - _viewportHeight)));
        }
        return (cx, Math.Clamp(y, 0, Math.Max(0, layout.ExtentHeight - _viewportHeight)));
    }

    /// <summary>Scrolls to a document position in device pixels; a sheet at a time, staying on <paramref name="page"/>'s sheet when given.</summary>
    internal void ScrollTo(double x, double y, bool animate, int? page = null)
    {
        if (_layout is null) return;
        var (cx, cy) = Clamped(x, y, page);
        if (animate)
        {
            StartAnimation(cx, cy);
            return;
        }

        StopAnimation();
        if (cy != _offsetY) _lastScrollDirection = Math.Sign(cy - _offsetY);
        _offsetX = cx;
        _offsetY = cy;
        UpdateViewport();
    }

    internal void ScrollBy(double dx, double dy, bool animate)
    {
        var (x, y) = _animating ? (_targetX, _targetY) : (_offsetX, _offsetY);

        // A sheet at a time, scrolling stays on the sheet; only a flip goes to another.
        if (_layout is { Fixed: true } layout && dy != 0)
        {
            var page = layout.PageAt(y);
            (_, var clamped) = Clamped(x, y + dy, page);
            ScrollTo(x + dx, clamped, animate);
            return;
        }
        ScrollTo(x + dx, y + dy, animate);
    }

    private bool _animating;
    private double _targetX;
    private double _targetY;
    private TimeSpan _lastFrame;

    private void StartAnimation(double x, double y)
    {
        _targetX = x;
        _targetY = y;
        if (_animating) return;
        _animating = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnAnimationFrame;
    }

    private void StopAnimation()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnAnimationFrame;
    }

    private void OnAnimationFrame(object? sender, EventArgs e)
    {
        var now = e is RenderingEventArgs r ? r.RenderingTime : TimeSpan.FromTicks(Environment.TickCount64 * 10_000);
        var dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((now - _lastFrame).TotalSeconds, 0, 0.1);
        if (dt == 0) return;
        _lastFrame = now;

        (_targetX, _targetY) = Clamped(_targetX, _targetY);
        var k = 1 - Math.Exp(-dt / 0.055);
        var nx = _offsetX + ((_targetX - _offsetX) * k);
        var ny = _offsetY + ((_targetY - _offsetY) * k);
        var done = Math.Abs(_targetX - nx) < 0.5 && Math.Abs(_targetY - ny) < 0.5;
        if (done)
        {
            nx = _targetX;
            ny = _targetY;
            StopAnimation();
        }

        if (ny != _offsetY) _lastScrollDirection = Math.Sign(ny - _offsetY);
        _offsetX = nx;
        _offsetY = ny;
        UpdateViewport();
    }

    // ===================================================================== IScrollInfo (device-independent pixels)

    public bool CanVerticallyScroll { get; set; } = true;

    public bool CanHorizontallyScroll { get; set; } = true;

    public double ExtentWidth => (_layout?.ExtentWidth ?? 0) / _dpi;

    public double ExtentHeight => (_layout?.ExtentHeight ?? 0) / _dpi;

    public double ViewportWidth => _viewportWidth / _dpi;

    public double ViewportHeight => _viewportHeight / _dpi;

    public double HorizontalOffset => Math.Round(_offsetX) / _dpi;

    public double VerticalOffset => Math.Round(_offsetY) / _dpi;

    public ScrollViewer? ScrollOwner { get; set; }

    private double LineStep => 40 * _dpi;

    public void LineUp() => StepVertically(-LineStep, fromBottomGoingBack: true);

    public void LineDown() => StepVertically(LineStep, fromBottomGoingBack: true);

    public void LineLeft() => ScrollBy(-LineStep, 0, animate: true);

    public void LineRight() => ScrollBy(LineStep, 0, animate: true);

    public void PageUp() => StepVertically(-PageStep(), fromBottomGoingBack: false);

    public void PageDown() => StepVertically(PageStep(), fromBottomGoingBack: false);

    public void PageLeft() => ScrollBy(-_viewportWidth * 0.9, 0, animate: true);

    public void PageRight() => ScrollBy(_viewportWidth * 0.9, 0, animate: true);

    private double PageStep() => Math.Max(LineStep, _viewportHeight - Px(48));

    // The view handles the wheel itself (OnMouseWheel), so these are only reached if it did not.
    public void MouseWheelUp() => ScrollBy(0, -LineStep * 3, animate: true);

    public void MouseWheelDown() => ScrollBy(0, LineStep * 3, animate: true);

    public void MouseWheelLeft() => ScrollBy(-LineStep * 3, 0, animate: true);

    public void MouseWheelRight() => ScrollBy(LineStep * 3, 0, animate: true);

    public void SetHorizontalOffset(double offset) => ScrollTo(offset * _dpi, _animating ? _targetY : _offsetY, animate: false);

    public void SetVerticalOffset(double offset)
    {
        var y = offset * _dpi;
        if (Math.Abs(y - Math.Round(_offsetY)) < 0.5) return;
        ScrollTo(_animating ? _targetX : _offsetX, y, animate: false);
    }

    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;

    // ===================================================================== WPF plumbing

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) =>
        index == 0 ? _root : throw new ArgumentOutOfRangeException(nameof(index));

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;
        var h = double.IsInfinity(availableSize.Height) ? 600 : availableSize.Height;
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var width = (int)Math.Round(finalSize.Width * dpi);
        var height = (int)Math.Round(finalSize.Height * dpi);
        if (width != _viewportWidth || height != _viewportHeight || dpi != _dpi) Resize(width, height, dpi);
        return finalSize;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateArrange();
    }

    private void Resize(int width, int height, double dpi)
    {
        var dpiChanged = dpi != _dpi;
        _rootScale.ScaleX = 1 / dpi;
        _rootScale.ScaleY = 1 / dpi;

        if (_layout is null || _document is null)
        {
            _dpi = dpi;
            _viewportWidth = width;
            _viewportHeight = height;
            ApplyPendingState();
            ScrollOwner?.InvalidateScrollInfo();
            return;
        }

        // Keep the top of the view on the same spot of the same page, through the new layout.
        var position = CurrentPosition();
        var (_, fx, _) = PointAt(_viewportWidth / 2.0, 0);

        _dpi = dpi;
        _viewportWidth = width;
        _viewportHeight = height;
        // A higher DPI can put a custom zoom past what the largest page may be laid out at.
        _zoom = _mode != ZoomMode.Custom ? FitZoom(_mode) : ClampZoom(_zoom);
        Relayout();

        var layout = _layout!;
        var page = position.Page;
        var atTop = Math.Round(_offsetY) <= 0;
        _offsetY = atTop ? 0 : layout.Top(page) + (position.Offset * layout.Height(page));
        _offsetX = layout.Left(page) + (fx * layout.Width(page)) - (width / 2.0);
        ClampOffsets(page);

        if (dpiChanged) ClearPageVisuals();
        BeginSettle();
        UpdateViewport();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Background, null, new Rect(RenderSize));
    }

    private void OnPaletteChanged(object? sender, EventArgs e)
    {
        InvalidateVisual();
        RedrawAllPages();
    }
}
