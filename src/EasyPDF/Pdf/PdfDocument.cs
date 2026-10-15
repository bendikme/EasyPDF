using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EasyPDF.Interop;
using EasyPDF.Services;

namespace EasyPDF.Pdf;

/// <summary>
/// An open PDF. What is known once it is open - page count, page sizes, metadata - can be read
/// from any thread. Everything else is a job on <see cref="PdfEngine"/>, and the methods marked
/// "engine thread" may only be called from there (the renderer's pump calls them directly).
///
/// Loaded pages are kept in a small most-recently-used cache: a page's parsed content and decoded
/// images are what make rendering it again fast, and also what makes a page expensive to keep,
/// so only the few on screen stay loaded.
/// </summary>
public sealed unsafe class PdfDocument
{
    /// <summary>Pages kept parsed. Two or three are on screen at a reading zoom.</summary>
    private const int PageCacheSize = 6;

    /// <summary>Coordinates are converted through a device square this big: six exact digits.</summary>
    private const int FractionScale = 1_000_000;

    private const int MaxLinksPerPage = 20_000;
    private const int MaxMatchesPerPage = 20_000;
    private const int MaxOutlineItems = 200_000;
    private const int MaxOutlineDepth = 64;

    private static readonly double DefaultWidth = 612;
    private static readonly double DefaultHeight = 792;

    private readonly PdfEngine _engine;
    private readonly List<PageEntry> _pages = new();
    private readonly NativeBuffer _scratch = new();

    private nint _doc;
    private nint _form;
    private void* _formInfo;
    private FileSource? _source;
    private string? _password;
    private volatile bool _closed;

    /// <summary>FPDF_GetFormType: 0 when the document has no form.</summary>
    private int _formType;

    /// <summary>The document has scripts of its own: its links may mark where they land (see <see cref="PdfMark"/>).</summary>
    private bool _hasScripts;

    /// <summary>
    /// The file's own objects, read without PDFium, for what its API does not tell (the size of
    /// a form, what buttons and named actions do); null until needed, and when the file cannot be
    /// read that way (encrypted, damaged). Engine thread.
    /// </summary>
    private PdfObjectReader? _objects;
    private bool _objectsTried;
    private bool _pageMismatchLogged;
    private bool _objectPagesChecked;
    private bool _objectPagesMatch;

    /// <summary>
    /// The links PDFium leaves out, by page, read from a copy PDFium wrote of the document: for a
    /// file the reader cannot read itself. Null until the job reading them is done. Engine thread.
    /// </summary>
    private Dictionary<int, List<RawLink>>? _copiedLinks;
    private Task? _copyJob;

    private PdfDocument(PdfEngine engine, string path)
    {
        _engine = engine;
        FilePath = path;
    }

    public string FilePath { get; }

    public string FileName => Path.GetFileName(FilePath);

    public long FileLength { get; private set; }

    public DateTime FileWriteTimeUtc { get; private set; }

    public int PageCount { get; private set; }

    /// <summary>Every page's displayed size in points, read once at open.</summary>
    public IReadOnlyList<PageSize> PageSizes { get; private set; } = Array.Empty<PageSize>();

    public PdfMetadata Metadata { get; private set; } = null!;

    public bool IsClosed => _closed;

    /// <summary>Whether reading the file failed since it was opened - it changed or vanished underneath.</summary>
    public bool FileReadFailed => _source?.ReadFailed == true;

    // ===================================================================== opening and closing

    /// <summary>
    /// Opens a document. The file itself is opened on a thread of its own: a network share that
    /// does not answer can hold that for half a minute, and it must hold up nothing else - not the
    /// window, and not the rendering of every other document on PDFium's one thread.
    /// </summary>
    public static Task<OpenResult> OpenAsync(string path, string? password = null, CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var engine = PdfEngine.Instance;
        return Task.Factory.StartNew(() => OpenFile(path), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
            .ContinueWith(opened =>
            {
                var (source, written, failure) = opened.Result;
                if (source is null) return Task.FromResult(failure!);
                if (cancellation.IsCancellationRequested)
                {
                    source.Dispose();
                    return Task.FromCanceled<OpenResult>(cancellation);
                }
                // From here OpenCore owns the file: it keeps it, or closes it.
                return engine.Run(JobPriority.Interactive, () => OpenCore(engine, path, password, source, written));
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
            .Unwrap();
    }

    /// <summary>Runs before a file is opened. Tests set it to play a share that is slow to answer.</summary>
    internal static Action<string>? BeforeOpenFile { get; set; }

    private static (FileSource? Source, DateTime Written, OpenResult? Failure) OpenFile(string path)
    {
        try
        {
            BeforeOpenFile?.Invoke(path);
            var source = FileSource.Open(path);
            DateTime written;
            try { written = File.GetLastWriteTimeUtc(path); }
            catch { written = DateTime.MinValue; }
            return (source, written, null);
        }
        catch (FileNotFoundException) { return (null, default, new OpenResult(null, OpenFailure.NotFound)); }
        catch (DirectoryNotFoundException) { return (null, default, new OpenResult(null, OpenFailure.NotFound)); }
        catch (UnauthorizedAccessException ex) { return (null, default, new OpenResult(null, OpenFailure.AccessDenied, ex.Message)); }
        catch (IOException ex) { return (null, default, new OpenResult(null, OpenFailure.AccessDenied, ex.Message)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, default, new OpenResult(null, OpenFailure.NotFound, ex.Message));
        }
    }

    private static OpenResult OpenCore(PdfEngine engine, string path, string? password, FileSource source, DateTime written)
    {
        var document = new PdfDocument(engine, path);
        try
        {
            if (source.Length == 0)
            {
                source.Dispose();
                return new OpenResult(null, OpenFailure.Empty);
            }

            // The file's own objects are read alongside PDFium's opening, on another thread: a
            // form's size must be known before its fields are loaded (see InitForms), and read
            // this way it costs no time at all.
            var stopMeasuring = new CancellationTokenSource();
            var measuring = Task.Run(() => MeasureForm(source, stopMeasuring.Token));

            var doc = LoadNative(source, path, password);
            if (doc == 0)
            {
                stopMeasuring.Cancel();
                var error = Pdfium.FPDF_GetLastError();
                var failure = error switch
                {
                    Pdfium.ErrPassword => string.IsNullOrEmpty(password) ? OpenFailure.PasswordRequired : OpenFailure.WrongPassword,
                    Pdfium.ErrSecurity => OpenFailure.UnsupportedSecurity,
                    Pdfium.ErrFile => OpenFailure.NotFound,
                    Pdfium.ErrFormat => LooksLikePdf(source) ? OpenFailure.Damaged : OpenFailure.NotPdf,
                    _ => LooksLikePdf(source) ? OpenFailure.Damaged : OpenFailure.NotPdf,
                };
                source.Dispose();
                return new OpenResult(null, failure);
            }

            document._doc = doc;
            document._source = source;
            document._password = password;
            document.FileLength = source.Length;
            document.FileWriteTimeUtc = written;

            var count = Pdfium.FPDF_GetPageCount(doc);
            if (count <= 0)
            {
                stopMeasuring.Cancel();
                document.CloseCore();
                return new OpenResult(null, OpenFailure.Damaged);
            }

            document.PageCount = count;
            document.PageSizes = ReadPageSizes(doc, count);
            document.InitForms(measuring, stopMeasuring);
            document.Metadata = document.ReadMetadata();
            document._memoryBaseline = ProcessMemory.Private();
            return new OpenResult(document, OpenFailure.None);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Opening {path} failed.", ex);
            if (document._doc != 0) document.CloseCore();
            else source.Dispose();
            return new OpenResult(null, OpenFailure.Unknown, ex.Message);
        }
    }

    private static nint LoadNative(FileSource source, string path, string? password)
    {
        var pw = password is null ? null : Utf8Z(password);
        fixed (byte* pwPtr = pw)
        {
            if (source.Length <= FileSource.CustomAccessLimit)
                return Pdfium.FPDF_LoadCustomDocument(source.Access, pwPtr);

            // Beyond 4 GiB only PDFium's own file reading can reach the end of the file.
            fixed (byte* pathPtr = Utf8Z(path))
                return Pdfium.FPDF_LoadDocument(pathPtr, pwPtr);
        }
    }

    /// <summary>A PDF has "%PDF-" in its first kilobyte; anything else was never a PDF.</summary>
    private static bool LooksLikePdf(FileSource source)
    {
        var head = source.ReadHead(1024);
        return head.AsSpan().IndexOf("%PDF-"u8) >= 0;
    }

    private static PageSize[] ReadPageSizes(nint doc, int count)
    {
        var sizes = new PageSize[count];
        for (var i = 0; i < count; i++)
        {
            Pdfium.SizeF size;
            if (Pdfium.FPDF_GetPageSizeByIndexF(doc, i, &size) != 0)
                sizes[i] = new PageSize(Sane(size.Width, DefaultWidth), Sane(size.Height, DefaultHeight));
            else
                sizes[i] = i > 0 ? sizes[i - 1] : new PageSize(DefaultWidth, DefaultHeight);
        }
        return sizes;
    }

    /// <summary>A usable page dimension: broken files report zero, negative or absurd sizes.</summary>
    private static double Sane(float value, double fallback) =>
        float.IsFinite(value) && value >= 1 ? Math.Min(value, 200_000) : fallback;

    /// <summary>A form with more fields than this is not handed to PDFium's form module (see <see cref="InitForms"/>).</summary>
    internal const int MaxFormFields = 5000;

    /// <summary>How long opening waits for the file's own objects to be read before judging the form by PDFium's count alone.</summary>
    private static readonly TimeSpan MeasureTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The file read without PDFium, and how many fields its form has (up to <see cref="MaxFormFields"/>); a null reader when it cannot be read that way. Any thread.</summary>
    private static (PdfObjectReader? Reader, int Fields) MeasureForm(FileSource source, CancellationToken cancel)
    {
        try
        {
            var reader = PdfObjectReader.Open(source, cancel);
            return (reader, reader?.CountFormFields(MaxFormFields) ?? 0);
        }
        catch (OperationCanceledException)
        {
            return (null, 0);
        }
        catch (Exception ex)
        {
            AppLog.Warn("The file's objects could not be read.", ex);
            return (null, 0);
        }
    }

    private void InitForms(Task<(PdfObjectReader? Reader, int Fields)> measuring, CancellationTokenSource stopMeasuring)
    {
        _formType = Pdfium.FPDF_GetFormType(_doc);
        _hasScripts = Pdfium.FPDFDoc_GetJavaScriptActionCount(_doc) > 0;

        // Without a form the reader is not needed now; it is made again if it ever is.
        if (_formType == 0 && !_hasScripts)
        {
            stopMeasuring.Cancel();
            return;
        }

        // PDFium's form module files every field by its name in lists it searches one entry at a
        // time, so loading a form takes time that grows with the square of its fields - the
        // 200,000 cross-reference buttons of an EPLAN schematic take minutes, during which no page
        // is drawn. Such a form is not loaded: its pages draw at once, and its buttons still work
        // as links (read by ActionLinks); only fields the form module would draw itself are not drawn.
        int? fields = null;
        if (measuring.Wait(MeasureTimeout))
        {
            var (reader, count) = measuring.Result;
            _objects = reader;
            _objectsTried = true;
            if (reader is not null) fields = count;
        }
        else
        {
            // A file that keeps the reader busy this long is judged without it; it is read again if needed.
            stopMeasuring.Cancel();
        }

        // A file the reader cannot read (encrypted, damaged) has its form judged by the widgets
        // PDFium finds on a few of its pages.
        var size = fields ?? EstimateWidgets();
        if (size >= MaxFormFields)
        {
            AppLog.Info($"The form of {FileName} has more than {MaxFormFields} fields; its fields are not drawn.");
            return;
        }

        _formInfo = FormCallbacks.Create();
        _form = Pdfium.FPDFDOC_InitFormFillEnvironment(_doc, _formInfo);
        if (_form == 0)
        {
            NativeMemory.Free(_formInfo);
            _formInfo = null;
        }
    }

    /// <summary>How many widgets the document has, judged from up to eight pages spread through it. Engine thread.</summary>
    private int EstimateWidgets()
    {
        var samples = Math.Min(PageCount, 8);
        long widgets = 0;
        for (var s = 0; s < samples; s++)
        {
            var page = Pdfium.FPDF_LoadPage(_doc, (int)((long)s * PageCount / samples));
            if (page == 0) continue;
            try
            {
                var count = Pdfium.FPDFPage_GetAnnotCount(page);
                for (var i = 0; i < count && i < 100_000; i++)
                {
                    var annot = Pdfium.FPDFPage_GetAnnot(page, i);
                    if (annot == 0) continue;
                    if (Pdfium.FPDFAnnot_GetSubtype(annot) == Pdfium.AnnotWidget) widgets++;
                    Pdfium.FPDFPage_CloseAnnot(annot);
                }
            }
            finally
            {
                Pdfium.FPDF_ClosePage(page);
            }
        }
        return samples == 0 ? 0 : (int)Math.Min(int.MaxValue, widgets * PageCount / samples);
    }

    private PdfMetadata ReadMetadata()
    {
        int version;
        var fileVersion = Pdfium.FPDF_GetFileVersion(_doc, &version) != 0 ? version : 0;
        return new PdfMetadata(
            MetaText("Title"),
            MetaText("Author"),
            MetaText("Subject"),
            MetaText("Keywords"),
            MetaText("Creator"),
            MetaText("Producer"),
            MetaText("CreationDate"),
            MetaText("ModDate"),
            fileVersion,
            Pdfium.FPDF_GetSecurityHandlerRevision(_doc) >= 0,
            _formType != 0);
    }

    private string MetaText(string tag)
    {
        var tagBytes = Encoding.ASCII.GetBytes(tag + "\0");
        fixed (byte* t = tagBytes)
        {
            var length = Pdfium.FPDF_GetMetaText(_doc, t, null, 0);
            if (length <= 2 || length > 1 << 20) return string.Empty;
            var buffer = new byte[length];
            fixed (byte* b = buffer) Pdfium.FPDF_GetMetaText(_doc, t, b, length);
            return CleanText(Utf16Z(buffer));
        }
    }

    /// <summary>Closes the document. Safe to call more than once and from any thread.</summary>
    public Task CloseAsync()
    {
        if (_closed) return Task.CompletedTask;
        _closed = true;
        return _engine.Run(JobPriority.Interactive, CloseCore);
    }

    private void CloseCore()
    {
        _closed = true;
        foreach (var entry in _pages) ClosePageEntry(entry);
        _pages.Clear();

        if (_form != 0)
        {
            Pdfium.FPDFDOC_ExitFormFillEnvironment(_form);
            _form = 0;
        }

        if (_doc != 0)
        {
            Pdfium.FPDF_CloseDocument(_doc);
            _doc = 0;
        }

        if (_formInfo != null)
        {
            NativeMemory.Free(_formInfo);
            _formInfo = null;
        }
        _recycleQueued = false;

        _objects = null;
        _copiedLinks = null;
        _source?.Dispose();
        _source = null;
        _scratch.Dispose();
    }

    // ===================================================================== keeping PDFium's memory in check

    /// <summary>
    /// PDFium keeps every object it has parsed and every object stream it has decompressed for as
    /// long as a document is open. Most documents are small enough that this never matters, but
    /// one of millions of objects - a two-thousand-page EPLAN schematic - grows by half a megabyte
    /// for every page read, past a gigabyte. When the process has grown this much since the
    /// document was opened, its PDFium document is opened afresh, unseen: pages simply load from
    /// the new one, which starts from nothing again.
    /// </summary>
    internal const long RecycleGrowth = 256L << 20;

    private long _memoryBaseline;
    private long _recycleGrowth = RecycleGrowth;
    private int _loadsSinceCheck;
    private bool _recycleQueued;

    /// <summary>How many times the PDFium document was opened afresh. For tests.</summary>
    internal int Recycles { get; private set; }

    private void CheckMemory()
    {
        _loadsSinceCheck = 0;
        if (_recycleQueued || _closed || ProcessMemory.Private() - _memoryBaseline < _recycleGrowth) return;

        // A job of its own, after what is on screen; no page is in use between jobs.
        _recycleQueued = true;
        _ = _engine.Run(JobPriority.Prefetch, Recycle);
    }

    /// <summary>Opens the PDFium document afresh and lets the old one go, with all it has collected. Engine thread.</summary>
    internal void Recycle()
    {
        _recycleQueued = false;
        if (_closed || _source is not { } source) return;

        // A file that changed is about to be reloaded whole; and a document that cannot be opened
        // again (it always can, unchanged) is kept as it is.
        nint fresh = 0;
        if (!source.ReadFailed && !ChangedSinceOpened()) fresh = LoadNative(source, FilePath, _password);
        if (fresh == 0 || Pdfium.FPDF_GetPageCount(fresh) != PageCount)
        {
            if (fresh != 0) Pdfium.FPDF_CloseDocument(fresh);
            _memoryBaseline = ProcessMemory.Private();
            return;
        }

        var before = ProcessMemory.Private();
        foreach (var entry in _pages) ClosePageEntry(entry);
        _pages.Clear();
        var hadForm = _form != 0;
        if (hadForm)
        {
            Pdfium.FPDFDOC_ExitFormFillEnvironment(_form);
            _form = 0;
        }
        Pdfium.FPDF_CloseDocument(_doc);

        _doc = fresh;
        if (hadForm && _formInfo != null) _form = Pdfium.FPDFDOC_InitFormFillEnvironment(_doc, _formInfo);
        Recycles++;

        // When it was not PDFium's collection that had grown (tiles, other documents), opening
        // afresh gave little back: it waits for twice the growth before it is tried again.
        var after = ProcessMemory.Private();
        if (before - after < (before - _memoryBaseline) / 2) _recycleGrowth = Math.Min(_recycleGrowth * 2, 8L << 30);
        _memoryBaseline = after;
        AppLog.Info($"{FileName} was opened afresh to let go of what PDFium had collected.");
    }

    // ===================================================================== page cache (engine thread)

    private sealed class PageEntry
    {
        public int Index;
        public nint Page;
        public nint Text;
        public bool MatrixReady;
        public double Ox, Oy, Ax, Ay, Bx, By;
    }

    /// <summary>The loaded page, from the cache or loaded now. Engine thread. Null when it cannot be loaded.</summary>
    private PageEntry? GetPage(int index, bool keep)
    {
        if (_closed || _doc == 0 || (uint)index >= (uint)PageCount) return null;

        for (var i = 0; i < _pages.Count; i++)
        {
            var hit = _pages[i];
            if (hit.Index != index) continue;
            if (i > 0)
            {
                _pages.RemoveAt(i);
                _pages.Insert(0, hit);
            }
            return hit;
        }

        var page = Pdfium.FPDF_LoadPage(_doc, index);
        if (page == 0) return null;
        if (_form != 0) Pdfium.FORM_OnAfterLoadPage(page, _form);
        if (++_loadsSinceCheck >= 8) CheckMemory();

        var entry = new PageEntry { Index = index, Page = page };
        if (!keep) return entry;

        _pages.Insert(0, entry);
        while (_pages.Count > PageCacheSize)
        {
            var last = _pages[^1];
            _pages.RemoveAt(_pages.Count - 1);
            ClosePageEntry(last);
        }
        return entry;
    }

    /// <summary>Closes a page that <see cref="GetPage"/> loaded without keeping it.</summary>
    private void Release(PageEntry entry)
    {
        if (!_pages.Contains(entry)) ClosePageEntry(entry);
    }

    private void ClosePageEntry(PageEntry entry)
    {
        if (entry.Text != 0)
        {
            Pdfium.FPDFText_ClosePage(entry.Text);
            entry.Text = 0;
        }

        if (entry.Page != 0)
        {
            if (_form != 0) Pdfium.FORM_OnBeforeClosePage(entry.Page, _form);
            Pdfium.FPDF_ClosePage(entry.Page);
            entry.Page = 0;
        }
    }

    private static nint TextOf(PageEntry entry)
    {
        if (entry.Text == 0) entry.Text = Pdfium.FPDFText_LoadPage(entry.Page);
        return entry.Text;
    }

    /// <summary>
    /// The mapping from PDF page space to page fractions, exactly as PDFium places the page
    /// (crop box, /Rotate and all): three points mapped through FPDF_PageToDevice give the affine.
    /// </summary>
    private static void EnsureMatrix(PageEntry entry)
    {
        if (entry.MatrixReady) return;

        int x0, y0, x1, y1, x2, y2;
        Pdfium.FPDF_PageToDevice(entry.Page, 0, 0, FractionScale, FractionScale, 0, 0, 0, &x0, &y0);
        Pdfium.FPDF_PageToDevice(entry.Page, 0, 0, FractionScale, FractionScale, 0, 1000, 0, &x1, &y1);
        Pdfium.FPDF_PageToDevice(entry.Page, 0, 0, FractionScale, FractionScale, 0, 0, 1000, &x2, &y2);

        const double s = FractionScale;
        entry.Ox = x0 / s;
        entry.Oy = y0 / s;
        entry.Ax = (x1 - x0) / s / 1000;
        entry.Ay = (y1 - y0) / s / 1000;
        entry.Bx = (x2 - x0) / s / 1000;
        entry.By = (y2 - y0) / s / 1000;
        entry.MatrixReady = true;
    }

    private static (double X, double Y) ToFraction(PageEntry e, double px, double py) =>
        (e.Ox + (px * e.Ax) + (py * e.Bx), e.Oy + (px * e.Ay) + (py * e.By));

    private static PageRect ToFractionRect(PageEntry e, double left, double top, double right, double bottom)
    {
        var (ax, ay) = ToFraction(e, left, top);
        var (bx, by) = ToFraction(e, right, bottom);
        return PageRect.FromCorners(ax, ay, bx, by);
    }

    // ===================================================================== rendering (engine thread)

    /// <summary>
    /// Renders part of a page into <paramref name="buffer"/> as 32-bit BGRx. The page is laid out
    /// at <paramref name="pageWidth"/> x <paramref name="pageHeight"/> pixels (after the viewer's
    /// rotation), and the region from (<paramref name="x"/>, <paramref name="y"/>) of size
    /// <paramref name="width"/> x <paramref name="height"/> of that is drawn. Engine thread.
    /// Returns false when the page could not be loaded; the buffer is then white.
    /// </summary>
    internal bool RenderRegion(int pageIndex, int pageWidth, int pageHeight, int rotation,
        int x, int y, int width, int height, byte* buffer, int stride, bool keepPage, int flags = Pdfium.FlagAnnot)
    {
        var bitmap = Pdfium.FPDFBitmap_CreateEx(width, height, Pdfium.BitmapBgrx, buffer, stride);
        if (bitmap == 0) return false;

        try
        {
            Pdfium.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);

            var entry = GetPage(pageIndex, keepPage);
            if (entry is null) return false;

            try
            {
                Pdfium.FPDF_RenderPageBitmap(bitmap, entry.Page, -x, -y, pageWidth, pageHeight, rotation & 3, flags);
                if (_form != 0)
                    Pdfium.FPDF_FFLDraw(_form, bitmap, entry.Page, -x, -y, pageWidth, pageHeight, rotation & 3, flags);
            }
            finally
            {
                if (!keepPage) Release(entry);
            }
            return true;
        }
        finally
        {
            Pdfium.FPDFBitmap_Destroy(bitmap);
        }
    }

    /// <summary>
    /// Pages whose pixel buffer would be larger than this (at very high zoom) are rendered region
    /// by region instead: committing that much address space for every batch is not worth it.
    /// </summary>
    private const long SeamlessLimitBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Renders the region (x, y, width, height) of a page laid out at pageWidth x pageHeight
    /// pixels, in the page's own coordinates (see <see cref="PageCanvas"/>), so every region of a
    /// page - rendered now or later - joins its neighbours exactly. <paramref name="flags"/> are
    /// PDFium's: printing adds FPDF_PRINTING, so what is marked for print (or not) prints so.
    /// Engine thread.
    /// </summary>
    internal PageCanvas RenderCanvas(int pageIndex, int pageWidth, int pageHeight, int rotation,
        int x, int y, int width, int height, bool keepPage, out bool ok, int flags = Pdfium.FlagAnnot)
    {
        ok = false;
        if ((long)pageWidth * 4 * (pageHeight + 1) <= SeamlessLimitBytes &&
            PageCanvas.AllocatePage(pageWidth, pageHeight) is { } canvas)
        {
            try
            {
                var bitmap = Pdfium.FPDFBitmap_CreateEx(pageWidth, pageHeight, Pdfium.BitmapBgrx, canvas.Origin, canvas.Stride);
                if (bitmap != 0)
                {
                    try
                    {
                        Pdfium.FPDFBitmap_FillRect(bitmap, x, y, width, height, 0xFFFFFFFF);
                        var entry = GetPage(pageIndex, keepPage);
                        if (entry is null) return canvas;
                        try
                        {
                            var matrix = DisplayMatrix(entry, pageWidth, pageHeight, rotation);
                            var clip = new Pdfium.RectF { Left = x, Top = y, Right = x + width, Bottom = y + height };
                            Pdfium.FPDF_RenderPageBitmapWithMatrix(bitmap, entry.Page, &matrix, &clip, flags);
                            // Form fields: drawn over the page with the same origin. FFLDraw has no
                            // clip, so it may draw outside the region - into the canvas, which is
                            // page-sized, and those pixels are never read.
                            if (_form != 0)
                                Pdfium.FPDF_FFLDraw(_form, bitmap, entry.Page, 0, 0, pageWidth, pageHeight, rotation & 3, flags);
                            ok = true;
                        }
                        finally
                        {
                            if (!keepPage) Release(entry);
                        }
                        return canvas;
                    }
                    finally
                    {
                        Pdfium.FPDFBitmap_Destroy(bitmap);
                    }
                }
            }
            catch
            {
                // Nothing should throw here; if anything does, the page-sized block is not left behind.
                canvas.Dispose();
                throw;
            }
            canvas.Dispose();
        }

        var regionCanvas = PageCanvas.AllocateRegion(x, y, width, height, out var buffer, out var stride);
        try
        {
            ok = RenderRegion(pageIndex, pageWidth, pageHeight, rotation, x, y, width, height, buffer, stride, keepPage, flags);
            return regionCanvas;
        }
        catch
        {
            regionCanvas.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The matrix that, after PDFium's own display matrix (which maps the page, /Rotate applied, to
    /// a rectangle of its size in points, origin top left), lays the page out at pageWidth x
    /// pageHeight pixels turned <paramref name="rotation"/> quarters clockwise - the same placement
    /// FPDF_RenderPageBitmap makes.
    /// </summary>
    private static Pdfium.Matrix DisplayMatrix(PageEntry entry, int pageWidth, int pageHeight, int rotation)
    {
        // Verified pixel for pixel against FPDF_RenderPageBitmap, at every rotation and for page
        // sizes that are not whole points (regression check "Canvas regions equal PDFium's own render").
        double w0 = Math.Max(1e-3, Pdfium.FPDF_GetPageWidthF(entry.Page));
        double h0 = Math.Max(1e-3, Pdfium.FPDF_GetPageHeightF(entry.Page));

        return (rotation & 3) switch
        {
            1 => new Pdfium.Matrix { A = 0, B = (float)(pageHeight / w0), C = (float)(-pageWidth / h0), D = 0, E = pageWidth, F = 0 },
            2 => new Pdfium.Matrix { A = (float)(-pageWidth / w0), B = 0, C = 0, D = (float)(-pageHeight / h0), E = pageWidth, F = pageHeight },
            3 => new Pdfium.Matrix { A = 0, B = (float)(-pageHeight / w0), C = (float)(pageWidth / h0), D = 0, E = 0, F = pageHeight },
            _ => new Pdfium.Matrix { A = (float)(pageWidth / w0), B = 0, C = 0, D = (float)(pageHeight / h0), E = 0, F = 0 },
        };
    }

    /// <summary>The engine thread's render buffer. Engine thread.</summary>
    internal byte* Scratch(nuint size) => _scratch.Get(size);

    /// <summary>
    /// Gets the first page ready while the window is still being built: renders it small, which
    /// parses it (it stays in the page cache) and has PDFium find and load its fonts - the slow
    /// part of any first render. The real tiles then render in a fraction of the time.
    /// </summary>
    public void WarmUp() =>
        _ = _engine.Run(JobPriority.Interactive, () =>
        {
            if (_closed || PageCount == 0) return;
            const int w = 64, h = 64;
            var buffer = Scratch(w * h * 4);
            RenderRegion(0, w, h, 0, 0, 0, w, h, buffer, w * 4, keepPage: true);
        });

    /// <summary>A whole page as a frozen bitmap, for thumbnails.</summary>
    public Task<BitmapSource?> RenderPageAsync(int pageIndex, int width, int height, int rotation,
        int priority, Func<bool>? stillWanted = null, CancellationToken cancellation = default)
    {
        return _engine.Run(priority, () =>
        {
            if (_closed || width <= 0 || height <= 0) return null;
            // Asked on the engine thread, just before the work: a thumbnail scrolled past is skipped.
            if (stillWanted is not null && !stillWanted()) return null;
            var stride = width * 4;
            var buffer = Scratch((nuint)((long)stride * height));
            RenderRegion(pageIndex, width, height, rotation, 0, 0, width, height, buffer, stride, keepPage: false);
            return ToBitmap(buffer, width, height, stride);
        }, cancellation);
    }

    /// <summary>
    /// Copies pixels into a frozen WPF bitmap that any thread may draw. The rows are first packed
    /// tight: WIC keeps a bitmap with the stride it is given, so a tile cut from a page-wide canvas
    /// would otherwise carry whole page-width rows - three times its size at 200%.
    /// </summary>
    internal static BitmapSource ToBitmap(byte* pixels, int width, int height, int stride)
    {
        var packed = width * 4;
        BitmapSource bitmap;
        if (stride == packed)
        {
            bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, (nint)pixels, packed * height, packed);
        }
        else
        {
            var rows = PackBuffer.Value!.Get((nuint)((long)packed * height));
            for (var y = 0; y < height; y++)
                Buffer.MemoryCopy(pixels + ((long)y * stride), rows + ((long)y * packed), packed, packed);
            bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, (nint)rows, packed * height, packed);
        }
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Where tile rows are packed before WIC copies them; one per thread that makes bitmaps.</summary>
    private static readonly ThreadLocal<NativeBuffer> PackBuffer = new(() => new NativeBuffer());

    // ===================================================================== printing (engine thread)

    /// <summary>
    /// Draws a page into a printer device context. Pages print as vectors and text, as sharp as
    /// the printer goes - except in a document with form fields, which only the form module can
    /// draw, and only into a bitmap: those pages print as a picture at up to
    /// <paramref name="bitmapDpi"/>, sent in bands so that even a poster-sized page needs little
    /// memory. Engine thread.
    /// </summary>
    internal bool PrintPage(nint hdc, int pageIndex, int x, int y, int width, int height, int rotation,
        int bitmapDpi = 0, int deviceDpi = 0)
    {
        if (_form == 0)
        {
            var entry = GetPage(pageIndex, keep: false);
            if (entry is null) return false;
            try
            {
                return Pdfium.FPDF_RenderPage(hdc, entry.Page, x, y, width, height, rotation & 3,
                    Pdfium.FlagAnnot | Pdfium.FlagPrinting) != 0;
            }
            finally
            {
                Release(entry);
            }
        }

        // The picture's resolution: the printer's, but no more than bitmapDpi. Rendered and sent in
        // bands of rows, each in the page's own coordinates so the bands join exactly.
        var factor = bitmapDpi > 0 && deviceDpi > bitmapDpi ? (double)bitmapDpi / deviceDpi : 1;
        var bw = Math.Max(1, (int)Math.Round(width * factor));
        var bh = Math.Max(1, (int)Math.Round(height * factor));
        var band = Math.Max(1, Math.Min(bh, (32 << 20) / (bw * 4)));
        for (var top = 0; top < bh; top += band)
        {
            var rows = Math.Min(band, bh - top);
            using var canvas = RenderCanvas(pageIndex, bw, bh, rotation, 0, top, bw, rows, keepPage: true, out var ok, Pdfium.FlagAnnot | Pdfium.FlagPrinting);
            if (!ok) return false;

            var destTop = y + (int)Math.Round(top / factor);
            var destBottom = y + (int)Math.Round((top + rows) / factor);
            if (!Gdi.Blit(hdc, x, destTop, width, destBottom - destTop, canvas.At(0, top), bw, rows)) return false;
        }
        return true;
    }

    /// <summary>Whether form fields need to be drawn by the form module - then pages print as pictures.</summary>
    internal bool HasForm => _form != 0;

    internal PdfEngine Engine => _engine;

    // ===================================================================== page info: links and text

    public Task<PageInfo?> LoadPageInfoAsync(int pageIndex, int priority, CancellationToken cancellation = default) =>
        _engine.Run(priority, () => LoadPageInfo(pageIndex), cancellation);

    private PageInfo? LoadPageInfo(int pageIndex)
    {
        var entry = GetPage(pageIndex, keep: true);
        if (entry is null) return _closed ? null : new PageInfo(pageIndex, Array.Empty<PdfLink>(), PageText.Empty);

        EnsureMatrix(entry);
        var links = ReadLinks(entry, out var unsupportedSeen);
        AddActionLinks(entry, links, unsupportedSeen, out var morePending);
        var text = TextOf(entry);
        var pageText = text != 0 ? ReadText(entry, text) : PageText.Empty;
        if (text != 0) ReadWebLinks(entry, text, links);
        return new PageInfo(pageIndex, links, pageText, morePending);
    }

    /// <summary>The page's link annotations, as PDFium reads them; <paramref name="unsupportedSeen"/> when one has an action it cannot read.</summary>
    private List<PdfLink> ReadLinks(PageEntry entry, out bool unsupportedSeen)
    {
        unsupportedSeen = false;
        var links = new List<PdfLink>();
        var position = 0;
        nint link;
        var ordinal = -1;
        while (links.Count < MaxLinksPerPage && Pdfium.FPDFLink_Enumerate(entry.Page, &position, &link) != 0)
        {
            ordinal++;
            if (link == 0) continue;

            // The action first: PDFium's FPDFLink_GetDest also hands back the destination of a
            // link into another file, which read as a place in this one would go to the wrong page.
            LinkTarget? target = null;
            var action = Pdfium.FPDFLink_GetAction(link);
            if (action != 0 && Pdfium.FPDFAction_GetType(action) != Pdfium.ActionGoTo)
            {
                // Named actions are among what PDFium cannot read: they come from AddActionLinks.
                if (Pdfium.FPDFAction_GetType(action) != Pdfium.ActionUnsupported) target = ReadAction(action, entry.Index, ordinal);
                else unsupportedSeen = true;
            }
            else
            {
                var dest = Pdfium.FPDFLink_GetDest(_doc, link);
                if (dest != 0 && ReadDestination(dest) is { } d) target = new GoToTarget(d);
            }

            if (target is null) continue;

            var areas = new List<PageRect>();
            var quads = Pdfium.FPDFLink_CountQuadPoints(link);
            for (var q = 0; q < quads && q < 256; q++)
            {
                Pdfium.QuadPointsF quad;
                if (Pdfium.FPDFLink_GetQuadPoints(link, q, &quad) == 0) continue;
                var (ax, ay) = ToFraction(entry, quad.X1, quad.Y1);
                var (bx, by) = ToFraction(entry, quad.X2, quad.Y2);
                var (cx, cy) = ToFraction(entry, quad.X3, quad.Y3);
                var (dx, dy) = ToFraction(entry, quad.X4, quad.Y4);
                var r = new PageRect(
                    (float)Math.Min(Math.Min(ax, bx), Math.Min(cx, dx)),
                    (float)Math.Min(Math.Min(ay, by), Math.Min(cy, dy)),
                    (float)Math.Max(Math.Max(ax, bx), Math.Max(cx, dx)),
                    (float)Math.Max(Math.Max(ay, by), Math.Max(cy, dy)));
                if (!r.IsEmpty) areas.Add(r);
            }

            if (areas.Count == 0)
            {
                Pdfium.RectF rect;
                if (Pdfium.FPDFLink_GetAnnotRect(link, &rect) != 0)
                {
                    var r = ToFractionRect(entry, rect.Left, rect.Top, rect.Right, rect.Bottom);
                    if (!r.IsEmpty) areas.Add(r);
                }
            }

            if (areas.Count > 0) links.Add(new PdfLink(areas, target, fromText: false));
        }

        return links;
    }

    // ===================================================================== links PDFium leaves out

    /// <summary>Documents up to this size can have the links PDFium leaves out read from a copy it writes.</summary>
    internal const long MaxCopiedSource = 64L << 20;

    private PdfObjectReader? Objects
    {
        get
        {
            if (_objectsTried) return _objects;
            _objectsTried = true;
            if (_closed || _source is not { } source) return null;
            try
            {
                _objects = PdfObjectReader.Open(source);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"The objects of {FileName} could not be read.", ex);
                _objects = null;
            }
            return _objects;
        }
    }

    /// <summary>The reader, when its page tree is PDFium's: then a page index means the same page to both.</summary>
    private PdfObjectReader? ObjectsWithPages
    {
        get
        {
            if (!_objectPagesChecked && Objects is { } objects)
            {
                _objectPagesChecked = true;
                try
                {
                    _objectPagesMatch = objects.PageCount == PageCount;
                    if (!_objectPagesMatch) AppLog.Warn($"The page tree of {FileName} reads as {objects.PageCount} pages, PDFium's as {PageCount}.");
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"The page tree of {FileName} could not be read.", ex);
                }
            }
            return _objectPagesMatch ? _objects : null;
        }
    }

    /// <summary>
    /// Adds the links PDFium's link API leaves out: buttons that go somewhere (only a document with
    /// a form has them) and named actions (only where PDFium met an action it could not read).
    /// They are read from the file itself; a file that cannot be read that way has them read from
    /// a copy PDFium writes, by a job of its own after what is on screen - <paramref name="pending"/>
    /// is that job, and the page is read again when it is done. Engine thread.
    /// </summary>
    private void AddActionLinks(PageEntry entry, List<PdfLink> links, bool unsupportedSeen, out Task? pending)
    {
        pending = null;
        if (_formType == 0 && !unsupportedSeen && !_hasScripts) return;

        List<RawLink>? found;
        if (ObjectsWithPages is { } objects)
        {
            try
            {
                found = ActionLinks.ForPage(objects, entry.Index, out var annotations);

                // The same page to both, or its links are not trusted: PDFium and the reader can
                // disagree about a page tree that is damaged in ways both get past.
                if (annotations != Pdfium.FPDFPage_GetAnnotCount(entry.Page))
                {
                    if (!_pageMismatchLogged) AppLog.Warn($"Page {entry.Index + 1} of {FileName} reads differently without PDFium; its buttons are not links.");
                    _pageMismatchLogged = true;
                    return;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"The buttons and named actions of page {entry.Index + 1} could not be read.", ex);
                return;
            }
        }
        else if (_copiedLinks is not null)
        {
            found = _copiedLinks.GetValueOrDefault(entry.Index);
        }
        else
        {
            pending = _copyJob ??= _engine.Run(JobPriority.Prefetch, () => _copiedLinks ??= ReadCopiedLinks());
            return;
        }

        if (found is null) return;
        foreach (var raw in found)
        {
            if (links.Count >= MaxLinksPerPage) break;
            var target = raw.Target ?? (raw.DestinationName is { } name ? NamedDestination(name) : null);
            if (target is null) continue;
            if (raw.Mark is { } mark && target is GoToTarget { Destination.Mark: null } go)
                target = go with { Destination = go.Destination with { Mark = mark } };
            var area = ToFractionRect(entry, raw.X0, raw.Y1, raw.X1, raw.Y0);
            if (area.IsEmpty) continue;

            // A link annotation PDFium read already: the same link, now knowing what it marks.
            if (raw.Annotation)
            {
                var same = links.FindIndex(l => !l.FromText && SameArea(l.Bounds, area));
                if (same >= 0)
                {
                    links[same] = new PdfLink(links[same].Areas, target, fromText: false);
                    continue;
                }
            }
            links.Add(new PdfLink([area], target, fromText: false));
        }

        static bool SameArea(PageRect a, PageRect b) =>
            Math.Abs(a.X0 - b.X0) < 0.002 && Math.Abs(a.Y0 - b.Y0) < 0.002 && Math.Abs(a.X1 - b.X1) < 0.002 && Math.Abs(a.Y1 - b.Y1) < 0.002;
    }

    /// <summary>A named destination of this document, by its name as the document spells it. Engine thread.</summary>
    private GoToTarget? NamedDestination(byte[] name)
    {
        if (DestinationNameZ(name) is not { } z) return null;
        nint dest;
        fixed (byte* n = z) dest = Pdfium.FPDF_GetNamedDestByName(_doc, n);
        return dest != 0 && ReadDestination(dest) is { } d ? new GoToTarget(d) : null;
    }

    /// <summary>
    /// A destination's name as FPDF_GetNamedDestByName takes it: a C string, which PDFium reads as
    /// a PDF text string and compares as text. A UTF-16 name cannot be a C string (its zero
    /// bytes), so it is passed as UTF-8 with its mark, which reads as the same text.
    /// </summary>
    internal static byte[]? DestinationNameZ(byte[] name)
    {
        byte[] bytes;
        if (name.Length >= 2 && name[0] == 0xFE && name[1] == 0xFF)
        {
            var text = Encoding.BigEndianUnicode.GetString(name, 2, (name.Length - 2) & ~1);
            bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)];
        }
        else
        {
            var end = System.Array.IndexOf(name, (byte)0);
            bytes = end < 0 ? name : name[..end];
        }
        if (bytes.Length == 0 || bytes.Length > 4096 || System.Array.IndexOf(bytes, (byte)0) >= 0) return null;
        return [.. bytes, 0];
    }

    /// <summary>A name given as text: plain ASCII as it is, anything else as UTF-8 with its mark.</summary>
    internal static byte[]? DestinationNameZ(string name) =>
        name.All(char.IsAscii) ? DestinationNameZ(Encoding.ASCII.GetBytes(name)) : DestinationNameZ([0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(name)]);

    /// <summary>
    /// For a file the reader cannot read itself (encrypted, or damaged so PDFium had to repair
    /// it), PDFium writes a copy of the document - plain objects, decrypted - from a second handle,
    /// which is closed again at once, so the open document does not keep every object parsed,
    /// into memory that is given back as soon as the links are read. Engine thread.
    /// </summary>
    private Dictionary<int, List<RawLink>> ReadCopiedLinks()
    {
        var found = new Dictionary<int, List<RawLink>>();
        if (_closed || _source is not { } source || FileLength > MaxCopiedSource) return found;

        // A file rewritten since it was opened is about to be reloaded; a copy of it now could
        // pair this document's pages with another's links.
        if (source.ReadFailed || ChangedSinceOpened()) return found;

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        nint copy;
        var pw = _password is null ? null : Utf8Z(_password);
        fixed (byte* pwPtr = pw) copy = Pdfium.FPDF_LoadCustomDocument(source.Access, pwPtr);
        if (copy == 0) return found;

        try
        {
            // The copy writes object streams out as plain objects, so it can be larger than the file.
            using var buffer = CopyBuffer.Reserve((int)Math.Min(int.MaxValue, (FileLength * 4) + (16 << 20)));
            if (buffer is null || Pdfium.FPDF_SaveAsCopy(copy, buffer.Writer, Pdfium.SaveNoIncremental | Pdfium.SaveRemoveSecurity) == 0) return found;

            var pdf = PdfObjectReader.Open(new MemorySource(buffer.Data, buffer.Length));
            if (pdf is null || pdf.PageCount != PageCount)
            {
                AppLog.Warn($"The copy of {FileName} could not be read for its buttons and named actions.");
                return found;
            }
            for (var page = 0; page < PageCount; page++)
            {
                var links = ActionLinks.ForPage(pdf, page, out _);
                if (links.Count > 0) found[page] = links;
            }
            return found;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"The buttons and named actions of {FileName} could not be read.", ex);
            return found;
        }
        finally
        {
            Pdfium.FPDF_CloseDocument(copy);
            if (StartupTrace.Enabled) StartupTrace.Mark($"copied-links\t{System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.0}");
        }
    }

    /// <summary>
    /// Whether the file is not the one opened any more. Asked on another thread and waited for a
    /// second at most - PDFium's thread must not stand still for a share that does not answer - and
    /// a file that does not say in time counts as changed.
    /// </summary>
    private bool ChangedSinceOpened()
    {
        var (path, length, written) = (FilePath, FileLength, FileWriteTimeUtc);
        var check = Task.Run(() =>
        {
            try
            {
                var file = new FileInfo(path);
                return !file.Exists || file.Length != length || file.LastWriteTimeUtc != written;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return true;
            }
        });
        return !check.Wait(TimeSpan.FromSeconds(1)) || check.Result;
    }

    private void ReadWebLinks(PageEntry entry, nint text, List<PdfLink> links)
    {
        var web = Pdfium.FPDFLink_LoadWebLinks(text);
        if (web == 0) return;

        try
        {
            var authored = links.Count;
            var count = Math.Min(Pdfium.FPDFLink_CountWebLinks(web), MaxLinksPerPage);
            for (var i = 0; i < count; i++)
            {
                var length = Pdfium.FPDFLink_GetURL(web, i, null, 0);
                if (length <= 1 || length > 8192) continue;
                var buffer = new ushort[length];
                int written;
                fixed (ushort* b = buffer) written = Pdfium.FPDFLink_GetURL(web, i, b, length);
                if (written <= 1) continue;
                var url = new string(MemoryMarshal.Cast<ushort, char>(buffer.AsSpan(0, written - 1))).Trim();
                if (url.Length == 0) continue;

                var areas = new List<PageRect>();
                var rects = Pdfium.FPDFLink_CountRects(web, i);
                for (var r = 0; r < rects && r < 64; r++)
                {
                    double l, t, rt, bt;
                    if (Pdfium.FPDFLink_GetRect(web, i, r, &l, &t, &rt, &bt) == 0) continue;
                    var area = ToFractionRect(entry, l, t, rt, bt);
                    if (!area.IsEmpty) areas.Add(area);
                }
                if (areas.Count == 0) continue;

                // The author's own link over the same words wins.
                var covered = false;
                for (var a = 0; a < authored && !covered; a++)
                {
                    foreach (var area in areas)
                    {
                        var centreX = (area.X0 + area.X1) / 2;
                        var centreY = (area.Y0 + area.Y1) / 2;
                        if (links[a].HitTest(centreX, centreY, 0)) { covered = true; break; }
                    }
                }
                if (covered) continue;

                links.Add(new PdfLink(areas, new UriTarget(url), fromText: true));
            }
        }
        finally
        {
            Pdfium.FPDFLink_CloseWebLinks(web);
        }
    }

    private static PageText ReadText(PageEntry entry, nint text)
    {
        var count = Pdfium.FPDFText_CountChars(text);
        if (count <= 0) return PageText.Empty;
        count = Math.Min(count, 2_000_000);

        var codes = new int[count];
        var boxes = new PageRect[count];
        for (var i = 0; i < count; i++)
        {
            codes[i] = (int)Pdfium.FPDFText_GetUnicode(text, i);
            Pdfium.RectF r;
            if (Pdfium.FPDFText_GetLooseCharBox(text, i, &r) != 0)
            {
                var box = ToFractionRect(entry, r.Left, r.Top, r.Right, r.Bottom);
                boxes[i] = box.IsEmpty ? default : box;
            }
        }

        return new PageText(codes, boxes);
    }

    // ===================================================================== text for copying

    /// <summary>
    /// The text of a range of characters that may run over several pages: from character
    /// <paramref name="startChar"/> on <paramref name="startPage"/> to <paramref name="endChar"/>
    /// on <paramref name="endPage"/>, inclusive. -1 as an end means the end of that page.
    /// </summary>
    public Task<string> GetTextAsync(int startPage, int startChar, int endPage, int endChar,
        CancellationToken cancellation = default)
    {
        return _engine.Run(JobPriority.Interactive, () =>
        {
            var sb = new StringBuilder();
            for (var p = Math.Max(0, startPage); p <= endPage && p < PageCount; p++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (_closed) break;

                var entry = GetPage(p, keep: false);
                if (entry is null) continue;
                try
                {
                    var text = TextOf(entry);
                    if (text == 0) continue;

                    var count = Pdfium.FPDFText_CountChars(text);
                    var from = p == startPage ? Math.Max(0, startChar) : 0;
                    var to = p == endPage && endChar >= 0 ? Math.Min(endChar, count - 1) : count - 1;
                    if (to < from) continue;

                    var length = to - from + 1;
                    var buffer = new ushort[length + 1];
                    int written;
                    fixed (ushort* b = buffer) written = Pdfium.FPDFText_GetText(text, from, length, b);
                    if (written > 1)
                    {
                        if (sb.Length > 0 && p != startPage && !EndsWithBreak(sb)) sb.Append("\r\n");
                        sb.Append(MemoryMarshal.Cast<ushort, char>(buffer.AsSpan(0, written - 1)));
                    }
                }
                finally
                {
                    Release(entry);
                }
            }
            return sb.ToString().Replace("\0", string.Empty);
        }, cancellation);
    }

    private static bool EndsWithBreak(StringBuilder sb) => sb.Length > 0 && sb[^1] is '\n' or '\r';

    // ===================================================================== search

    /// <summary>Every occurrence of <paramref name="query"/> on one page.</summary>
    public Task<IReadOnlyList<SearchMatch>> SearchPageAsync(int pageIndex, string query, bool matchCase, bool wholeWord,
        CancellationToken cancellation = default)
    {
        return _engine.Run(JobPriority.Search, () => SearchPage(pageIndex, query, matchCase, wholeWord), cancellation);
    }

    private IReadOnlyList<SearchMatch> SearchPage(int pageIndex, string query, bool matchCase, bool wholeWord)
    {
        if (string.IsNullOrEmpty(query)) return Array.Empty<SearchMatch>();
        var entry = GetPage(pageIndex, keep: false);
        if (entry is null) return Array.Empty<SearchMatch>();

        try
        {
            var text = TextOf(entry);
            if (text == 0) return Array.Empty<SearchMatch>();
            EnsureMatrix(entry);

            var flags = (matchCase ? Pdfium.MatchCase : 0) | (wholeWord ? Pdfium.MatchWholeWord : 0);
            var matches = new List<SearchMatch>();
            nint search;
            fixed (char* q = query) search = Pdfium.FPDFText_FindStart(text, (ushort*)q, flags, 0);
            if (search == 0) return matches;

            try
            {
                while (matches.Count < MaxMatchesPerPage && Pdfium.FPDFText_FindNext(search) != 0)
                {
                    var start = Pdfium.FPDFText_GetSchResultIndex(search);
                    var count = Pdfium.FPDFText_GetSchCount(search);
                    if (start < 0 || count <= 0) continue;

                    var rects = new List<PageRect>();
                    var n = Pdfium.FPDFText_CountRects(text, start, count);
                    for (var r = 0; r < n && r < 256; r++)
                    {
                        double l, t, rt, b;
                        if (Pdfium.FPDFText_GetRect(text, r, &l, &t, &rt, &b) == 0) continue;
                        var rect = ToFractionRect(entry, l, t, rt, b);
                        if (!rect.IsEmpty) rects.Add(rect);
                    }
                    if (rects.Count > 0) matches.Add(new SearchMatch(pageIndex, start, count, rects));
                }
            }
            finally
            {
                Pdfium.FPDFText_FindClose(search);
            }

            return matches;
        }
        finally
        {
            Release(entry);
        }
    }

    // ===================================================================== destinations and actions

    private PdfDestination? ReadDestination(nint dest)
    {
        var page = Pdfium.FPDFDest_GetDestPageIndex(_doc, dest);
        if (page < 0 || page >= PageCount) return null;
        return ReadView(dest, page);
    }

    private static PdfDestination ReadView(nint dest, int page)
    {
        uint n = 0;
        var p = stackalloc float[4];
        var view = Pdfium.FPDFDest_GetView(dest, &n, p);
        switch (view)
        {
            case Pdfium.ViewXyz:
                int hasX, hasY, hasZoom;
                float x, y, zoom;
                if (Pdfium.FPDFDest_GetLocationInPage(dest, &hasX, &hasY, &hasZoom, &x, &y, &zoom) != 0)
                {
                    return new PdfDestination(page, DestinationFit.Xyz,
                        hasX != 0 && float.IsFinite(x) ? x : null,
                        hasY != 0 && float.IsFinite(y) ? y : null);
                }
                return new PdfDestination(page, DestinationFit.Xyz);

            case Pdfium.ViewFitH or Pdfium.ViewFitBH when n >= 1 && float.IsFinite(p[0]):
                return new PdfDestination(page, DestinationFit.FitH, null, p[0]);

            case Pdfium.ViewFitV or Pdfium.ViewFitBV when n >= 1 && float.IsFinite(p[0]):
                return new PdfDestination(page, DestinationFit.FitV, p[0], null);

            case Pdfium.ViewFitR when n >= 4 && float.IsFinite(p[0]) && float.IsFinite(p[3]):
                // [left bottom right top]
                return new PdfDestination(page, DestinationFit.FitR, p[0], p[3],
                    float.IsFinite(p[2]) ? p[2] : null, float.IsFinite(p[1]) ? p[1] : null);

            case Pdfium.ViewFit or Pdfium.ViewFitB:
                return new PdfDestination(page, DestinationFit.Fit);

            default:
                return new PdfDestination(page, DestinationFit.None);
        }
    }

    private LinkTarget? ReadAction(nint action, int pageIndex, int ordinal, int outlineIndex = -1)
    {
        switch (Pdfium.FPDFAction_GetType(action))
        {
            case Pdfium.ActionGoTo:
            {
                var dest = Pdfium.FPDFAction_GetDest(_doc, action);
                return dest != 0 && ReadDestination(dest) is { } d ? new GoToTarget(d) : null;
            }

            case Pdfium.ActionUri:
            {
                var length = Pdfium.FPDFAction_GetURIPath(_doc, action, null, 0);
                if (length <= 1 || length > 65536) return null;
                var buffer = new byte[length];
                fixed (byte* b = buffer) Pdfium.FPDFAction_GetURIPath(_doc, action, b, length);
                var uri = DecodeLenient(buffer.AsSpan(0, (int)length - 1)).Trim();
                return uri.Length > 0 ? new UriTarget(uri) : null;
            }

            case Pdfium.ActionLaunch:
            {
                var path = FilePathOf(action);
                return string.IsNullOrWhiteSpace(path) ? null : new LaunchTarget(path);
            }

            case Pdfium.ActionRemoteGoTo:
            {
                var path = FilePathOf(action);
                return string.IsNullOrWhiteSpace(path) ? null : new RemoteGoToTarget(path, pageIndex, ordinal, outlineIndex);
            }

            default:
                return null;
        }
    }

    private static string FilePathOf(nint action)
    {
        var length = Pdfium.FPDFAction_GetFilePath(action, null, 0);
        if (length <= 1 || length > 65536) return string.Empty;
        var buffer = new byte[length];
        fixed (byte* b = buffer) Pdfium.FPDFAction_GetFilePath(action, b, length);
        return DecodeLenient(buffer.AsSpan(0, (int)length - 1)).Trim();
    }

    /// <summary>Turns a destination into page fractions. Needs the target page, so it is a job.</summary>
    public Task<ResolvedDestination?> ResolveAsync(PdfDestination destination) =>
        _engine.Run(JobPriority.Interactive, () => Resolve(destination));

    private ResolvedDestination? Resolve(PdfDestination destination)
    {
        if ((uint)destination.PageIndex >= (uint)PageCount) return null;
        if (destination.PageX is null && destination.PageY is null && destination.Mark is null)
            return new ResolvedDestination(destination.PageIndex, null, null);

        var entry = GetPage(destination.PageIndex, keep: true);
        if (entry is null) return new ResolvedDestination(destination.PageIndex, null, null);

        EnsureMatrix(entry);
        if (destination.PageX is null && destination.PageY is null)
            return new ResolvedDestination(destination.PageIndex, null, null, MarkArea(entry, destination.Mark!.Value));
        var (fx, fy) = ToFraction(entry, destination.PageX ?? 0, destination.PageY ?? 0);

        // On a page turned by /Rotate the PDF's x is the screen's y; only an axis the
        // destination actually gave is used.
        var yFromPdfY = Math.Abs(entry.By) >= Math.Abs(entry.Ay);
        var xFromPdfX = Math.Abs(entry.Ax) >= Math.Abs(entry.Bx);
        double? x = (xFromPdfX ? destination.PageX : destination.PageY) is not null ? Math.Clamp(fx, 0, 1) : null;
        double? y = (yFromPdfY ? destination.PageY : destination.PageX) is not null ? Math.Clamp(fy, 0, 1) : null;

        // A rectangle (/FitR) is an area of the page; its corners in page fractions.
        PageRect? area = null;
        if (destination is { Fit: DestinationFit.FitR, PageX: { } left, PageY: { } top, PageRight: { } right, PageBottom: { } bottom })
        {
            var (ax, ay) = ToFraction(entry, left, top);
            var (bx, by) = ToFraction(entry, right, bottom);
            var rect = PageRect.FromCorners(Math.Clamp(ax, 0, 1), Math.Clamp(ay, 0, 1), Math.Clamp(bx, 0, 1), Math.Clamp(by, 0, 1));
            if (!rect.IsEmpty) area = rect;
        }
        if (destination.Mark is { } mark) area = MarkArea(entry, mark) ?? area;
        return new ResolvedDestination(destination.PageIndex, x, y, area);
    }

    /// <summary>
    /// A mark's rectangle in page fractions. It is in the page's coordinates as it is shown (what
    /// Acrobat's scripts call rotated user space): PDF space itself on a page that is not turned,
    /// the turned page's own width and height from its lower left corner on one that is.
    /// </summary>
    private PageRect? MarkArea(PageEntry entry, PdfMark mark)
    {
        PageRect rect;
        if ((Pdfium.FPDFPage_GetRotation(entry.Page) & 3) == 0)
        {
            var (ax, ay) = ToFraction(entry, mark.X0, mark.Y0);
            var (bx, by) = ToFraction(entry, mark.X1, mark.Y1);
            rect = PageRect.FromCorners(Math.Clamp(ax, 0, 1), Math.Clamp(ay, 0, 1), Math.Clamp(bx, 0, 1), Math.Clamp(by, 0, 1));
        }
        else
        {
            var size = PageSizes[entry.Index];
            rect = PageRect.FromCorners(
                Math.Clamp(mark.X0 / size.Width, 0, 1), Math.Clamp(1 - (mark.Y0 / size.Height), 0, 1),
                Math.Clamp(mark.X1 / size.Width, 0, 1), Math.Clamp(1 - (mark.Y1 / size.Height), 0, 1));
        }
        return rect.IsEmpty ? null : rect;
    }

    /// <summary>
    /// The destination of a link to another PDF, read with that PDF open: the destination may be
    /// a name only the other file can resolve.
    /// </summary>
    public Task<ResolvedDestination?> ResolveRemoteAsync(RemoteGoToTarget target, PdfDocument other) =>
        _engine.Run(JobPriority.Interactive, () =>
        {
            if (_closed || other.IsClosed) return (ResolvedDestination?)null;
            var action = target.OutlineIndex >= 0 ? OutlineActionAt(target.OutlineIndex) : LinkActionAt(target.PageIndex, target.LinkOrdinal);
            if (action == 0 || Pdfium.FPDFAction_GetType(action) != Pdfium.ActionRemoteGoTo) return null;

            var dest = Pdfium.FPDFAction_GetDest(other._doc, action);
            if (dest == 0) return null;
            var page = Pdfium.FPDFDest_GetDestPageIndex(other._doc, dest);
            if (page < 0 || page >= other.PageCount) return null;
            return other.Resolve(ReadView(dest, page));
        });

    /// <summary>The action of link <paramref name="ordinal"/> on a page, counted as <see cref="ReadLinks"/> counts. Engine thread.</summary>
    private nint LinkActionAt(int pageIndex, int ordinal)
    {
        if (ordinal < 0) return 0;
        var entry = GetPage(pageIndex, keep: true);
        if (entry is null) return 0;

        var position = 0;
        nint link;
        var at = -1;
        while (Pdfium.FPDFLink_Enumerate(entry.Page, &position, &link) != 0)
        {
            if (++at == ordinal) return link != 0 ? Pdfium.FPDFLink_GetAction(link) : 0;
        }
        return 0;
    }

    /// <summary>A named destination of this document, by name: "#nameddest=intro" in a link to it.</summary>
    public Task<ResolvedDestination?> ResolveNamedAsync(string name) =>
        _engine.Run(JobPriority.Interactive, () =>
        {
            if (_closed || string.IsNullOrEmpty(name) || name.Length > 4096) return (ResolvedDestination?)null;
            nint dest;
            if (DestinationNameZ(name) is not { } z) return null;
            fixed (byte* n = z) dest = Pdfium.FPDF_GetNamedDestByName(_doc, n);
            return dest != 0 && ReadDestination(dest) is { } d ? Resolve(d) : null;
        });

    // ===================================================================== outline

    public Task<IReadOnlyList<OutlineItem>> LoadOutlineAsync() =>
        _engine.Run(JobPriority.PageInfo, LoadOutline);

    private IReadOnlyList<OutlineItem> LoadOutline()
    {
        if (_closed) return Array.Empty<OutlineItem>();

        // Each entry's children are gathered while it is open, and it is added to its parent's when it closes.
        var levels = new Stack<List<OutlineItem>>();
        var open = new Stack<(string Title, LinkTarget? Target, bool StartsOpen)>();
        levels.Push(new List<OutlineItem>());
        WalkOutline(
            (bookmark, index) =>
            {
                open.Push((BookmarkTitle(bookmark), BookmarkTarget(bookmark, index), Pdfium.FPDFBookmark_GetCount(bookmark) > 0));
                levels.Push(new List<OutlineItem>());
                return true;
            },
            () =>
            {
                var children = levels.Pop();
                var (title, target, startsOpen) = open.Pop();
                levels.Peek().Add(new OutlineItem(title, target, startsOpen, children.Count > 0 ? children : Array.Empty<OutlineItem>()));
            });
        return levels.Pop();
    }

    /// <summary>
    /// Walks the outline depth first, as it is shown: <paramref name="enter"/> gets each entry and
    /// its index in that order, and returns false to stop; <paramref name="leave"/> follows once
    /// the entry's children are done. A malformed outline can loop back on itself or run on without
    /// end; it is cut the same way for every walk, so an index names the same entry every time.
    /// Engine thread.
    /// </summary>
    private void WalkOutline(Func<nint, int, bool> enter, Action? leave = null)
    {
        var visited = new HashSet<nint>();
        var total = 0;
        Walk(0, 0);

        bool Walk(nint parent, int depth)
        {
            if (depth > MaxOutlineDepth) return true;
            var child = Pdfium.FPDFBookmark_GetFirstChild(_doc, parent);
            while (child != 0 && total < MaxOutlineItems)
            {
                if (!visited.Add(child)) break;
                if (!enter(child, total++)) return false;
                var more = Walk(child, depth + 1);
                leave?.Invoke();
                if (!more) return false;
                child = Pdfium.FPDFBookmark_GetNextSibling(_doc, child);
            }
            return true;
        }
    }

    /// <summary>The action of outline entry <paramref name="index"/>, counted as <see cref="WalkOutline"/> counts. Engine thread.</summary>
    private nint OutlineActionAt(int index)
    {
        nint found = 0;
        WalkOutline((bookmark, at) =>
        {
            if (at != index) return true;
            found = bookmark;
            return false;
        });
        return found != 0 ? Pdfium.FPDFBookmark_GetAction(found) : 0;
    }

    private static string BookmarkTitle(nint bookmark)
    {
        var length = Pdfium.FPDFBookmark_GetTitle(bookmark, null, 0);
        if (length <= 2 || length > 1 << 16) return string.Empty;
        var buffer = new byte[length];
        fixed (byte* b = buffer) Pdfium.FPDFBookmark_GetTitle(bookmark, b, length);
        return CleanText(Utf16Z(buffer));
    }

    private LinkTarget? BookmarkTarget(nint bookmark, int index)
    {
        // As for links: an action into another file must not be read as a place in this one.
        var action = Pdfium.FPDFBookmark_GetAction(bookmark);
        if (action != 0 && Pdfium.FPDFAction_GetType(action) != Pdfium.ActionGoTo) return ReadAction(action, -1, -1, index);

        var dest = Pdfium.FPDFBookmark_GetDest(_doc, bookmark);
        return dest != 0 && ReadDestination(dest) is { } d ? new GoToTarget(d) : null;
    }

    // ===================================================================== strings

    private static byte[] Utf8Z(string s)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(s) + 1];
        Encoding.UTF8.GetBytes(s, bytes);
        return bytes;
    }

    /// <summary>A UTF-16LE buffer that ends in a NUL, as PDFium returns titles and metadata.</summary>
    private static string Utf16Z(byte[] buffer)
    {
        var chars = MemoryMarshal.Cast<byte, char>(buffer.AsSpan(0, buffer.Length & ~1));
        var end = chars.IndexOf('\0');
        return new string(end >= 0 ? chars[..end] : chars);
    }

    /// <summary>UTF-8 when it is UTF-8, Latin-1 otherwise: link targets in the wild are both.</summary>
    private static string DecodeLenient(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>Titles may carry line breaks, tabs and stray control characters; one line of plain text is shown.</summary>
    internal static string CleanText(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }
}
