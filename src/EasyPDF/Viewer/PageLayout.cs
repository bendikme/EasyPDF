using EasyPDF.Pdf;

namespace EasyPDF.Viewer;

/// <summary>
/// Where every page sits, in device pixels, at one scale and rotation: pages stacked top to
/// bottom; a page that fits the viewport's width is centred in it, a wider one starts at the left
/// margin and the document scrolls sideways to it. (Centring every page in the widest page's
/// width instead would push ordinary pages off screen in a document with one huge page.) Everything is in whole device pixels so that a
/// rendered tile lands exactly on the screen's pixels and is never resampled.
///
/// Positions are relative to the top left of the whole document. They can run past two billion
/// for a long document at a high zoom, so the vertical ones are doubles; nothing that large ever
/// reaches WPF, which only sees positions relative to the viewport.
///
/// A sheet at a time (one page, or a grid of them - see <see cref="PageGrid"/>), each sheet has a
/// slot of its own at least a screen tall, the sheet centred in it: the view is kept inside one
/// slot, so nothing of another sheet is ever in sight, and moving on is moving to the next slot.
/// The cells of a sheet are as wide as the widest page in their column and as tall as the tallest
/// in their row, each page centred in its cell; an empty cell (beside a cover, at the end) is as
/// large as the sheet's largest, so pages keep their places. Pages keep their place in one
/// coordinate space either way, so links, search, selection and history work the same in both.
/// Continuous, every page is a sheet of its own, so the same questions have answers there too.
/// </summary>
public sealed class PageLayout
{
    /// <summary>Points to device-independent pixels: a page at 100% is its real size.</summary>
    public const double PointsToDip = 96.0 / 72.0;

    /// <summary>No page dimension is laid out larger than this many pixels.</summary>
    public const int MaxPagePixels = 1 << 20;

    private readonly int[] _widths;
    private readonly int[] _heights;
    private readonly int[] _lefts;
    private readonly double[] _tops;

    /// <summary>A sheet at a time: where each sheet's slot begins, and the end of the last.</summary>
    private readonly double[]? _slotTops;

    /// <summary>A sheet at a time: each sheet's left edge and width, its empty cells included.</summary>
    private readonly int[]? _sheetLefts;

    private readonly int[]? _sheetWidths;

    /// <param name="slotHeight">A sheet at a time: the height of the viewport, the least a sheet's slot is. 0 for one continuous column.</param>
    /// <param name="grid">A sheet at a time: the pages on a sheet; one by default.</param>
    public PageLayout(IReadOnlyList<PageSize> sizes, double scale, int rotation, int gap, int margin, int viewportWidth, int slotHeight = 0,
        PageGrid grid = default)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        if (!(scale > 0) || !double.IsFinite(scale)) throw new ArgumentOutOfRangeException(nameof(scale));

        Count = sizes.Count;
        Scale = scale;
        Rotation = ((rotation % 4) + 4) % 4;
        Gap = Math.Max(0, gap);
        Margin = Math.Max(0, margin);
        Grid = grid.Columns == 0 ? PageGrid.One : grid;

        _widths = new int[Count];
        _heights = new int[Count];
        _lefts = new int[Count];
        _tops = new double[Count];

        var swap = (Rotation & 1) == 1;
        var widest = 0;
        for (var i = 0; i < Count; i++)
        {
            var size = sizes[i];
            var w = swap ? size.Height : size.Width;
            var h = swap ? size.Width : size.Height;
            _widths[i] = Pixels(w * scale);
            _heights[i] = Pixels(h * scale);
            widest = Math.Max(widest, _widths[i]);
        }

        var viewport = Math.Max(0, viewportWidth);
        if (slotHeight > 0)
        {
            SheetCount = Count == 0 ? 0 : ((Count - 1 + Grid.Lead) / Grid.PerSheet) + 1;
            _slotTops = new double[SheetCount + 1];
            _sheetLefts = new int[SheetCount];
            _sheetWidths = new int[SheetCount];
            widest = LayOutSheets(slotHeight, viewport);
        }
        else
        {
            SheetCount = Count;
            double y = Margin;
            for (var i = 0; i < Count; i++)
            {
                _tops[i] = y;
                y += _heights[i] + (i < Count - 1 ? Gap : 0);
                _lefts[i] = _widths[i] + (2 * Margin) <= viewport ? (viewport - _widths[i]) / 2 : Margin;
            }
            ExtentHeight = Count == 0 ? 0 : y + Margin;
        }

        ContentWidth = widest + (2 * Margin);
        ExtentWidth = Math.Max(viewport, ContentWidth);
    }

    /// <summary>Places every sheet in its slot and every page in its cell. Returns the widest sheet's width.</summary>
    private int LayOutSheets(int slotHeight, int viewport)
    {
        var columns = Grid.Columns;
        var rows = Grid.Rows;
        var cellWidths = new int[columns];
        var cellHeights = new int[rows];
        var cellLefts = new int[columns];
        var cellTops = new int[rows];
        var widest = 0;
        double slot = 0;

        for (var sheet = 0; sheet < SheetCount; sheet++)
        {
            Array.Clear(cellWidths);
            Array.Clear(cellHeights);
            var first = FirstOfSheet(sheet);
            var last = LastOfSheet(sheet);
            for (var page = first; page <= last; page++)
            {
                var (column, row) = CellOf(page);
                cellWidths[column] = Math.Max(cellWidths[column], _widths[page]);
                cellHeights[row] = Math.Max(cellHeights[row], _heights[page]);
            }

            var largestWidth = cellWidths.Max();
            var largestHeight = cellHeights.Max();
            int sheetWidth = (columns - 1) * Gap, sheetHeight = (rows - 1) * Gap;
            for (var c = 0; c < columns; c++)
            {
                if (cellWidths[c] == 0) cellWidths[c] = largestWidth;
                cellLefts[c] = c == 0 ? 0 : cellLefts[c - 1] + cellWidths[c - 1] + Gap;
                sheetWidth += cellWidths[c];
            }
            for (var r = 0; r < rows; r++)
            {
                if (cellHeights[r] == 0) cellHeights[r] = largestHeight;
                cellTops[r] = r == 0 ? 0 : cellTops[r - 1] + cellHeights[r - 1] + Gap;
                sheetHeight += cellHeights[r];
            }

            // The sheet in the middle of its slot, or from its top margin when taller; across, like a page.
            var height = Math.Max(slotHeight, sheetHeight + (2 * Margin));
            var top = slot + Math.Max(Margin, (height - sheetHeight) / 2);
            var left = sheetWidth + (2 * Margin) <= viewport ? (viewport - sheetWidth) / 2 : Margin;
            _slotTops![sheet] = slot;
            _sheetLefts![sheet] = left;
            _sheetWidths![sheet] = sheetWidth;
            widest = Math.Max(widest, sheetWidth);

            for (var page = first; page <= last; page++)
            {
                var (column, row) = CellOf(page);
                _lefts[page] = left + cellLefts[column] + ((cellWidths[column] - _widths[page]) / 2);
                _tops[page] = top + cellTops[row] + ((cellHeights[row] - _heights[page]) / 2);
            }
            slot += height;
        }

        _slotTops![SheetCount] = slot;
        ExtentHeight = slot;
        return widest;
    }

    public int Count { get; }

    /// <summary>Device pixels per PDF point.</summary>
    public double Scale { get; }

    /// <summary>The viewer's rotation in quarter turns clockwise, 0-3.</summary>
    public int Rotation { get; }

    public int Gap { get; }

    public int Margin { get; }

    /// <summary>Widest page (or sheet) plus margins.</summary>
    public int ContentWidth { get; }

    /// <summary>The scrollable width: the content, or the viewport when that is wider.</summary>
    public int ExtentWidth { get; }

    public double ExtentHeight { get; private set; }

    /// <summary>Whether this is the layout of a sheet at a time.</summary>
    public bool Fixed => _slotTops is not null;

    /// <summary>The pages on a sheet. Continuous, one.</summary>
    public PageGrid Grid { get; }

    /// <summary>A sheet at a time, how many sheets; continuous, how many pages.</summary>
    public int SheetCount { get; }

    /// <summary>The sheet <paramref name="page"/> is on.</summary>
    public int SheetOf(int page) => Fixed ? (page + Grid.Lead) / Grid.PerSheet : page;

    /// <summary>The first page on a sheet.</summary>
    public int FirstOfSheet(int sheet) => Fixed ? Math.Max(0, (sheet * Grid.PerSheet) - Grid.Lead) : sheet;

    /// <summary>The last page on a sheet.</summary>
    public int LastOfSheet(int sheet) => Fixed ? Math.Min(Count - 1, ((sheet + 1) * Grid.PerSheet) - Grid.Lead - 1) : sheet;

    /// <summary>Where a page is on its sheet: column and row.</summary>
    private (int Column, int Row) CellOf(int page)
    {
        var cell = (page + Grid.Lead) % Grid.PerSheet;
        return (cell % Grid.Columns, cell / Grid.Columns);
    }

    /// <summary>A sheet at a time: where a sheet's slot begins. Continuous: the page's top less the gap above it.</summary>
    public double SheetTop(int sheet) => _slotTops?[sheet] ?? (sheet == 0 ? 0 : _tops[sheet] - Gap);

    /// <summary>A sheet at a time: where a sheet's slot ends. Continuous: the page's bottom plus the gap below it.</summary>
    public double SheetBottom(int sheet) => _slotTops?[sheet + 1] ?? (sheet == Count - 1 ? ExtentHeight : _tops[sheet + 1] - Gap);

    /// <summary>Where the slot of <paramref name="page"/>'s sheet begins (see <see cref="SheetTop"/>).</summary>
    public double SlotTop(int page) => SheetTop(SheetOf(page));

    /// <summary>Where the slot of <paramref name="page"/>'s sheet ends (see <see cref="SheetBottom"/>).</summary>
    public double SlotBottom(int page) => SheetBottom(SheetOf(page));

    /// <summary>A sheet's left edge and width across all its cells; continuous, the page's own.</summary>
    public (int Left, int Width) SheetSpan(int sheet) =>
        _sheetLefts is null ? (_lefts[sheet], _widths[sheet]) : (_sheetLefts[sheet], _sheetWidths![sheet]);

    /// <summary>The sheet whose slot holds <paramref name="y"/>; the first or last outside them all. Continuous, the page at it.</summary>
    public int SheetAt(double y)
    {
        if (Count == 0) return -1;
        if (_slotTops is null) return PageAt(y);
        int lo = 0, hi = SheetCount - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >>> 1;
            if (_slotTops[mid] <= y) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    public int Width(int page) => _widths[page];

    public int Height(int page) => _heights[page];

    public int Left(int page) => _lefts[page];

    public double Top(int page) => _tops[page];

    public double Bottom(int page) => _tops[page] + _heights[page];

    private static int Pixels(double value) => (int)Math.Clamp(Math.Round(value), 1, MaxPagePixels);

    /// <summary>
    /// The page at a vertical position. In the gap between two pages it is the nearer of them;
    /// above the first or below the last it is that page. A sheet at a time, the first page of the
    /// sheet there.
    /// </summary>
    public int PageAt(double y)
    {
        if (Count == 0) return -1;
        if (Fixed) return FirstOfSheet(SheetAt(y));
        var index = FirstEndingAfter(y);
        if (index >= Count) return Count - 1;
        if (index > 0 && y < _tops[index])
        {
            // In the gap above page "index": whichever edge is closer.
            var above = Bottom(index - 1);
            return y - above <= _tops[index] - y ? index - 1 : index;
        }
        return index;
    }

    /// <summary>The page at a point: the one under it, or the nearest. Continuous, as <see cref="PageAt(double)"/>.</summary>
    public int PageAt(double x, double y)
    {
        if (!Fixed || Count == 0) return PageAt(y);
        var sheet = SheetAt(y);
        var best = FirstOfSheet(sheet);
        var bestDistance = double.MaxValue;
        for (var page = best; page <= LastOfSheet(sheet); page++)
        {
            var dx = Math.Max(0, Math.Max(_lefts[page] - x, x - (_lefts[page] + _widths[page])));
            var dy = Math.Max(0, Math.Max(_tops[page] - y, y - Bottom(page)));
            var distance = (dx * dx) + (dy * dy);
            if (distance < bestDistance)
            {
                best = page;
                bestDistance = distance;
            }
        }
        return best;
    }

    /// <summary>
    /// The first and last page that overlap [top, bottom), or (-1, -1) when none do. A sheet at a
    /// time, a page between the two may be beside the band rather than in it.
    /// </summary>
    public (int First, int Last) VisibleRange(double top, double bottom)
    {
        if (Count == 0 || bottom <= top) return (-1, -1);
        if (Fixed)
        {
            var s0 = SheetAt(top);
            var s1 = SheetAt(bottom);
            if (s1 > s0 && _slotTops![s1] >= bottom) s1--;
            int from = -1, to = -1;
            for (var page = FirstOfSheet(s0); page <= LastOfSheet(s1); page++)
            {
                if (_tops[page] >= bottom || Bottom(page) <= top) continue;
                if (from < 0) from = page;
                to = page;
            }
            return (from, to);
        }

        var first = FirstEndingAfter(top);
        if (first >= Count || _tops[first] >= bottom) return (-1, -1);

        var last = first;
        while (last + 1 < Count && _tops[last + 1] < bottom) last++;
        return (first, last);
    }

    /// <summary>The page showing most of the band [top, bottom); the earlier one on a tie.</summary>
    public int MostVisible(double top, double bottom) => MostVisible(double.MinValue, top, double.MaxValue, bottom);

    /// <summary>
    /// The page showing most in the view [left, right) x [top, bottom): continuous, the most of its
    /// height; a sheet at a time, the most of itself - so a sheet seen whole is at its first page,
    /// and a page zoomed into is that page. The earlier one on a tie.
    /// </summary>
    public int MostVisible(double left, double top, double right, double bottom)
    {
        var (first, last) = VisibleRange(top, bottom);
        if (first < 0) return PageAt((top + bottom) / 2);

        var best = first;
        double bestSeen = -1;
        for (var i = first; i <= last; i++)
        {
            double seen;
            if (Fixed)
            {
                var across = Math.Min(right, _lefts[i] + _widths[i]) - Math.Max(left, _lefts[i]);
                var down = Math.Min(bottom, Bottom(i)) - Math.Max(top, _tops[i]);
                if (across <= 0 || down <= 0) continue;
                seen = across * down / ((double)_widths[i] * _heights[i]);
                if (seen <= bestSeen + 0.005) continue;
            }
            else
            {
                seen = Math.Min(bottom, Bottom(i)) - Math.Max(top, _tops[i]);
                if (seen <= bestSeen + 0.5) continue;
            }
            best = i;
            bestSeen = seen;
        }
        return best;
    }

    /// <summary>Index of the first page whose bottom is below <paramref name="y"/>; Count if none.</summary>
    private int FirstEndingAfter(double y)
    {
        int lo = 0, hi = Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (Bottom(mid) <= y) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    // ------------------------------------------------------------------ rotation of page fractions

    /// <summary>A point in page fractions (as the PDF displays the page) to fractions of the page as shown here.</summary>
    public static (double X, double Y) Rotate(double x, double y, int rotation) => (((rotation % 4) + 4) % 4) switch
    {
        1 => (1 - y, x),
        2 => (1 - x, 1 - y),
        3 => (y, 1 - x),
        _ => (x, y),
    };

    /// <summary>The inverse of <see cref="Rotate"/>.</summary>
    public static (double X, double Y) Unrotate(double x, double y, int rotation) => (((rotation % 4) + 4) % 4) switch
    {
        1 => (y, 1 - x),
        2 => (1 - x, 1 - y),
        3 => (1 - y, x),
        _ => (x, y),
    };

    /// <summary>A page-fraction rectangle to pixels inside page <paramref name="page"/>, relative to its top left.</summary>
    public (double X, double Y, double Width, double Height) ToPagePixels(int page, PageRect rect)
    {
        var (ax, ay) = Rotate(rect.X0, rect.Y0, Rotation);
        var (bx, by) = Rotate(rect.X1, rect.Y1, Rotation);
        var w = _widths[page];
        var h = _heights[page];
        var x0 = Math.Min(ax, bx) * w;
        var y0 = Math.Min(ay, by) * h;
        return (x0, y0, (Math.Max(ax, bx) * w) - x0, (Math.Max(ay, by) * h) - y0);
    }
}
