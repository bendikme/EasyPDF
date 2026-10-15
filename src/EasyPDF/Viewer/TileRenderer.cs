using System.Collections.Concurrent;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EasyPDF.Pdf;
using EasyPDF.Services;

namespace EasyPDF.Viewer;

/// <summary>A square of a page at one scale and rotation. Generation tells documents apart across a reload.</summary>
public readonly record struct TileKey(int Generation, int Page, int Rotation, long ScaleBits, int Col, int Row);

/// <summary>A rendered tile. Its bitmap is frozen, so any thread may draw it.</summary>
public sealed class Tile
{
    public Tile(TileKey key, BitmapSource? bitmap, int x, int y, int width, int height)
    {
        Key = key;
        Bitmap = bitmap;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Bytes = (long)width * height * 4;
    }

    public TileKey Key { get; }

    /// <summary>Null when the page could not be drawn; the page then stays blank rather than being retried forever.</summary>
    public BitmapSource? Bitmap { get; }

    /// <summary>Position and size in page pixels at the tile's scale.</summary>
    public int X { get; }

    public int Y { get; }

    public int Width { get; }

    public int Height { get; }

    public long Bytes { get; }

    public long Stamp { get; set; }
}

/// <summary>A tile the view wants, with what is needed to render it.</summary>
public readonly record struct TileRequest(TileKey Key, int PageWidth, int PageHeight, int X, int Y, int Width, int Height, bool Visible);

/// <summary>
/// Renders tiles for one view and keeps them, within a memory budget.
///
/// The view publishes a plan - the tiles it wants, most important first - every time what is on
/// screen changes. A pump on the engine thread takes the newest plan each time it runs and
/// renders the first tile in it that is not done yet, together with the neighbouring tiles of the
/// same page in one PDFium call, then queues itself again. So nothing stale is ever rendered: a
/// page scrolled past is simply not in the next plan, and there is no backlog to cancel.
/// </summary>
public sealed unsafe class TileRenderer
{
    /// <summary>Tile edge in device pixels.</summary>
    public const int TileSize = 512;

    /// <summary>Largest area rendered in one call: 8 megapixels (32 MB) for tiles on screen, 2 for prefetch.</summary>
    private const long VisibleBatchPixels = 8L * 1024 * 1024;

    private const long PrefetchBatchPixels = 2L * 1024 * 1024;

    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<TileKey, Tile> _cache = new();
    private readonly Dictionary<int, List<Tile>> _byPage = new();
    private readonly ConcurrentDictionary<TileKey, byte> _delivered = new();
    private readonly ConcurrentQueue<Tile> _arrivals = new();
    private readonly object _pumpGate = new();

    private volatile Plan? _plan;
    private int _queuedVisible;
    private int _queuedPrefetch;
    private int _drainQueued;
    private long _stamp;
    private long _bytes;
    private PdfDocument? _document;
    private int _generation;

    public TileRenderer(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>Raised on the UI thread with the pages that received tiles.</summary>
    public event Action<IReadOnlyCollection<int>>? TilesArrived;

    /// <summary>Bytes of pixels held. Tiles that are drawn are never let go, so this can exceed <see cref="Budget"/> briefly.</summary>
    public long CachedBytes => _bytes;

    public int CachedCount => _cache.Count;

    /// <summary>How many bytes of tiles to keep; the view sets it from the size of its viewport.</summary>
    public long Budget { get; set; } = 128L * 1024 * 1024;

    /// <summary>Changes with every document (and every reload), so old tiles and old jobs are told apart.</summary>
    public int Generation => _generation;

    /// <summary>Whether the pump has work queued or running. For tests.</summary>
    public bool IsBusy
    {
        get
        {
            lock (_pumpGate) return _queuedVisible + _queuedPrefetch > 0 || !_arrivals.IsEmpty;
        }
    }

    // ===================================================================== UI thread

    public void SetDocument(PdfDocument? document)
    {
        _document = document;
        _generation++;
        _plan = null;
        _cache.Clear();
        _byPage.Clear();
        _bytes = 0;
        _delivered.Clear();
    }

    public bool TryGet(TileKey key, out Tile tile) => _cache.TryGetValue(key, out tile!);

    /// <summary>The tiles held for a page, at any scale.</summary>
    public IReadOnlyList<Tile> TilesFor(int page) =>
        _byPage.TryGetValue(page, out var list) ? list : (IReadOnlyList<Tile>)Array.Empty<Tile>();

    public void Touch(Tile tile) => tile.Stamp = ++_stamp;

    /// <summary>
    /// Replaces the plan. <paramref name="requests"/> are the tiles wanted, most important first,
    /// already without the ones held.
    /// </summary>
    public void SetPlan(List<TileRequest> requests)
    {
        if (_document is not { IsClosed: false } document || requests.Count == 0)
        {
            _plan = null;
            return;
        }

        _plan = new Plan(document, _generation, requests.ToArray());
        RequestPump(requests[0].Visible);
    }

    /// <summary>Drops every tile of <paramref name="page"/> that is not at this scale and rotation.</summary>
    public bool DropOtherScales(int page, int rotation, long scaleBits)
    {
        if (!_byPage.TryGetValue(page, out var list)) return false;
        var removed = false;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            var key = list[i].Key;
            if (key.Rotation == rotation && key.ScaleBits == scaleBits) continue;
            Remove(list[i], list, i);
            removed = true;
        }
        if (list.Count == 0) _byPage.Remove(page);
        return removed;
    }

    /// <summary>Drops tiles of pages not in <paramref name="keepPages"/> that are not at the current scale.</summary>
    public void DropStaleOutside(ISet<int> keepPages, int rotation, long scaleBits)
    {
        foreach (var page in _byPage.Keys.ToArray())
        {
            if (!keepPages.Contains(page)) DropOtherScales(page, rotation, scaleBits);
        }
    }

    /// <summary>
    /// Lets go of the least recently drawn tiles until the cache is within budget. Tiles in
    /// <paramref name="keep"/> are on screen and stay. Returns the pages that lost tiles.
    /// </summary>
    public HashSet<int>? Trim(ISet<TileKey> keep)
    {
        if (_bytes <= Budget) return null;

        var candidates = new List<Tile>(_cache.Count);
        foreach (var tile in _cache.Values)
        {
            if (!keep.Contains(tile.Key)) candidates.Add(tile);
        }
        candidates.Sort((a, b) => a.Stamp.CompareTo(b.Stamp));

        HashSet<int>? pages = null;
        foreach (var tile in candidates)
        {
            if (_bytes <= Budget * 3 / 4) break;
            if (!_byPage.TryGetValue(tile.Key.Page, out var list)) continue;
            var index = list.IndexOf(tile);
            if (index < 0) continue;
            Remove(tile, list, index);
            if (list.Count == 0) _byPage.Remove(tile.Key.Page);
            (pages ??= new HashSet<int>()).Add(tile.Key.Page);
        }
        return pages;
    }

    private void Remove(Tile tile, List<Tile> list, int index)
    {
        list.RemoveAt(index);
        _cache.Remove(tile.Key);
        _bytes -= tile.Bytes;
        // No longer held, so it may be rendered again when it is wanted again.
        _delivered.TryRemove(tile.Key, out _);
    }

    private void Add(Tile tile)
    {
        if (_cache.TryGetValue(tile.Key, out var old) && _byPage.TryGetValue(old.Key.Page, out var oldList))
        {
            var i = oldList.IndexOf(old);
            if (i >= 0) Remove(old, oldList, i);
        }

        _cache[tile.Key] = tile;
        if (!_byPage.TryGetValue(tile.Key.Page, out var list)) _byPage[tile.Key.Page] = list = new List<Tile>();
        list.Add(tile);
        _bytes += tile.Bytes;
        tile.Stamp = ++_stamp;
    }

    private void Drain()
    {
        Interlocked.Exchange(ref _drainQueued, 0);

        HashSet<int>? pages = null;
        while (_arrivals.TryDequeue(out var tile))
        {
            if (tile.Key.Generation == _generation)
            {
                // "Delivered" means held or on its way: it stays marked for as long as it is held,
                // so a plan made before it arrived cannot have it rendered twice.
                Add(tile);
                _delivered[tile.Key] = 1;
                (pages ??= new HashSet<int>()).Add(tile.Key.Page);
            }
            else
            {
                _delivered.TryRemove(tile.Key, out _);
            }
        }

        if (pages is null) return;
        try
        {
            TilesArrived?.Invoke(pages);
        }
        catch (Exception ex)
        {
            AppLog.Error("Showing new tiles failed.", ex);
        }
    }

    // ===================================================================== pump

    private void RequestPump(bool visible)
    {
        lock (_pumpGate)
        {
            if (visible)
            {
                if (_queuedVisible > 0) return;
                _queuedVisible++;
            }
            else
            {
                if (_queuedVisible + _queuedPrefetch > 0) return;
                _queuedPrefetch++;
            }
        }

        var priority = visible ? JobPriority.Visible : JobPriority.Prefetch;
        _ = PdfEngine.Instance.Run(priority, () => PumpJob(visible));
    }

    private void PumpJob(bool visible)
    {
        lock (_pumpGate)
        {
            if (visible) _queuedVisible--;
            else _queuedPrefetch--;
        }

        bool? more;
        try
        {
            more = RenderOneBatch();
        }
        catch (Exception ex)
        {
            AppLog.Error("Rendering tiles failed.", ex);
            more = null;
        }

        if (more is { } nextVisible) RequestPump(nextVisible);
    }

    /// <summary>
    /// Engine thread. Renders the first tile of the newest plan that is not done, and its
    /// neighbours. Returns whether what remains is on screen, or null when nothing remains.
    /// </summary>
    private bool? RenderOneBatch()
    {
        var plan = _plan;
        if (plan is null || plan.Document.IsClosed) return null;

        var items = plan.Items;
        var first = -1;
        for (var i = 0; i < items.Length; i++)
        {
            if (!_delivered.ContainsKey(items[i].Key)) { first = i; break; }
        }
        if (first < 0) return null;

        var head = items[first];
        var batch = new List<TileRequest> { head };
        var x0 = head.X;
        var y0 = head.Y;
        var x1 = head.X + head.Width;
        var y1 = head.Y + head.Height;
        long covered = (long)head.Width * head.Height;
        var cap = head.Visible ? VisibleBatchPixels : PrefetchBatchPixels;

        for (var i = first + 1; i < items.Length; i++)
        {
            var t = items[i];
            if (t.Key.Page != head.Key.Page || t.Key.ScaleBits != head.Key.ScaleBits ||
                t.Key.Rotation != head.Key.Rotation || t.Visible != head.Visible ||
                _delivered.ContainsKey(t.Key))
            {
                continue;
            }

            var nx0 = Math.Min(x0, t.X);
            var ny0 = Math.Min(y0, t.Y);
            var nx1 = Math.Max(x1, t.X + t.Width);
            var ny1 = Math.Max(y1, t.Y + t.Height);
            var area = (long)(nx1 - nx0) * (ny1 - ny0);
            var newCovered = covered + ((long)t.Width * t.Height);
            // Grow the batch only while it stays compact: no large unwanted area rendered.
            if (area > cap || area * 2 > newCovered * 3) continue;

            batch.Add(t);
            (x0, y0, x1, y1, covered) = (nx0, ny0, nx1, ny1, newCovered);
        }

        // Rendered in the page's own coordinates, so these tiles join every other tile of the
        // page exactly, whenever those were rendered (see PageCanvas).
        using (var canvas = plan.Document.RenderCanvas(head.Key.Page, head.PageWidth, head.PageHeight, head.Key.Rotation,
                   x0, y0, x1 - x0, y1 - y0, keepPage: true, out var ok))
        {
            foreach (var t in batch)
            {
                var bitmap = ok ? PdfDocument.ToBitmap(canvas.At(t.X, t.Y), t.Width, t.Height, canvas.Stride) : null;
                _delivered[t.Key] = 1;
                _arrivals.Enqueue(new Tile(t.Key, bitmap, t.X, t.Y, t.Width, t.Height));
            }
        }

        if (Interlocked.Exchange(ref _drainQueued, 1) == 0)
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Drain));

        // What is left of the newest plan decides how urgently the pump comes back.
        var latest = _plan;
        if (latest is null || latest.Document.IsClosed) return null;
        foreach (var t in latest.Items)
        {
            if (!_delivered.ContainsKey(t.Key)) return t.Visible;
        }
        return null;
    }

    private sealed class Plan
    {
        public Plan(PdfDocument document, int generation, TileRequest[] items)
        {
            Document = document;
            Generation = generation;
            Items = items;
        }

        public PdfDocument Document { get; }

        public int Generation { get; }

        public TileRequest[] Items { get; }
    }
}
