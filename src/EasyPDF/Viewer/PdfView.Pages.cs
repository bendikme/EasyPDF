using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using EasyPDF.Pdf;

namespace EasyPDF.Viewer;

public sealed partial class PdfView
{
    /// <summary>One page on screen: its paper and tiles, with highlights in a child visual above.</summary>
    private sealed class PageVisual : DrawingVisual
    {
        public PageVisual()
        {
            VisualBitmapScalingMode = BitmapScalingMode.Linear;
            Children.Add(Overlay);
        }

        public int Page = -1;

        /// <summary>The layout the page was last drawn for. Zooming or turning makes a new layout, and the page is drawn again for it.</summary>
        public PageLayout? DrawnFor;

        public readonly DrawingVisual Overlay = new();

        public void Clear()
        {
            Page = -1;
            DrawnFor = null;
            using (RenderOpen()) { }
            using (Overlay.RenderOpen()) { }
        }
    }

    private static readonly Brush ShadowNear = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)));
    private static readonly Brush ShadowFar = Frozen(new SolidColorBrush(Color.FromArgb(0x14, 0, 0, 0)));

    /// <summary>The pages on screen; above them, a sheet turned from while it fades out.</summary>
    private readonly ContainerVisual _pageLayer = new();

    private readonly ContainerVisual _fadeLayer = new();

    private readonly Dictionary<int, PageVisual> _visuals = new();
    private readonly Stack<PageVisual> _spareVisuals = new();
    private readonly HashSet<TileKey> _onScreen = new();
    private readonly HashSet<int> _visiblePages = new();
    private readonly List<TileRequest> _requests = new();
    private int _firstVisible = -1;
    private int _lastVisible = -1;

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private long ScaleBits => BitConverter.DoubleToInt64Bits(_layout?.Scale ?? 0);

    /// <summary>
    /// Brings the screen up to date with the scroll position: places the page visuals, works out
    /// which tiles are wanted and hands the plan to the renderer. Cheap enough to run every frame.
    /// </summary>
    private void UpdateViewport()
    {
        var layout = _layout;
        if (layout is null || _document is null || _viewportWidth <= 0 || _viewportHeight <= 0)
        {
            ClearPageVisuals();
            ScrollOwner?.InvalidateScrollInfo();
            return;
        }

        var ox = Math.Round(_offsetX);
        var oy = Math.Round(_offsetY);
        var (first, last) = layout.VisibleRange(oy, oy + _viewportHeight);

        // A sheet at a time, a turn to another sheet: the one turned from fades out over the new one.
        var sheet = layout.Fixed ? layout.SheetAt(oy) : -1;
        var turned = layout.Fixed && ReferenceEquals(layout, _shownLayout) && sheet != _shownSheet;
        var fade = turned && TurnsFade && Stopwatch.GetElapsedTime(_lastTurn).TotalMilliseconds >= RapidTurnMilliseconds;
        if (turned)
        {
            EndTurnFade();
            _lastTurn = Stopwatch.GetTimestamp();
        }
        _shownLayout = layout;
        _shownSheet = sheet;

        // Visuals of pages that scrolled away go back to the pool - or, turned from, fade first.
        if (_visuals.Count > 0)
        {
            List<int>? gone = null;
            foreach (var page in _visuals.Keys)
            {
                if (page < first || page > last) (gone ??= new List<int>()).Add(page);
            }
            if (gone is not null)
            {
                foreach (var page in gone)
                {
                    var visual = _visuals[page];
                    _visuals.Remove(page);
                    _pageLayer.Children.Remove(visual);
                    if (fade)
                    {
                        // Where it was on screen; its offset is relative to the view already.
                        _fadeLayer.Children.Add(visual);
                        continue;
                    }
                    visual.Clear();
                    if (_spareVisuals.Count < 8) _spareVisuals.Push(visual);
                }
            }
        }
        if (fade && _fadeLayer.Children.Count > 0) StartTurnFade();

        _firstVisible = first;
        _lastVisible = last;
        _visiblePages.Clear();
        if (first >= 0)
        {
            for (var page = first; page <= last; page++)
            {
                _visiblePages.Add(page);
                if (!_visuals.TryGetValue(page, out var visual))
                {
                    visual = _spareVisuals.Count > 0 ? _spareVisuals.Pop() : new PageVisual();
                    visual.Page = page;
                    _visuals[page] = visual;
                    _pageLayer.Children.Add(visual);
                    DrawPage(visual);
                    DrawOverlay(visual);
                }
                else if (!ReferenceEquals(visual.DrawnFor, layout))
                {
                    // The zoom, rotation or width changed: paper, stand-in tiles and highlights
                    // are drawn again for the new size straight away, not when new tiles come.
                    DrawPage(visual);
                    DrawOverlay(visual);
                }
                visual.Offset = new Vector(layout.Left(page) - ox, layout.Top(page) - oy);
            }
        }

        var current = layout.MostVisible(ox, oy, ox + _viewportWidth, oy + _viewportHeight);
        if (layout.Fixed && (uint)_preferredPage < (uint)layout.Count)
        {
            // A sheet at a time, the page gone to stays the current one while it is as much in view as any.
            if (layout.SheetOf(_preferredPage) != layout.SheetOf(current)) _preferredPage = -1;
            else if (layout.SeenOf(_preferredPage, ox, oy, ox + _viewportWidth, oy + _viewportHeight) >=
                     layout.SeenOf(current, ox, oy, ox + _viewportWidth, oy + _viewportHeight) - 0.005)
                current = _preferredPage;
        }
        var pageChanged = current != _currentPage;
        _currentPage = current;

        if (!_settling) PlanTiles(layout, ox, oy);
        RequestVisiblePageInfo();

        // The page moved under a still pointer (wheel, keys): the cursor and the link address follow.
        RefreshPointer();

        ScrollOwner?.InvalidateScrollInfo();
        if (pageChanged) ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearPageVisuals()
    {
        EndTurnFade();
        foreach (var visual in _visuals.Values)
        {
            _pageLayer.Children.Remove(visual);
            visual.Clear();
        }
        _visuals.Clear();
        _spareVisuals.Clear();
        _firstVisible = _lastVisible = -1;
        _shownLayout = null;
        _shownSheet = -1;
    }

    // ===================================================================== turning a sheet

    /// <summary>How long the sheet turned from takes to fade out.</summary>
    private const int TurnFadeMilliseconds = 130;

    /// <summary>Turns closer together than this (a key held down, the scroll bar dragged) cut over without a fade.</summary>
    private const int RapidTurnMilliseconds = 250;

    private PageLayout? _shownLayout;
    private int _shownSheet = -1;
    private long _lastTurn;
    private long _fadeStart;
    private bool _fading;

    /// <summary>Whether turns fade: not when Windows is set to show no animations.</summary>
    internal static bool TurnsFade => SystemParameters.ClientAreaAnimation;

    /// <summary>Whether a sheet turned from is fading out now. For tests.</summary>
    internal bool TurnFading => _fading;

    private void StartTurnFade()
    {
        _fadeStart = Stopwatch.GetTimestamp();
        _fadeLayer.Opacity = 1;
        if (_fading) return;
        _fading = true;
        CompositionTarget.Rendering += OnTurnFadeFrame;
    }

    private void OnTurnFadeFrame(object? sender, EventArgs e)
    {
        var t = Stopwatch.GetElapsedTime(_fadeStart).TotalMilliseconds / TurnFadeMilliseconds;
        if (t >= 1)
        {
            EndTurnFade();
            return;
        }
        // Eased at both ends, so neither the start nor the end of it shows as a step.
        _fadeLayer.Opacity = 1 - (t * t * (3 - (2 * t)));
    }

    /// <summary>Takes the sheet turned from away, faded or not, and its visuals back to the pool.</summary>
    private void EndTurnFade()
    {
        if (_fading)
        {
            _fading = false;
            CompositionTarget.Rendering -= OnTurnFadeFrame;
        }
        if (_fadeLayer.Children.Count == 0) return;
        var visuals = new List<PageVisual>(_fadeLayer.Children.Count);
        foreach (var child in _fadeLayer.Children)
            if (child is PageVisual visual) visuals.Add(visual);
        _fadeLayer.Children.Clear();
        _fadeLayer.Opacity = 1;
        foreach (var visual in visuals)
        {
            visual.Clear();
            if (_spareVisuals.Count < 8) _spareVisuals.Push(visual);
        }
    }

    private void RedrawAllPages()
    {
        foreach (var visual in _visuals.Values)
        {
            DrawPage(visual);
            DrawOverlay(visual);
        }
    }

    private void RedrawOverlays()
    {
        foreach (var visual in _visuals.Values) DrawOverlay(visual);
    }

    /// <summary>
    /// Draws a page: shadow, paper, then its tiles. Tiles at the current scale are drawn last,
    /// over any held at another scale, which stand in - stretched - while the new ones render.
    /// </summary>
    private void DrawPage(PageVisual visual)
    {
        var layout = _layout;
        if (layout is null || visual.Page < 0 || visual.Page >= layout.Count) return;

        var page = visual.Page;
        var w = layout.Width(page);
        var h = layout.Height(page);
        var scaleBits = ScaleBits;
        var scale = layout.Scale;
        visual.DrawnFor = layout;

        using var dc = visual.RenderOpen();
        var edge = Math.Max(1, Px(1));
        dc.DrawRectangle(ShadowFar, null, new Rect(-edge * 2, -edge, w + (edge * 4), h + (edge * 4)));
        dc.DrawRectangle(ShadowNear, null, new Rect(-edge, 0, w + (edge * 2), h + (edge * 2)));
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));

        var tiles = _tiles.TilesFor(page);
        if (tiles.Count == 0) return;

        // Stand-ins first, the nearest scale last so it ends up on top.
        List<Tile>? others = null;
        foreach (var tile in tiles)
        {
            if (tile.Key.ScaleBits != scaleBits && tile.Key.Rotation == layout.Rotation && tile.Bitmap is not null)
                (others ??= new List<Tile>()).Add(tile);
        }
        if (others is not null)
        {
            others.Sort((a, b) => Math.Abs(Math.Log(ScaleOf(b) / scale)).CompareTo(Math.Abs(Math.Log(ScaleOf(a) / scale))));
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
            foreach (var tile in others)
            {
                var f = scale / ScaleOf(tile);
                dc.DrawImage(tile.Bitmap, new Rect(tile.X * f, tile.Y * f, tile.Width * f, tile.Height * f));
            }
            dc.Pop();
        }

        foreach (var tile in tiles)
        {
            if (tile.Key.ScaleBits != scaleBits || tile.Key.Rotation != layout.Rotation || tile.Bitmap is null) continue;
            dc.DrawImage(tile.Bitmap, new Rect(tile.X, tile.Y, tile.Width, tile.Height));
            _tiles.Touch(tile);
        }
    }

    private static double ScaleOf(Tile tile) => BitConverter.Int64BitsToDouble(tile.Key.ScaleBits);

    /// <summary>
    /// Works out the tiles wanted - those on screen nearest the middle first, then a screen's
    /// worth ahead in the direction of reading and half a screen behind - and hands them over.
    /// A sheet at a time, what is ahead and behind is the next sheet and the one before, as a turn
    /// would show them, and the one after next: turning shows a sheet that is drawn already.
    /// </summary>
    private void PlanTiles(PageLayout layout, double ox, double oy)
    {
        var document = _document!;
        _requests.Clear();
        _onScreen.Clear();

        var generation = _tiles.Generation;
        var rotation = layout.Rotation;
        var scaleBits = ScaleBits;
        var centreX = ox + (_viewportWidth / 2.0);
        var centreY = oy + (_viewportHeight / 2.0);

        // On screen.
        var visibleCount = 0;
        if (_firstVisible >= 0)
        {
            for (var page = _firstVisible; page <= _lastVisible; page++)
                AddTiles(page, ox, oy, ox + _viewportWidth, oy + _viewportHeight, visible: true);
        }
        visibleCount = _requests.Count;
        _requests.Sort(0, visibleCount, Comparer<TileRequest>.Create((a, b) =>
            Distance(a).CompareTo(Distance(b))));

        // Ahead and behind, only once nothing on screen is missing.
        if (visibleCount == 0 && layout.Fixed && layout.SheetCount > 1)
        {
            var sheet = layout.SheetAt(oy);
            var forward = _lastScrollDirection >= 0;
            ReadOnlySpan<int> order = forward ? [sheet + 1, sheet - 1, sheet + 2] : [sheet - 1, sheet + 1, sheet - 2];
            foreach (var s in order)
            {
                if ((uint)s >= (uint)layout.SheetCount) continue;
                // Where a turn lands: a sheet ahead at its top, one behind at its foot (a line up goes back to it).
                var top = s > sheet ? layout.SheetTop(s) : Math.Max(layout.SheetTop(s), layout.SheetBottom(s) - _viewportHeight);
                var pageOne = layout.FirstOfSheet(s);
                var left = Math.Round(Math.Clamp(XShowing(pageOne, ox), 0, Math.Max(0, layout.ExtentWidth - _viewportWidth)));
                for (var page = pageOne; page <= layout.LastOfSheet(s); page++)
                    AddTiles(page, left, top, left + _viewportWidth, top + _viewportHeight, visible: false);
            }
        }
        else if (visibleCount == 0)
        {
            var ahead = _viewportHeight;
            var behind = _viewportHeight / 2.0;
            var (top, bottom) = _lastScrollDirection >= 0
                ? (oy - behind, oy + _viewportHeight + ahead)
                : (oy - ahead, oy + _viewportHeight + behind);
            var (pf, pl) = layout.VisibleRange(Math.Max(0, top), bottom);
            if (pf >= 0)
            {
                for (var page = pf; page <= pl; page++)
                    AddTiles(page, ox, top, ox + _viewportWidth, bottom, visible: false);
            }
            _requests.Sort(Comparer<TileRequest>.Create((a, b) => Distance(a).CompareTo(Distance(b))));
        }

        // Keep about six screens of tiles: what is on screen, what is ahead, and enough to scroll
        // back over without waiting. Older tiles go first.
        var screen = (long)_viewportWidth * _viewportHeight * 4;
        _tiles.Budget = Math.Clamp(screen * 6, 32L << 20, 256L << 20);

        _tiles.SetPlan(_requests);

        if (_tiles.Trim(_onScreen) is { } trimmed)
        {
            foreach (var page in trimmed)
                if (_visuals.TryGetValue(page, out var visual)) DrawPage(visual);
        }

        double Distance(TileRequest r)
        {
            var x = layout.Left(r.Key.Page) + r.X + (r.Width / 2.0) - centreX;
            var y = layout.Top(r.Key.Page) + r.Y + (r.Height / 2.0) - centreY;
            return (x * x) + (y * y);
        }

        void AddTiles(int page, double x0, double y0, double x1, double y1, bool visible)
        {
            var left = layout.Left(page);
            var top = layout.Top(page);
            var w = layout.Width(page);
            var h = layout.Height(page);

            var ix0 = (int)Math.Max(0, Math.Floor(x0 - left));
            var iy0 = (int)Math.Max(0, Math.Floor(y0 - top));
            var ix1 = (int)Math.Min(w, Math.Ceiling(x1 - left));
            var iy1 = (int)Math.Min(h, Math.Ceiling(y1 - top));
            if (ix1 <= ix0 || iy1 <= iy0) return;

            const int T = TileRenderer.TileSize;
            for (var row = iy0 / T; row <= (iy1 - 1) / T; row++)
            {
                for (var col = ix0 / T; col <= (ix1 - 1) / T; col++)
                {
                    var key = new TileKey(generation, page, rotation, scaleBits, col, row);
                    if (_tiles.TryGet(key, out var held))
                    {
                        if (visible)
                        {
                            _onScreen.Add(key);
                            _tiles.Touch(held);
                        }
                        continue;
                    }

                    var tx = col * T;
                    var ty = row * T;
                    _requests.Add(new TileRequest(key, w, h, tx, ty, Math.Min(T, w - tx), Math.Min(T, h - ty), visible));
                    if (visible) _onScreen.Add(key);
                }
            }
        }
    }

    private void OnTilesArrived(IReadOnlyCollection<int> pages)
    {
        var layout = _layout;
        if (layout is null) return;

        foreach (var page in pages)
        {
            if (!_visuals.TryGetValue(page, out var visual)) continue;
            // Once a page is complete at this scale, the stand-ins it held go.
            if (IsComplete(page)) _tiles.DropOtherScales(page, layout.Rotation, ScaleBits);
            DrawPage(visual);
        }

        // Tiles of other scales on pages that are no longer on screen are of no further use.
        _tiles.DropStaleOutside(_visiblePages, layout.Rotation, ScaleBits);

        if (!_settling) PlanTiles(layout, Math.Round(_offsetX), Math.Round(_offsetY));
        if (ScreenRendered is { } handler && IsScreenComplete()) handler(this, EventArgs.Empty);
    }

    /// <summary>Raised when the last missing tile on screen arrives.</summary>
    public event EventHandler? ScreenRendered;

    /// <summary>Whether every tile of the page's visible part is held at the current scale (true when none of it is visible).</summary>
    private bool IsComplete(int page)
    {
        var layout = _layout!;
        var ox = Math.Round(_offsetX);
        var oy = Math.Round(_offsetY);
        var left = layout.Left(page);
        var top = layout.Top(page);
        var ix0 = (int)Math.Max(0, ox - left);
        var iy0 = (int)Math.Max(0, oy - top);
        var ix1 = (int)Math.Min(layout.Width(page), ox + _viewportWidth - left);
        var iy1 = (int)Math.Min(layout.Height(page), oy + _viewportHeight - top);
        // Nothing of the page is in view (it is beside the viewport): nothing to wait for.
        if (ix1 <= ix0 || iy1 <= iy0) return true;

        const int T = TileRenderer.TileSize;
        var generation = _tiles.Generation;
        var scaleBits = ScaleBits;
        for (var row = iy0 / T; row <= (iy1 - 1) / T; row++)
        {
            for (var col = ix0 / T; col <= (ix1 - 1) / T; col++)
            {
                if (!_tiles.TryGet(new TileKey(generation, page, layout.Rotation, scaleBits, col, row), out _)) return false;
            }
        }
        return true;
    }

    /// <summary>Whether a page has tiles at the current scale and rotation, on screen or not. For tests.</summary>
    internal bool HasTilesAtScale(int page) =>
        _layout is { } layout && _tiles.TilesFor(page).Any(t => t.Key.ScaleBits == ScaleBits && t.Key.Rotation == layout.Rotation && t.Key.Generation == _tiles.Generation);

    /// <summary>What each page on screen draws: its paper and tile images. For tests.</summary>
    internal IEnumerable<(int Page, Vector Offset, DrawingGroup? Drawing)> PageDrawings() =>
        _visuals.Values.Select(v => (v.Page, v.Offset, (DrawingGroup?)VisualTreeHelper.GetDrawing(v)));

    /// <summary>Each page on screen with the layout it was drawn for, and its highlights. For tests.</summary>
    internal IEnumerable<(int Page, PageLayout? DrawnFor, DrawingGroup? Overlay)> PageOverlays() =>
        _visuals.Values.Select(v => (v.Page, v.DrawnFor, (DrawingGroup?)VisualTreeHelper.GetDrawing(v.Overlay)));

    /// <summary>What is on screen and what is missing, for a test that waited in vain.</summary>
    internal string DescribeScreen()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"settling={_settling} busy={_tiles.IsBusy} held={_tiles.CachedCount} bytes={_tiles.CachedBytes >> 20}MiB budget={_tiles.Budget >> 20}MiB ");
        sb.Append($"viewport={_viewportWidth}x{_viewportHeight} offset=({_offsetX:0},{_offsetY:0}) pages {_firstVisible}-{_lastVisible} scale={_layout?.Scale:0.###} rotation={_rotation}");
        if (_layout is null || _firstVisible < 0) return sb.ToString();
        const int T = TileRenderer.TileSize;
        var layout = _layout;
        var ox = Math.Round(_offsetX);
        var oy = Math.Round(_offsetY);
        for (var page = _firstVisible; page <= _lastVisible; page++)
        {
            var ix0 = (int)Math.Max(0, ox - layout.Left(page));
            var iy0 = (int)Math.Max(0, oy - layout.Top(page));
            var ix1 = (int)Math.Min(layout.Width(page), ox + _viewportWidth - layout.Left(page));
            var iy1 = (int)Math.Min(layout.Height(page), oy + _viewportHeight - layout.Top(page));
            sb.Append($"; page {page}: {layout.Width(page)}x{layout.Height(page)} visible [{ix0},{iy0})-[{ix1},{iy1}) missing:");
            if (ix1 <= ix0 || iy1 <= iy0)
            {
                sb.Append(" (nothing visible)");
                continue;
            }
            for (var row = iy0 / T; row <= (iy1 - 1) / T; row++)
            for (var col = ix0 / T; col <= (ix1 - 1) / T; col++)
                if (!_tiles.TryGet(new TileKey(_tiles.Generation, page, layout.Rotation, ScaleBits, col, row), out _)) sb.Append($" ({col},{row})");
        }
        return sb.ToString();
    }

    /// <summary>Whether everything on screen is drawn at the current scale. For tests and benchmarks.</summary>
    internal bool IsScreenComplete()
    {
        if (_layout is null || _firstVisible < 0 || _settling) return false;
        for (var page = _firstVisible; page <= _lastVisible; page++)
        {
            if (!IsComplete(page)) return false;
        }
        return true;
    }
}
