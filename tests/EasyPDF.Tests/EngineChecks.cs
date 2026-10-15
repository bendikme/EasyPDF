using System.Diagnostics;
using System.IO;
using EasyPDF.Pdf;
using EasyPDF.Viewer;
using static Program;

/// <summary>PDFium through the engine: opening, rendering, links, outline, text and search.</summary>
internal static unsafe class EngineChecks
{
    public static IEnumerable<(string, Action)> All() =>
    [
        ("Engine runs jobs by priority, then in order", Priorities),
        ("Every fixture opens with the right page count and sizes", OpensFixtures),
        ("Broken, empty, missing and non-PDF files fail cleanly", BrokenFiles),
        ("Passwords: required, wrong, right, and owner-only", Passwords),
        ("A file with a non-ASCII name opens, and is never locked", UnicodePathNotLocked),
        ("Rendered pages have ink where the text is", RendersInk),
        ("Canvas regions equal PDFium's own render, at every rotation and size", CanvasEqualsPlainRender),
        ("Tiles stitch into exactly the full-page render, at every rotation and zoom", TilesMatchFullRender),
        ("Form fields are drawn", FormFieldsDrawn),
        ("Link annotations: every kind, at the right place", LinkAnnotations),
        ("Quadrilateral links are hit only on their quads", QuadPointLinks),
        ("Web addresses in plain text become links", WebLinksFromText),
        ("Links on a page with /Rotate cover their text", RotatedPageLinks),
        ("Named actions are found, in object streams and encrypted files too", NamedActions),
        ("The object reader survives damaged input without reading past it", ObjectReaderFuzz),
        ("The object reader is not made to work for ever", ObjectReaderBombs),
        ("The links of a large document are read from the file in place", LinksOfLargeDocument),
        ("Buttons that go somewhere are links, in plain and encrypted files", ButtonLinks),
        ("A form of thousands of fields is not loaded, so pages draw at once", HugeForm),
        ("A huge form PDFium has to repair is judged by its widgets, and its buttons still work", HugeFormRepaired),
        ("A document opened afresh to free PDFium's memory works on as before", RecycledDocument),
        ("Destinations resolve to the named place on the page", Destinations),
        ("Links into another PDF resolve there, named or numbered", RemoteDestinations),
        ("The outline is read with its nesting and targets", Outline),
        ("A cyclic outline is read without hanging", CyclicOutline),
        ("Text is extracted, accents and all", TextExtraction),
        ("Search honours case and whole words", SearchOptions),
        ("A document search starts at the reader's page and wraps", SearchSessionWraps),
        ("Closing a document with work queued is safe", CloseWithWorkQueued),
        ("Opening is fast and does not read the whole file", OpenIsFast),
        ("A file over 2 GB and over 4 GB opens, renders and has its links", HugeFiles),
    ];

    private static byte[] Render(PdfDocument doc, int page, int pageWidth, int pageHeight, int rotation, int x, int y, int w, int h)
    {
        return PdfEngine.Instance.Invoke(() =>
        {
            var stride = w * 4;
            var buffer = new byte[stride * h];
            fixed (byte* p = buffer) doc.RenderRegion(page, pageWidth, pageHeight, rotation, x, y, w, h, p, stride, keepPage: true);
            return buffer;
        });
    }

    private static int DarkPixels(byte[] bgrx, int w, int x0, int y0, int x1, int y1)
    {
        var count = 0;
        for (var y = y0; y < y1; y++)
        for (var x = x0; x < x1; x++)
        {
            var i = ((y * w) + x) * 4;
            if (bgrx[i] + bgrx[i + 1] + bgrx[i + 2] < 3 * 160) count++;
        }
        return count;
    }

    private static void Priorities()
    {
        var engine = PdfEngine.Instance;
        using var gate = new ManualResetEventSlim(false);
        var order = new List<int>();
        var blocker = engine.Run(JobPriority.Interactive, () => gate.Wait(5000));
        Thread.Sleep(30); // the blocker is running
        var jobs = new[] { 50, 10, 30, 0, 10 }.Select((p, i) => engine.Run(p, () =>
        {
            lock (order) order.Add(p * 10 + i);
        })).ToArray();
        gate.Set();
        Task.WaitAll(jobs, 5000);
        blocker.Wait(5000);
        Equal(string.Join(",", order), "3,101,104,302,500", "execution order (priority*10 + queue position)");
    }

    private static void OpensFixtures()
    {
        foreach (var (name, pages) in new[] { ("links.pdf", 3), ("other.pdf", 2), ("mixed.pdf", 5), ("a4.pdf", 4), ("form.pdf", 1), ("search.pdf", 2), ("cyclic-outline.pdf", 2) })
        {
            var doc = Open(name);
            Equal(doc.PageCount, pages, $"{name} page count");
            Await(doc.CloseAsync());
            Check(doc.IsClosed, $"{name} closed");
        }

        var mixed = Open("mixed.pdf");
        var sizes = mixed.PageSizes.Select(s => $"{s.Width:0}x{s.Height:0}").ToArray();
        Equal(string.Join(" ", sizes), "612x792 842x595 792x612 100x100 2000x3000", "mixed page sizes (as displayed)");
        Await(mixed.CloseAsync());

        var big = Open(BigPdf);
        Equal(big.PageCount, 2000, "big page count");
        Await(big.CloseAsync());
    }

    private static void BrokenFiles()
    {
        OpenResult Try(string path) => Await(PdfDocument.OpenAsync(path));

        Equal(Try(Path.Combine(Temp, "no-such-file.pdf")).Failure, OpenFailure.NotFound, "missing file");
        Equal(Try(Path.Combine(Temp, "no-such-folder", "x.pdf")).Failure, OpenFailure.NotFound, "missing folder");
        Equal(Try(Fixture("empty.pdf")).Failure, OpenFailure.Empty, "empty file");
        Equal(Try(Fixture("notpdf.pdf")).Failure, OpenFailure.NotPdf, "text file");
        Equal(Try(Fixture("garbage.pdf")).Failure, OpenFailure.Damaged, "garbage after a PDF header");
        Equal(Try(Temp).Failure, OpenFailure.AccessDenied, "a folder");

        // PDFium rebuilds what it can of a cut-off file; either way nothing may crash.
        var truncated = Try(Fixture("truncated.pdf"));
        if (truncated.Document is { } doc)
        {
            for (var i = 0; i < doc.PageCount; i++)
            {
                var size = doc.PageSizes[i];
                Render(doc, i, (int)size.Width, (int)size.Height, 0, 0, 0, (int)size.Width, (int)size.Height);
                Await(doc.LoadPageInfoAsync(i, JobPriority.Interactive));
            }
            Await(doc.LoadOutlineAsync());
            Await(doc.CloseAsync());
        }
        else
        {
            Check(truncated.Failure is OpenFailure.Damaged or OpenFailure.NotPdf, $"truncated: {truncated.Failure}");
        }
    }

    private static void Passwords()
    {
        Equal(Await(PdfDocument.OpenAsync(Fixture("encrypted.pdf"))).Failure, OpenFailure.PasswordRequired, "no password");
        Equal(Await(PdfDocument.OpenAsync(Fixture("encrypted.pdf"), "nope")).Failure, OpenFailure.WrongPassword, "wrong password");
        var doc = Open("encrypted.pdf", "secret");
        Check(doc.Metadata.IsEncrypted, "reported as encrypted");
        var text = Await(doc.GetTextAsync(0, 0, 0, -1));
        Check(text.Contains("Blåbærsyltetøy", StringComparison.Ordinal), $"decrypted text: {text}");
        Await(doc.CloseAsync());

        var owner = Open("owner-only.pdf");
        Check(owner.Metadata.IsEncrypted, "owner-only reported as encrypted");
        Await(owner.CloseAsync());
    }

    private static void UnicodePathNotLocked()
    {
        var dir = Path.Combine(Temp, "Blåbær æøå 文件");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Sjøkart — ünïcode.pdf");
        File.Copy(Fixture("links.pdf"), path, overwrite: true);

        var doc = Open(path);
        Equal(doc.PageCount, 3, "pages");

        // The viewer must not lock the file: it can be overwritten, renamed and deleted while open.
        File.Copy(Fixture("other.pdf"), path, overwrite: true);
        var renamed = path + ".renamed";
        File.Move(path, renamed);
        File.Delete(renamed);
        Await(doc.CloseAsync());
    }

    private static void RendersInk()
    {
        var doc = Open("links.pdf");
        var pixels = Render(doc, 0, 612, 792, 0, 0, 0, 612, 792);
        // "Links test document" at 20pt, baseline y=72: ink between y 55 and 75.
        Check(DarkPixels(pixels, 612, 72, 52, 300, 76) > 400, "the title has ink");
        Check(DarkPixels(pixels, 612, 400, 600, 600, 780) == 0, "an empty corner has none");
        Await(doc.CloseAsync());
    }

    /// <summary>A region of a page, rendered the way the tile renderer does it.</summary>
    private static byte[] CanvasRegion(PdfDocument doc, int page, int pageWidth, int pageHeight, int rotation, int x, int y, int w, int h)
    {
        return PdfEngine.Instance.Invoke(() =>
        {
            using var canvas = doc.RenderCanvas(page, pageWidth, pageHeight, rotation, x, y, w, h, keepPage: true, out var ok);
            if (!ok) throw new InvalidOperationException("The canvas did not render.");
            var buffer = new byte[w * h * 4];
            for (var row = 0; row < h; row++)
                new ReadOnlySpan<byte>(canvas.At(x, y + row), w * 4).CopyTo(buffer.AsSpan(row * w * 4, w * 4));
            return buffer;
        });
    }

    private static (int Differing, int Worst) Compare(byte[] a, int aWidth, int ax, int ay, byte[] b, int bWidth, int bx, int by, int w, int h)
    {
        var differing = 0;
        var worst = 0;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (((ay + y) * aWidth) + ax + x) * 4;
            var j = (((by + y) * bWidth) + bx + x) * 4;
            var d = Math.Max(Math.Abs(a[i] - b[j]), Math.Max(Math.Abs(a[i + 1] - b[j + 1]), Math.Abs(a[i + 2] - b[j + 2])));
            if (d == 0) continue;
            differing++;
            worst = Math.Max(worst, d);
        }
        return (differing, worst);
    }

    private static void CanvasEqualsPlainRender()
    {
        foreach (var file in new[] { "a4.pdf", "mixed.pdf", "form.pdf" })
        {
            var doc = Open(file);
            for (var page = 0; page < Math.Min(doc.PageCount, 4); page++)
            foreach (var rotation in new[] { 0, 1, 2, 3 })
            foreach (var scale in new[] { 0.5, 1.0, 1.33, 2.0 })
            {
                var size = doc.PageSizes[page];
                var (w, h) = (rotation & 1) == 0 ? (size.Width, size.Height) : (size.Height, size.Width);
                var pw = Math.Max(1, (int)Math.Round(w * scale));
                var ph = Math.Max(1, (int)Math.Round(h * scale));
                if ((long)pw * ph > 8_000_000) continue;
                var plain = Render(doc, page, pw, ph, rotation, 0, 0, pw, ph);
                var canvas = CanvasRegion(doc, page, pw, ph, rotation, 0, 0, pw, ph);
                var (differing, worst) = Compare(canvas, pw, 0, 0, plain, pw, 0, 0, pw, ph);
                Check(differing == 0, $"{file} page {page + 1} rotation {rotation} scale {scale}: {differing} pixels differ (worst {worst})");
            }
            Await(doc.CloseAsync());
        }
    }

    private static void TilesMatchFullRender()
    {
        // Tiles rendered one by one - and, as the renderer batches them, several at a time - must
        // reproduce the whole page exactly: no seam where tiles meet, at any zoom or rotation.
        const int T = TileRenderer.TileSize;
        foreach (var file in new[] { "links.pdf", "a4.pdf" })
        {
            var doc = Open(file);
            foreach (var rotation in new[] { 0, 1, 2, 3 })
            foreach (var scale in new[] { 1.0, 2.0, 2.37, 3.0 })
            {
                var size = doc.PageSizes[0];
                var (w0, h0) = (rotation & 1) == 0 ? (size.Width, size.Height) : (size.Height, size.Width);
                var pw = (int)Math.Round(w0 * scale);
                var ph = (int)Math.Round(h0 * scale);
                var full = Render(doc, 0, pw, ph, rotation, 0, 0, pw, ph);
                Check(DarkPixels(full, pw, 0, 0, pw, ph) > 1000, $"{file} rotation {rotation} has ink");

                for (var ty = 0; ty < ph; ty += T)
                for (var tx = 0; tx < pw; tx += T)
                {
                    var w = Math.Min(T, pw - tx);
                    var h = Math.Min(T, ph - ty);
                    var tile = CanvasRegion(doc, 0, pw, ph, rotation, tx, ty, w, h);
                    var (differing, worst) = Compare(tile, w, 0, 0, full, pw, tx, ty, w, h);
                    Check(differing == 0, $"{file} rotation {rotation} scale {scale}: tile ({tx},{ty}) differs in {differing} pixels (worst {worst})");
                }

                // A batch of two rows by two columns, as the renderer draws them in one call.
                var bw = Math.Min(2 * T, pw - T);
                var bh = Math.Min(2 * T, ph - T);
                var batch = CanvasRegion(doc, 0, pw, ph, rotation, T, T, bw, bh);
                var (bd, bworst) = Compare(batch, bw, 0, 0, full, pw, T, T, bw, bh);
                Check(bd == 0, $"{file} rotation {rotation} scale {scale}: a batch differs in {bd} pixels (worst {bworst})");
            }
            Await(doc.CloseAsync());
        }
    }

    private static void RecycledDocument()
    {
        foreach (var name in new[] { "links.pdf", "form.pdf", "buttons.pdf" })
        {
            var doc = Open(name);
            var before = Render(doc, 0, 612, 792, 0, 0, 0, 612, 792);
            var linksBefore = string.Join("|", Info(doc, 0).Links.Select(l => l.Target));
            var outlineBefore = Await(doc.LoadOutlineAsync()).Count;

            PdfEngine.Instance.Invoke(() =>
            {
                doc.Recycle();
                return 0;
            });
            Equal(doc.Recycles, 1, $"{name}: opened afresh");

            Check(before.AsSpan().SequenceEqual(Render(doc, 0, 612, 792, 0, 0, 0, 612, 792)), $"{name}: the page draws the same, form fields and all");
            Equal(string.Join("|", Info(doc, 0).Links.Select(l => l.Target)), linksBefore, $"{name}: the same links");
            Equal(Await(doc.LoadOutlineAsync()).Count, outlineBefore, $"{name}: the same outline");
            Check(Await(doc.GetTextAsync(0, 0, 0, -1)).Length > 0, $"{name}: text");
            Check(Await(doc.SearchPageAsync(0, "o", false, false)).Count > 0, $"{name}: search");
            Await(doc.CloseAsync());
        }

        // A file that has changed is not opened afresh: it is about to be reloaded whole.
        var path = Path.Combine(Temp, "recycled.pdf");
        File.Copy(Fixture("links.pdf"), path, overwrite: true);
        var changing = Await(PdfDocument.OpenAsync(path)).Document!;
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
        PdfEngine.Instance.Invoke(() =>
        {
            changing.Recycle();
            return 0;
        });
        Equal(changing.Recycles, 0, "a changed file is left for the reload");
        Check(Render(changing, 0, 612, 792, 0, 0, 0, 612, 792).Length > 0, "and still draws");
        Await(changing.CloseAsync());
    }

    private static void FormFieldsDrawn()
    {
        var doc = Open("form.pdf");
        Check(doc.Metadata.HasForm, "has a form");
        var pixels = Render(doc, 0, 612, 792, 0, 0, 0, 612, 792);
        // The text field (72,100)-(300,130) holds "Hello form"; the checkbox (72,150)-(92,170) is ticked.
        Check(DarkPixels(pixels, 612, 76, 104, 296, 128) > 60, "the field's value is drawn");
        Check(DarkPixels(pixels, 612, 74, 152, 90, 168) > 15, "the checkbox's tick is drawn");
        Await(doc.CloseAsync());
    }

    /// <summary>A page's links and text, complete: when links PDFium leaves out were still being read, the page is read again.</summary>
    internal static PageInfo Info(PdfDocument doc, int page)
    {
        var info = Await(doc.LoadPageInfoAsync(page, JobPriority.Interactive))!;
        if (info.MoreLinksPending is not { } pending) return info;
        Await(pending);
        info = Await(doc.LoadPageInfoAsync(page, JobPriority.Interactive))!;
        Check(info.MoreLinksPending is null, "the links PDFium leaves out are read once");
        return info;
    }

    /// <summary>The middle of a PyMuPDF-style rectangle (top-left origin, points) as page fractions.</summary>
    internal static (double X, double Y) Centre(double x0, double y0, double x1, double y1, double w = 612, double h = 792) =>
        ((x0 + x1) / 2 / w, (y0 + y1) / 2 / h);

    private static void LinkAnnotations()
    {
        var doc = Open("links.pdf");
        var info = Info(doc, 0);

        LinkTarget? At(double x0, double y0, double x1, double y1)
        {
            var (x, y) = Centre(x0, y0, x1, y1);
            return info.LinkAt(x, y)?.Target;
        }

        Equal(At(70, 106, 200, 124), new UriTarget("https://example.com/docs?a=1&b=2"), "URI");
        Check(At(70, 166, 200, 184) is GoToTarget { Destination: { PageIndex: 2, Fit: DestinationFit.Xyz, PageY: 500 } }, $"GoTo XYZ: {At(70, 166, 200, 184)}");
        Check(At(70, 196, 220, 214) is GoToTarget { Destination.PageIndex: 1 }, $"named destination: {At(70, 196, 220, 214)}");
        Equal(At(70, 226, 200, 244), new UriTarget("mailto:support@example.com"), "mailto");
        Equal(At(70, 256, 220, 274), new LaunchTarget("other.pdf"), "launch");
        Check(At(70, 286, 220, 304) is RemoteGoToTarget { Path: "other.pdf", PageIndex: 0 }, $"remote page: {At(70, 286, 220, 304)}");
        Check(At(70, 316, 240, 334) is RemoteGoToTarget { Path: "other.pdf" }, $"remote named: {At(70, 316, 240, 334)}");
        Equal(At(70, 346, 240, 364), null, "a JavaScript action is no link");
        Equal(At(70, 376, 240, 394), new UriTarget("javascript:alert(1)"), "script URI (refused later, by the policy)");
        Equal(At(70, 406, 240, 424), new LaunchTarget("calc.exe"), "launch of a program (refused later)");
        Equal(At(70, 496, 240, 514), new UriTarget("other.pdf"), "relative URI");
        Equal(At(400, 600, 500, 700), null, "nothing where there is no link");

        // Areas match the annotation rectangle: just inside hits, just outside does not.
        var uri = info.LinkAt(Centre(70, 106, 200, 124).X, Centre(70, 106, 200, 124).Y)!;
        Near(uri.Bounds.X0, 70 / 612.0, 1e-4, "URI left");
        Near(uri.Bounds.Y0, 106 / 792.0, 1e-4, "URI top");
        Near(uri.Bounds.X1, 200 / 612.0, 1e-4, "URI right");
        Near(uri.Bounds.Y1, 124 / 792.0, 1e-4, "URI bottom");

        var back = Info(doc, 1).LinkAt(Centre(70, 106, 220, 124).X, Centre(70, 106, 220, 124).Y);
        Check(back?.Target is GoToTarget { Destination: { PageIndex: 0, Fit: DestinationFit.Fit } }, "page 2 links back to page 1 (Fit)");
        Await(doc.CloseAsync());
    }

    private static void NamedActions()
    {
        foreach (var (name, password) in new[] { ("links.pdf", (string?)null), ("links-objstm.pdf", null), ("links-encrypted.pdf", "secret") })
        {
            var doc = Open(name, password);
            LinkTarget? At(int page, double x0, double y0, double x1, double y1)
            {
                var (x, y) = Centre(x0, y0, x1, y1);
                return Info(doc, page).LinkAt(x, y)?.Target;
            }

            Equal(At(0, 70, 346, 240, 364), null, $"{name}: a JavaScript action is still no link");
            Equal(At(0, 70, 106, 200, 124), new UriTarget("https://example.com/docs?a=1&b=2"), $"{name}: other links are unchanged");
            Equal(At(1, 70, 586, 240, 604), new NamedActionTarget(NamedAction.NextPage, 1), $"{name}: next page");
            Equal(At(1, 70, 616, 240, 634), new NamedActionTarget(NamedAction.LastPage, 1), $"{name}: last page");
            Equal(At(2, 70, 586, 240, 604), new NamedActionTarget(NamedAction.FirstPage, 2), $"{name}: first page (with /Type /Action)");
            Equal(At(2, 70, 616, 240, 634), new NamedActionTarget(NamedAction.PrevPage, 2), $"{name}: previous page");
            Equal(At(2, 70, 646, 240, 664), new NamedActionTarget(NamedAction.GoBack, 2), $"{name}: back");
            Equal(At(2, 400, 600, 500, 700), null, $"{name}: nothing where there is no link");
            Await(doc.CloseAsync());
        }

        Equal(NamedActionTarget.From("Quit", 0), null, "names the viewer does not run are no links");
        Equal(NamedActionTarget.From("nextpage", 0), null, "names are case-sensitive");
    }

    /// <summary>
    /// The object reader is fed thousands of damaged files - classic tables, cross-reference and
    /// object streams, a form of buttons - each from memory that ends exactly at an inaccessible
    /// page, so a single byte read past the end would stop the process; and nothing in it may throw.
    /// </summary>
    private static void ObjectReaderFuzz()
    {
        var plain = File.ReadAllBytes(Fixture("links.pdf"));
        var (links, pages) = Guarded(plain, plain.Length);
        Equal(pages, 3, "pages of the plain fixture");
        Equal(string.Join(",", links.Select(l => $"{l.Page}:{(l.Link.Target as NamedActionTarget)?.Action}")), "1:NextPage,1:LastPage,2:FirstPage,2:PrevPage,2:GoBack", "named links of the plain fixture");

        var objstm = File.ReadAllBytes(Fixture("links-objstm.pdf"));
        var (streamed, streamedPages) = Guarded(objstm, objstm.Length);
        Equal(streamedPages, 3, "pages read through a cross-reference stream and object streams");
        Equal(streamed.Count, 5, "named links inside object streams");

        var buttons = File.ReadAllBytes(Fixture("buttons.pdf"));
        Check(Guarded(buttons, buttons.Length).Links.Count >= 6, "the buttons fixture's links");

        var random = new Random(20261015);
        var syntax = System.Text.Encoding.ASCII.GetBytes("<<>>[]()/R obj endobj stream endstream 0 1 9 . - % # \\ \n\r xref trailer startxref /Length /Filter /W /Index");
        var sources = new[] { plain, objstm, buttons };
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 6000; i++)
        {
            var bytes = (byte[])sources[i % sources.Length].Clone();
            var flips = random.Next(0, 24);
            for (var f = 0; f < flips; f++)
            {
                var at = random.Next(bytes.Length);
                bytes[at] = random.Next(2) == 0 ? (byte)random.Next(256) : syntax[random.Next(syntax.Length)];
            }
            // Cut anywhere: inside an object, a stream, the table, the trailer.
            var length = random.Next(3) == 0 ? random.Next(bytes.Length + 1) : bytes.Length;
            Guarded(bytes, length);
        }
        Check(sw.ElapsedMilliseconds < 30000, $"damaged input is read quickly: {sw.ElapsedMilliseconds} ms");

        foreach (var text in new[]
                 {
                     "", "%PDF-1.7", "startxref", "startxref 999999999", "xref 0 99999999 trailer", "1 0 obj << /Type /Catalog /Pages 1 0 R >> endobj",
                     "1 0 obj [1 0 R] endobj trailer << /Root 1 0 R >>", "1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj 2 0 obj << /Kids [2 0 R 2 0 R] >> endobj",
                     new string('[', 100_000), new string('(', 100_000), "<< /A" + new string('/', 50_000), "1 0 obj 2 0 R endobj 2 0 obj 1 0 R endobj trailer << /Root 1 0 R >>",
                     // A cross-reference stream that claims everything, and object streams that contain themselves.
                     "1 0 obj << /Type /XRef /W [1 4 2] /Size 8000000 /Index [0 8000000] /Length 0 >> stream\n\nendstream endobj startxref 0",
                     "1 0 obj << /Type /XRef /W [9 9 9] /Size 3 /Length 3 >> stream\nabc\nendstream endobj startxref 0",
                     "1 0 obj << /Type /ObjStm /N 1 /First 4 /Length 2 0 R >> stream\nxx\nendstream endobj 2 0 obj 1 0 R endobj " +
                     "xref\n0 3\n0000000000 65535 f \n0000000000 00000 n \n0000000079 00000 n \ntrailer << /Size 3 /Root 1 0 R >> startxref 92",
                 })
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(text);
            Guarded(bytes, bytes.Length);
        }
    }

    /// <summary>A large document of plain objects: its named action and buttons are read from the file itself, at once and in place.</summary>
    private static void LinksOfLargeDocument()
    {
        // 2200 pages of 31 KB each: over 64 MB, more than a copy is ever made of.
        var path = BigPdfWriter.Write(Path.Combine(Temp, "named-huge.pdf"), 2200, needles: [], namedLinkPage: 1500, padding: 31_000);
        var length = new FileInfo(path).Length;
        Check(length > PdfDocument.MaxCopiedSource, "larger than any copy");
        var doc = Await(PdfDocument.OpenAsync(path)).Document!;
        Info(doc, 3); // warm: the reader is opened

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var before = process.PrivateMemorySize64;
        var sw = Stopwatch.StartNew();
        var info = Await(doc.LoadPageInfoAsync(1500, JobPriority.Interactive))!;
        var elapsed = sw.ElapsedMilliseconds;
        process.Refresh();
        var grown = process.PrivateMemorySize64 - before;

        Check(info.MoreLinksPending is null, "nothing is left for later");
        var (x, y) = Centre(70, 792 - 620, 240, 792 - 600);
        Equal(info.LinkAt(x, y)?.Target, new NamedActionTarget(NamedAction.NextPage, 1500), $"{length >> 20} MB: the named link");
        Console.WriteLine($"     {length / 1048576.0:0.0} MB, 2200 pages: the page's links read in {elapsed} ms, private memory {grown / 1048576.0:+0.0;-0.0} MB");
        Check(elapsed < 100, $"read in {elapsed} ms");
        Check(grown < 8L << 20, $"nothing big is kept ({grown >> 20} MB)");
        Await(doc.CloseAsync());
    }

    private static void ButtonLinks()
    {
        foreach (var (name, password) in new[] { ("buttons.pdf", (string?)null), ("buttons-encrypted.pdf", "secret") })
        {
            var doc = Open(name, password);
            var info = Info(doc, 0);
            LinkTarget? At(double x0, double y0, double x1, double y1)
            {
                var (x, y) = Centre(x0, y0, x1, y1);
                return info.LinkAt(x, y)?.Target;
            }

            Equal(At(70, 100, 200, 120), new GoToTarget(new PdfDestination(2, DestinationFit.Fit)), $"{name}: a jump, with a script after it");
            Equal(At(70, 140, 200, 160), new UriTarget("https://example.com/button"), $"{name}: a web address");
            Equal(At(70, 180, 200, 200), new RemoteGoToTarget("other.pdf", 0, -1, -1, new PdfDestination(1, DestinationFit.Fit)), $"{name}: a page of another PDF");
            Equal(At(70, 220, 200, 240), new RemoteGoToTarget("other.pdf", 0, -1, -1, null, "intro"), $"{name}: a named place in another PDF");
            Check(At(70, 260, 200, 280) is GoToTarget { Destination: { PageIndex: 2, Fit: DestinationFit.Xyz, PageY: 400 } }, $"{name}: a named place here: {At(70, 260, 200, 280)}");
            Equal(At(70, 300, 200, 320), null, $"{name}: a hidden button is no link");
            Equal(At(70, 340, 200, 360), null, $"{name}: a text field is no link, whatever its action");
            Equal(At(70, 380, 200, 400), null, $"{name}: a button that only runs a script is no link");
            Equal(At(70, 420, 200, 440), new NamedActionTarget(NamedAction.NextPage, 0), $"{name}: a named action");
            Equal(At(70, 460, 200, 480), new GoToTarget(new PdfDestination(1, DestinationFit.Fit)), $"{name}: a widget whose field is above it");
            Equal(At(70, 660, 200, 680), new GoToTarget(new PdfDestination(2, DestinationFit.Fit)), $"{name}: a widget whose action is its field's");
            Check(At(70, 500, 200, 520) is GoToTarget { Destination: { PageIndex: 2, Fit: DestinationFit.Xyz, PageY: 400 } }, $"{name}: a place named in UTF-16: {At(70, 500, 200, 520)}");
            Equal(At(70, 540, 200, 560), new RemoteGoToTarget("other.pdf", 0, -1, -1, null, "slutt-\u00f8"), $"{name}: a place named in UTF-16 in another PDF");
            Equal(At(70, 580, 200, 600), new UriTarget("https://example.com/base/relative.html"), $"{name}: a relative address, against the document's base");
            Equal(At(70, 620, 200, 640), null, $"{name}: a read-only button is no link");

            Check(doc.Metadata.HasForm && doc.HasForm, $"{name}: a small form is loaded, so its fields are drawn");
            Await(doc.CloseAsync());
        }
    }

    /// <summary>
    /// 20,000 buttons: PDFium's form module would take seconds before drawing anything (minutes
    /// for an EPLAN schematic's 200,000). The form is not loaded; the pages draw at once, and
    /// the buttons are links all the same.
    /// </summary>
    private static void HugeForm()
    {
        var path = BigPdfWriter.WriteForm(Path.Combine(Temp, "huge-form.pdf"), pages: 20, buttonsPerPage: 1000);
        var sw = Stopwatch.StartNew();
        var doc = Await(PdfDocument.OpenAsync(path)).Document!;
        var opened = sw.ElapsedMilliseconds;
        Check(doc.Metadata.HasForm, "it has a form");
        Check(!doc.HasForm, "the form is not loaded");

        sw.Restart();
        var pixels = Render(doc, 0, 612, 792, 0, 0, 0, 612, 792);
        var drawn = sw.ElapsedMilliseconds;
        Check(DarkPixels(pixels, 612, 60, 30, 300, 70) > 50, "the page is drawn");
        Check(opened + drawn < 1500, $"open {opened} ms and draw {drawn} ms without waiting for the form");

        sw.Restart();
        var info = Info(doc, 0);
        Check(sw.ElapsedMilliseconds < 1000, $"a page of 1000 buttons is read in {sw.ElapsedMilliseconds} ms");
        Equal(info.Links.Count(l => l.Target is GoToTarget { Destination.PageIndex: 1 }), 1000, "every button is a link to the next page");
        Equal(Info(doc, 19).Links.Count(l => l.Target is GoToTarget { Destination.PageIndex: 0 }), 1000, "the last page's lead to the first");
        Await(doc.CloseAsync());
    }

    /// <summary>The same form, its cross-reference pointing nowhere: only PDFium (which rebuilds it) can read the file.</summary>
    private static void HugeFormRepaired()
    {
        var path = BigPdfWriter.WriteForm(Path.Combine(Temp, "huge-form-repaired.pdf"), pages: 20, buttonsPerPage: 1000, brokenTable: true);
        var sw = Stopwatch.StartNew();
        var doc = Await(PdfDocument.OpenAsync(path)).Document!;
        Check(doc.Metadata.HasForm && !doc.HasForm, "the form is judged by PDFium's widgets and not loaded");
        Render(doc, 0, 612, 792, 0, 0, 0, 612, 792);
        Check(sw.ElapsedMilliseconds < 2000, $"open and draw without waiting for the form: {sw.ElapsedMilliseconds} ms");
        Equal(Info(doc, 0).Links.Count(l => l.Target is GoToTarget { Destination.PageIndex: 1 }), 1000, "the buttons are links, read from PDFium's copy");
        Await(doc.CloseAsync());
    }

    /// <summary>A small PDF of the given objects (numbered from 1, the first the catalog) with a correct cross-reference table.</summary>
    private static byte[] MakePdf(IReadOnlyList<string> objects)
    {
        var sb = new System.Text.StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets) sb.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>Files made to cost a careless reader hours: each is read in a moment, without reading past its end.</summary>
    private static void ObjectReaderBombs()
    {
        // 20,000 page-tree nodes that all share one Kids array listing all of them.
        const int nodes = 20_000;
        var kids = string.Join(" ", Enumerable.Range(4, nodes).Select(n => $"{n} 0 R"));
        var tree = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids 3 0 R >>", $"[{kids}]" };
        tree.AddRange(Enumerable.Repeat("<< /Type /Pages /Kids 3 0 R >>", nodes));
        var sw = Stopwatch.StartNew();
        var bytes = MakePdf(tree);
        Equal(Guarded(bytes, bytes.Length).Pages, -1, "a page tree folded on itself is not read whole");
        Check(sw.ElapsedMilliseconds < 5000, $"and it takes {sw.ElapsedMilliseconds} ms");

        // Ten fields whose kids are one array of 100,000 named fields.
        const int fieldKids = 100_000;
        var form = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [{string.Join(" ", Enumerable.Range(4, 10).Select(n => $"{n} 0 R"))}] >> >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            $"[{string.Join(" ", Enumerable.Range(14, fieldKids).Select(n => $"{n} 0 R"))}]",
        };
        form.AddRange(Enumerable.Repeat("<< /T (f) /Kids 3 0 R >>", 10));
        form.AddRange(Enumerable.Repeat("<< /T (k) /FT /Btn >>", fieldKids));
        bytes = MakePdf(form);
        sw.Restart();
        var counted = WithReader(bytes, pdf => pdf.CountFormFields(PdfDocument.MaxFormFields));
        Equal(counted, PdfDocument.MaxFormFields, "a form whose fields share their kids counts up to the limit");
        Check(sw.ElapsedMilliseconds < 5000, $"and it takes {sw.ElapsedMilliseconds} ms");
    }

    private static T WithReader<T>(byte[] bytes, Func<PdfObjectReader, T> use)
    {
        fixed (byte* p = bytes) return use(PdfObjectReader.Open(new MemorySource(p, bytes.Length))!);
    }

    private static (List<(int Page, RawLink Link)> Links, int Pages) Guarded(byte[] data, int length)
    {
        const int Page = 4096;
        var size = ((length + Page - 1) / Page * Page) + Page;
        var block = (byte*)VirtualAlloc(null, (nuint)size, 0x3000, 0x04);
        if (block == null) throw new OutOfMemoryException();
        try
        {
            uint old;
            if (!VirtualProtect(block + size - Page, Page, 0x01, &old)) throw new InvalidOperationException("no guard page");
            var start = block + size - Page - length;
            data.AsSpan(0, length).CopyTo(new Span<byte>(start, length));

            var links = new List<(int, RawLink)>();
            var pdf = PdfObjectReader.Open(new MemorySource(start, length));
            if (pdf is null) return (links, 0);
            pdf.CountFormFields(100);
            var pages = pdf.PageCount;
            for (var page = 0; page < Math.Min(pages, 50); page++)
                links.AddRange(ActionLinks.ForPage(pdf, page, out _).Select(l => (page, l)));
            return (links, pages);
        }
        finally
        {
            VirtualFree(block, 0, 0x8000);
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern void* VirtualAlloc(void* address, nuint size, uint type, uint protect);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool VirtualProtect(void* address, nuint size, uint protect, uint* old);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool VirtualFree(void* address, nuint size, uint type);

    private static void QuadPointLinks()
    {
        var doc = Open("links.pdf");
        var info = Info(doc, 0);
        var link = info.Links.Single(l => l.Target is UriTarget { Uri: "https://example.com/quad" });
        Equal(link.Areas.Count, 2, "two quads");
        Check(link.HitTest(100 / 612.0, 446 / 792.0, 0), "the first line's quad");
        Check(link.HitTest(100 / 612.0, 476 / 792.0, 0), "the second line's quad");
        Check(!link.HitTest(250 / 612.0, 446 / 792.0, 0), "beside the quads, inside the rectangle");
        Check(!link.HitTest(100 / 612.0, 461 / 792.0, 0), "between the lines");
        Await(doc.CloseAsync());
    }

    private static void WebLinksFromText()
    {
        var doc = Open("links.pdf");
        var info = Info(doc, 0);
        var web = info.Links.Where(l => l.FromText).ToArray();
        Equal(web.Length, 1, "web links found in text");
        Check(web[0].Target is UriTarget { Uri: var u } && u.StartsWith("https://example.org/plain", StringComparison.Ordinal), $"web link: {web[0].Target}");
        // It sits on the second line of text, from "https" onwards.
        Check(web[0].Bounds.Y0 > 135 / 792.0 && web[0].Bounds.Y1 < 156 / 792.0, $"web link is on its line: {web[0].Bounds}");
        Await(doc.CloseAsync());
    }

    private static void RotatedPageLinks()
    {
        var doc = Open("mixed.pdf");
        var info = Info(doc, 2);
        var link = info.Links.SingleOrDefault(l => l.Target is UriTarget { Uri: "https://example.com/rotated" });
        Check(link is not null, "the rotated page's link is read");

        // Every character of "Rotated page link" lies inside the link, wherever /Rotate put them.
        var text = info.Text;
        var phrase = "Rotated page link";
        var all = text.GetText(0, text.Count - 1);
        var at = all.IndexOf(phrase, StringComparison.Ordinal);
        Check(at >= 0, $"the phrase is in the page text: {all}");
        var inside = 0;
        for (var i = at; i < at + phrase.Length; i++)
        {
            var box = text.Boxes[i];
            if (box.IsEmpty) continue;
            if (link!.HitTest((box.X0 + box.X1) / 2, (box.Y0 + box.Y1) / 2, 0.002)) inside++;
        }
        Check(inside >= phrase.Replace(" ", string.Empty).Length - 1, $"characters inside the link: {inside}");
        // Turned a quarter: the link is now taller than it is wide.
        Check(link!.Bounds.Height * 792 > link.Bounds.Width * 612, $"rotated bounds: {link.Bounds}");
        Await(doc.CloseAsync());
    }

    private static void Destinations()
    {
        var doc = Open("links.pdf");
        var info = Info(doc, 0);
        var go = (GoToTarget)info.LinkAt(Centre(70, 166, 200, 184).X, Centre(70, 166, 200, 184).Y)!.Target;
        var resolved = Await(doc.ResolveAsync(go.Destination));
        Check(resolved is { PageIndex: 2 }, "page 3");
        Near(resolved!.Value.Y!.Value, (792 - 500) / 792.0, 1e-4, "XYZ top as a fraction");
        Near(resolved.Value.X!.Value, 72 / 612.0, 1e-4, "XYZ left as a fraction");

        var named = (GoToTarget)info.LinkAt(Centre(70, 196, 220, 214).X, Centre(70, 196, 220, 214).Y)!.Target;
        var namedPlace = Await(doc.ResolveAsync(named.Destination));
        Check(namedPlace is { PageIndex: 1 }, "named destination is page 2");
        Near(namedPlace!.Value.Y!.Value, 0, 1e-4, "named destination's top");

        var fit = Await(doc.ResolveAsync(new PdfDestination(1, DestinationFit.Fit)));
        Check(fit is { PageIndex: 1, X: null, Y: null }, "Fit has no point");
        Check(Await(doc.ResolveAsync(new PdfDestination(99))) is null, "a page past the end resolves to nothing");

        // A /Rotate 90 page: a PDF y names the screen's x.
        var mixed = Open("mixed.pdf");
        var r = Await(mixed.ResolveAsync(new PdfDestination(2, DestinationFit.FitH, null, 692)));
        Check(r is { Y: null } && r.Value.X is { } rx && Math.Abs(rx - (692 / 792.0)) < 0.01, $"FitH on a rotated page: {r}");
        Await(mixed.CloseAsync());
        Await(doc.CloseAsync());
    }

    private static void RemoteDestinations()
    {
        var doc = Open("links.pdf");
        var other = Open("other.pdf");
        var info = Info(doc, 0);

        var numbered = (RemoteGoToTarget)info.LinkAt(Centre(70, 286, 220, 304).X, Centre(70, 286, 220, 304).Y)!.Target;
        var a = Await(doc.ResolveRemoteAsync(numbered, other));
        Check(a is { PageIndex: 1 }, $"[1 /Fit] in the other file is its page 2: {a}");

        var named = (RemoteGoToTarget)info.LinkAt(Centre(70, 316, 240, 334).X, Centre(70, 316, 240, 334).Y)!.Target;
        var b = Await(doc.ResolveRemoteAsync(named, other));
        Check(b is { PageIndex: 1 }, $"(intro) in the other file is its page 2: {b}");
        Near(b!.Value.Y!.Value, (792 - 420) / 792.0, 1e-4, "intro's top");

        Await(other.CloseAsync());
        Check(Await(doc.ResolveRemoteAsync(named, other)) is null, "a closed target resolves to nothing");
        Await(doc.CloseAsync());
    }

    private static void Outline()
    {
        var doc = Open("links.pdf");
        var items = Await(doc.LoadOutlineAsync());
        Equal(string.Join("|", items.Select(i => i.Title)), "Introduction|Chapter 2|Chapter 3|Website|Other intro", "top level");
        Equal(items[0].PageIndex, 0, "Introduction page");
        Equal(items[1].PageIndex, 1, "Chapter 2 page");
        Equal(items[1].Children.Count, 1, "Chapter 2 children");
        Equal(items[1].Children[0].Title, "Section 2.1", "child title");
        var section = (GoToTarget)items[1].Children[0].Target!;
        var place = Await(doc.ResolveAsync(section.Destination));
        Check(place is { PageIndex: 1 } && place.Value.Y is { } y && Math.Abs(y - (400 / 792.0)) < 0.02, $"Section 2.1 place: {place}");
        Equal(items[2].PageIndex, 2, "Chapter 3 page");
        Equal(items[3].Target, new UriTarget("https://example.com/toc"), "Website target");

        // An entry into another PDF finds its place there, by a name only that file knows.
        Equal(items[4].Target, new RemoteGoToTarget("other.pdf", -1, -1, 5), "Other intro target");
        var other = Open("other.pdf");
        var there = Await(doc.ResolveRemoteAsync((RemoteGoToTarget)items[4].Target!, other));
        Check(there is { PageIndex: 1 } && there.Value.Y is { } ty && Math.Abs(ty - (372 / 792.0)) < 0.02, $"Other intro place: {there}");
        var named = Await(other.ResolveNamedAsync("intro"));
        Check(named is { PageIndex: 1 } && named.Value.Y is { } ny && Math.Abs(ny - (372 / 792.0)) < 0.02, $"#nameddest=intro: {named}");
        Equal(Await(other.ResolveNamedAsync("no-such-place")), null, "an unknown name");
        var utf16 = Await(other.ResolveNamedAsync("slutt-\u00f8"));
        Check(utf16 is { PageIndex: 1 } && utf16.Value.Y is { } uy && Math.Abs(uy - (372 / 792.0)) < 0.02, $"a name that is not ASCII: {utf16}");
        Check(PdfDocument.DestinationNameZ([0xFE, 0xFF, 0x00, 0x41])!.SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF, 0x41, 0x00 }), "UTF-16 names reach PDFium as UTF-8");
        Check(Await(other.LoadOutlineAsync()).Count == 0, "no outline");
        Await(other.CloseAsync());
        Await(doc.CloseAsync());
    }

    private static void CyclicOutline()
    {
        var doc = Open("cyclic-outline.pdf");
        var task = doc.LoadOutlineAsync();
        var items = Await(task, 5000);
        Check(items.Count is >= 1 and <= 2, $"entries: {items.Count}");
        static int Count(IReadOnlyList<OutlineItem> list) => list.Sum(i => 1 + Count(i.Children));
        Check(Count(items) < 10, $"a loop was not followed: {Count(items)} entries");
        Await(doc.CloseAsync());
    }

    private static void TextExtraction()
    {
        var doc = Open("search.pdf");
        var info = Info(doc, 0);
        var all = info.Text.GetText(0, info.Text.Count - 1);
        Check(all.Contains("alpha beta Alpha gamma alphabet ALPHA", StringComparison.Ordinal), $"page text: {all}");
        Check(all.Contains("second line with alpha", StringComparison.Ordinal), "second line");

        var copied = Await(doc.GetTextAsync(0, 0, 1, -1));
        Check(copied.Contains("alphabet", StringComparison.Ordinal) && copied.Contains("page two alpha", StringComparison.Ordinal), $"text over two pages: {copied}");
        var part = Await(doc.GetTextAsync(0, 6, 0, 9));
        Equal(part, "beta", "a range");

        // Character boxes sit on the text: "alpha" begins at x=72pt on the line at y≈72pt.
        var first = info.Text.Boxes.First(b => !b.IsEmpty);
        Near(first.X0 * 612, 72, 2, "first character's left");
        Check(first.Y0 * 792 < 72 && first.Y1 * 792 > 64, $"first character's line: {first}");
        Await(doc.CloseAsync());
    }

    private static void SearchOptions()
    {
        var doc = Open("search.pdf");
        int Count(bool matchCase, bool whole) => Await(doc.SearchPageAsync(0, "alpha", matchCase, whole)).Count;
        Equal(Count(false, false), 5, "any case, any part of a word");
        Equal(Count(true, false), 3, "match case");
        Equal(Count(false, true), 4, "whole words");
        Equal(Count(true, true), 2, "match case, whole words");
        Equal(Await(doc.SearchPageAsync(1, "alpha", false, false)).Count, 1, "page two");
        Equal(Await(doc.SearchPageAsync(0, "missing", false, false)).Count, 0, "no match");

        var match = Await(doc.SearchPageAsync(0, "gamma", false, false)).Single();
        Check(match.Rects.Count == 1 && match.Bounds.Y0 * 792 < 72 && match.Bounds.Y1 * 792 > 62, $"match rectangle: {match.Bounds}");
        Await(doc.CloseAsync());
    }

    private static void SearchSessionWraps()
    {
        var doc = Open(BigPdf);
        var session = new SearchSession(doc, "needle", false, false, startPage: 1000);
        SearchMatch? shown = null;
        session.CurrentChanged += m => shown = m;
        var run = session.RunAsync();
        PumpUntil(() => shown is not null, "the first match");
        Equal(shown!.PageIndex, 1499, "first match is the first after the reader's page");
        Await(run, 60000);
        Check(session.IsComplete, "complete");
        Equal(session.Matches.Count, 2, "matches");
        Equal(session.Matches[0].PageIndex, 499, "kept in document order");
        session.Next();
        Equal(shown!.PageIndex, 499, "next wraps to the start");
        session.Previous();
        Equal(shown!.PageIndex, 1499, "previous wraps to the end");
        Await(doc.CloseAsync());
    }

    private static void CloseWithWorkQueued()
    {
        for (var round = 0; round < 20; round++)
        {
            var doc = Open("links.pdf");
            var work = new List<Task>();
            for (var i = 0; i < 6; i++)
            {
                work.Add(doc.LoadPageInfoAsync(i % 3, JobPriority.Prefetch));
                work.Add(doc.RenderPageAsync(i % 3, 300, 400, 0, JobPriority.Thumbnail));
                work.Add(doc.SearchPageAsync(i % 3, "page", false, false));
            }
            var close = doc.CloseAsync();
            work.Add(doc.LoadOutlineAsync());
            work.Add(doc.GetTextAsync(0, 0, 2, -1));
            Await(close);
            foreach (var task in work)
            {
                PumpUntil(() => task.IsCompleted, "queued work");
                Check(!task.IsFaulted, $"queued work after close failed: {task.Exception?.GetBaseException()}");
            }
        }
    }

    /// <summary>
    /// Sparse files, so they take no disk: a page whose objects all lie past 2 GB (and past 4 GB),
    /// behind a stream of nothing.
    /// </summary>
    private static void HugeFiles()
    {
        foreach (var gap in new[] { 2_600_000_000L, 4_400_000_000L })
        {
            var path = Path.Combine(Temp, $"huge-{gap >> 30}.pdf");
            WriteSparsePdf(path, gap);
            try
            {
                var sw = Stopwatch.StartNew();
                var result = Await(PdfDocument.OpenAsync(path));
                Check(result.Document is not null, $"{gap >> 30} GB: opens ({result.Failure} {result.Detail})");
                var doc = result.Document!;
                Check(sw.ElapsedMilliseconds < 2000, $"{gap >> 30} GB: opened in {sw.ElapsedMilliseconds} ms");
                Equal(doc.PageCount, 1, $"{gap >> 30} GB: pages");
                var pixels = Render(doc, 0, 612, 792, 0, 0, 0, 612, 792);
                Check(DarkPixels(pixels, 612, 60, 60, 400, 120) > 50, $"{gap >> 30} GB: the page's text is drawn");
                Equal(Info(doc, 0).LinkAt(Centre(72, 200, 300, 220).X, Centre(72, 200, 300, 220).Y)?.Target, new UriTarget("https://example.com/huge"), $"{gap >> 30} GB: the link");

                // Never locked, however it was opened.
                using (var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                    Check(writer.CanWrite, $"{gap >> 30} GB: the file can still be written");
                Await(doc.CloseAsync());
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    private static void WriteSparsePdf(string path, long gap)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        uint returned;
        if (!DeviceIoControl(stream.SafeFileHandle, 0x000900C4, null, 0, null, 0, &returned, null))
            throw new IOException("The file system cannot make sparse files.");

        var latin = System.Text.Encoding.Latin1;
        var offsets = new long[7];
        void Raw(string text) => stream.Write(latin.GetBytes(text));
        void Obj(int number, string body)
        {
            offsets[number] = stream.Position;
            Raw($"{number} 0 obj\n{body}\nendobj\n");
        }

        Raw("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n");
        offsets[1] = stream.Position;
        Raw($"1 0 obj\n<< /Length {gap} >>\nstream\n");
        stream.Position += gap; // never written: a hole in the file
        Raw("\nendstream\nendobj\n");
        Obj(2, "<< /Type /Catalog /Pages 3 0 R >>");
        Obj(3, "<< /Type /Pages /Kids [4 0 R] /Count 1 >>");
        Obj(4, "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 6 0 R " +
               "/Annots [<< /Type /Annot /Subtype /Link /Rect [72 572 300 592] /A << /S /URI /URI (https://example.com/huge) >> >>] >>");
        Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        const string content = "BT /F1 36 Tf 72 700 Td (Far beyond 2 GB) Tj ET";
        Obj(6, $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");

        var xref = stream.Position;
        Raw("xref\n0 7\n0000000000 65535 f \n");
        for (var i = 1; i <= 6; i++) Raw($"{offsets[i]:D10} 00000 n \n");
        Raw($"trailer\n<< /Size 7 /Root 2 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint code, void* input, uint inputSize,
        void* output, uint outputSize, uint* returned, void* overlapped);

    private static void OpenIsFast()
    {
        // Warm up, then time a fresh open of the 2000-page file.
        Await(Open(BigPdf).CloseAsync());
        var sw = Stopwatch.StartNew();
        var doc = Open(BigPdf);
        var opened = sw.ElapsedMilliseconds;
        var size = doc.PageSizes[1999];
        Render(doc, 1999, (int)size.Width, (int)size.Height, 0, 0, 0, (int)size.Width, (int)size.Height);
        var rendered = sw.ElapsedMilliseconds;
        Console.WriteLine($"     2000-page file: open {opened} ms, last page rendered at {rendered} ms");
        Check(opened < 400, $"open took {opened} ms");
        Check(rendered < 600, $"open and render took {rendered} ms");
        Await(doc.CloseAsync());
    }
}
