using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EasyPDF.Pdf;
using EasyPDF.Services;
using EasyPDF.Views;
using static Program;

/// <summary>Draws through a GDI device context, the way a printer receives pages.</summary>
internal static unsafe class GdiCapture
{
    public static int RenderThroughDc(int width, int height, Func<nint, bool> draw, int x0, int y0, int x1, int y1)
    {
        var screen = GetDC(0);
        var dc = CreateCompatibleDC(screen);
        ReleaseDC(0, screen);
        var info = new BitmapInfoHeader
        {
            Size = 40,
            Width = width,
            Height = -height, // top-down
            Planes = 1,
            BitCount = 32,
        };
        var bitmap = CreateDIBSection(dc, ref info, 0, out var bits, 0, 0);
        var old = SelectObject(dc, bitmap);
        try
        {
            PatBlt(dc, 0, 0, width, height, 0x00FF0062 /* WHITENESS */);
            if (!draw(dc)) throw new InvalidOperationException("Drawing into the device context failed.");
            GdiFlush();

            var pixels = (byte*)bits;
            var count = 0;
            for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                var p = pixels + (((y * width) + x) * 4);
                if (p[0] + p[1] + p[2] < 3 * 160) count++;
            }
            return count;
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(bitmap);
            DeleteDC(dc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
        public int Palette0;
    }

    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfoHeader info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool PatBlt(nint dc, int x, int y, int w, int h, uint rop);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
}

internal static class Tools
{
    /// <summary>
    /// Writes the app icon's drawings, one XPS page per icon size, exactly as the windows draw
    /// them. tests/tools/make_icon.py rasterises the pages and packs them into the .ico.
    /// </summary>
    public static int WriteIcon(string xpsPath)
    {
        // One file per size: a batch reuses its first page's size for every page.
        var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };
        foreach (var px in sizes)
        {
            var file = $"{xpsPath}.{px}.xps";
            if (File.Exists(file)) File.Delete(file);
            using var document = new System.Windows.Xps.Packaging.XpsDocument(file, FileAccess.ReadWrite);
            var element = new IconElement(px);
            element.Measure(new Size(px, px));
            element.Arrange(new Rect(0, 0, px, px));
            element.UpdateLayout();
            System.Windows.Xps.Packaging.XpsDocument.CreateXpsDocumentWriter(document).Write(element);
        }
        Console.WriteLine($"Wrote {xpsPath}: {string.Join(", ", sizes)}");
        return 0;
    }

    /// <summary>The icon at one size, filling exactly that many units.</summary>
    private sealed class IconElement : FrameworkElement
    {
        private readonly int _px;

        public IconElement(int px)
        {
            _px = px;
            Width = px;
            Height = px;
        }

        protected override void OnRender(DrawingContext dc)
        {
            // An invisible frame, so the page is the full square even where the icon is transparent.
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, _px, _px));
            AppIconArt.Draw(dc, _px);
        }
    }

    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    /// <summary>
    /// The window as an XPS page: WPF's own vector description of everything it would draw.
    /// (RenderTargetBitmap draws nothing while the Windows session is disconnected; XPS needs no
    /// screen, and tests/tools/xps2png.py turns the pages into PNGs.)
    /// </summary>
    private static void Snap(Window window, string path)
    {
        Pump(80);
        var root = (FrameworkElement)window.Content;
        var xps = Path.ChangeExtension(path, ".xps");
        if (File.Exists(xps)) File.Delete(xps);
        using (var document = new System.Windows.Xps.Packaging.XpsDocument(xps, FileAccess.ReadWrite))
        {
            System.Windows.Xps.Packaging.XpsDocument.CreateXpsDocumentWriter(document).Write(root);
        }
        Console.WriteLine($"Saved {xps}");
    }

    /// <summary>Screenshots of the main states, light and dark, for reviewing the design.</summary>
    public static int Screenshots(string[] args)
    {
        var dir = args.FirstOrDefault() ?? Path.Combine(Temp, "shots");
        Directory.CreateDirectory(dir);
        MainWindow.ShellOpen = _ => true;
        MainWindow.NoticeSink = (_, _) => { };

        SettingsStore.Current.Remember(new RecentFile { Path = Fixture("links.pdf"), OpenedUtc = DateTime.UtcNow.AddMinutes(-3) });
        SettingsStore.Current.Remember(new RecentFile { Path = Fixture("mixed.pdf"), OpenedUtc = DateTime.UtcNow.AddDays(-1) });
        SettingsStore.Current.Remember(new RecentFile { Path = Fixture("search.pdf"), OpenedUtc = DateTime.UtcNow.AddDays(-30) });
        SettingsStore.Current.Remember(new RecentFile { Path = Path.Combine(Temp, "Removed report.pdf"), OpenedUtc = DateTime.UtcNow.AddDays(-2) });
        SettingsStore.Current.SetPinned(Fixture("mixed.pdf"), true);
        SettingsStore.Current.SetPinned(Path.Combine(Temp, "Removed report.pdf"), true);

        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        {
            ThemeService.Current!.Force(theme);
            var name = theme.ToString().ToLowerInvariant();

            var start = App.OpenEmptyWindow(null);
            start.Width = 1180;
            start.Height = 820;
            PumpUntil(() => FileAvailability.Get(Path.Combine(Temp, "Removed report.pdf")) == Availability.Missing, "the missing file to be found missing");
            start.ShowStartPage();
            Pump(100);
            Snap(start, Path.Combine(dir, $"start-{name}.png"));
            start.Close();

            var window = App.OpenEmptyWindow(null);
            window.Width = 1180;
            window.Height = 820;
            Pump(30);
            Await(window.OpenAsync(Fixture("links.pdf")));
            window.SetSidebar(true, remember: false);
            PumpUntil(() => window.PageView.IsScreenComplete() && !window.PageView.Tiles.IsBusy, "render");
            Pump(200);
            var link = window.PageView.ViewPointOf(0, 135 / 612.0, 115 / 792.0);
            window.PageView.PointerMove(link);
            Pump(100);
            Snap(window, Path.Combine(dir, $"document-{name}.png"));

            window.ShowPagesInSidebar();
            window.SearchBox.Text = "page";
            PumpUntil(() => window.Search is { IsComplete: true }, "search");
            PumpUntil(() => window.Thumbnails is { Count: >= 3 }, "thumbnails");
            Pump(300);
            Snap(window, Path.Combine(dir, $"search-{name}.png"));

            window.Width = 620;
            Pump(200);
            Snap(window, Path.Combine(dir, $"narrow-{name}.png"));
            window.Close();

            // A followed link's destination, marked.
            var flash = App.OpenEmptyWindow(null);
            flash.Width = 1180;
            flash.Height = 820;
            Await(flash.OpenAsync(Fixture("links.pdf")));
            PumpUntil(() => flash.PageView.IsScreenComplete() && !flash.PageView.Tiles.IsBusy, "render");
            flash.PageView.RequestPageInfo(0, JobPriority.Interactive);
            PumpUntil(() => flash.PageView.InfoFor(0) is not null, "links");
            var go = flash.PageView.ViewPointOf(0, 135 / 612.0, 175 / 792.0);
            flash.PageView.PointerMove(go);
            flash.PageView.PointerDown(go, 1, false);
            flash.PageView.PointerUp(go);
            PumpUntil(() => flash.PageView.Flashing is not null && flash.PageView.IsScreenComplete() && !flash.PageView.Tiles.IsBusy, "the mark");
            Snap(flash, Path.Combine(dir, $"flash-{name}.png"));

            // One page at a time, just after a page was turned.
            flash.SetSinglePage(true);
            flash.PageView.SetMode(EasyPDF.Viewer.ZoomMode.FitPage);
            flash.PageView.GoToPage(0);
            Pump(100);
            flash.PageView.HandleKey(System.Windows.Input.Key.PageDown, System.Windows.Input.ModifierKeys.None);
            PumpUntil(() => flash.PageView.IsScreenComplete() && !flash.PageView.Tiles.IsBusy, "render");
            Pump(250);
            Snap(flash, Path.Combine(dir, $"single-{name}.png"));
            flash.Close();

            // The trail of jumps over a grid of four pages, the sidebar compact: on a real schematic
            // when one is given (--screenshot <folder> <schematic.pdf>), otherwise on plain pages.
            var real = args.Length > 1 ? Path.GetFullPath(args[1]) : null;
            var many = real is not null && File.Exists(real) ? real : BigPdfWriter.Write(Path.Combine(Temp, "shots30.pdf"), 30, []);
            var grid = App.OpenEmptyWindow(null);
            grid.Width = 1380;
            grid.Height = 860;
            Await(grid.OpenAsync(many), 60000);
            grid.SetSidebar(true, remember: false);
            grid.SetCompactSidebar(true);

            // A cross-reference followed: the element it refers to framed, the trail above.
            if (many == real)
            {
                var view = grid.PageView;
                view.GoToPage(1000);
                view.RequestPageInfo(1000, JobPriority.Interactive);
                PumpUntil(() => view.InfoFor(1000) is { MoreLinksPending: null }, "links", 60000);
                if (view.InfoFor(1000)!.Links.FirstOrDefault(l => l.Target is GoToTarget { Destination.Mark: not null }) is { } reference)
                {
                    view.Activate(reference.Target, 1000);
                    PumpUntil(() => view.Flashing is not null && view.IsScreenComplete() && !view.Tiles.IsBusy, "the mark", 60000);
                    Pump(100);
                    Console.WriteLine($"     jumped to page {view.Flashing!.Value.Page + 1}, marked {view.Flashing.Value.Area}");
                    Snap(grid, Path.Combine(dir, $"jump-{name}.png"));
                }
            }

            foreach (var page in new[] { 4, 11, 20 }) grid.PageView.GoToPage(page);
            grid.SetPageDisplay(new EasyPDF.Viewer.PageGrid(2, 2));
            grid.PageView.HandleKey(System.Windows.Input.Key.PageDown, System.Windows.Input.ModifierKeys.None);
            PumpUntil(() => grid.PageView.IsScreenComplete() && !grid.PageView.Tiles.IsBusy, "render", 60000);
            Pump(300);
            Snap(grid, Path.Combine(dir, $"grid-{name}.png"));
            grid.SetCompactSidebar(false);
            grid.Close();

            // The grid picker, as the page display menu shows it.
            var picker = new Window
            {
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
                Content = new System.Windows.Controls.Border { Child = new GridPicker(new EasyPDF.Viewer.PageGrid(3, 2)) },
            };
            ((System.Windows.Controls.Border)picker.Content).SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "SurfaceBrush");
            picker.Show();
            Pump(100);
            Snap(picker, Path.Combine(dir, $"picker-{name}.png"));
            picker.Close();
        }
        return 0;
    }

    public static unsafe int DiagLeak()
    {
        var steps = new (string Name, Action<PdfDocument> Use)[]
        {
            ("open+close", _ => { }),
            ("page info", d => Await(d.LoadPageInfoAsync(0, JobPriority.Interactive))),
            ("canvas", d => d.Engine.Invoke(() => { using var c = d.RenderCanvas(0, 300, 400, 0, 0, 0, 300, 400, true, out _); return 0; })),
            ("thumbnail", d => Await(d.RenderPageAsync(0, 100, 130, 0, JobPriority.Thumbnail))),
            ("search", d => Await(d.SearchPageAsync(0, "a", false, false))),
            ("outline", d => Await(d.LoadOutlineAsync())),
            ("resolve", d => Await(d.ResolveAsync(new PdfDestination(1, DestinationFit.Xyz, 10, 10)))),
            ("text", d => Await(d.GetTextAsync(0, 0, 1, -1))),
        };
        foreach (var (name, use) in steps)
        {
            var weak = Make(use);
            for (var i = 0; i < 4 && weak.IsAlive; i++)
            {
                Pump(30);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            Console.WriteLine($"{name,-12} {(weak.IsAlive ? "KEPT" : "freed")}");
        }
        return 0;

        static WeakReference Make(Action<PdfDocument> use)
        {
            var doc = Open("links.pdf");
            use(doc);
            Await(doc.CloseAsync());
            // Something else through the engine, so its thread lets go of the last job.
            Await(PdfEngine.Instance.Run(JobPriority.Interactive, () => 0));
            return new WeakReference(doc);
        }
    }

    /// <summary>Opens and closes each fixture many times, without a window, and reports what the process keeps: managed and native.</summary>
    public static int DiagLeak2(string[] args)
    {
        var rounds = 40;
        var names = args.Length > 0 ? args : ["links.pdf", "buttons.pdf", "buttons-encrypted.pdf", "form.pdf", "mixed.pdf", "links-objstm.pdf"];
        (long Managed, long Private) Settle()
        {
            for (var i = 0; i < 3; i++)
            {
                Await(PdfEngine.Instance.Run(JobPriority.Interactive, () => 0));
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Thread.Sleep(20);
            }
            return (GC.GetTotalMemory(true), ProcessMemory.Private());
        }

        if (names[0] == "--trend")
        {
            var file = names.Length > 1 ? names[1] : "links.pdf";
            Settle();
            var start = Settle();
            for (var step = 1; step <= 8; step++)
            {
                for (var i = 0; i < 50; i++)
                {
                    var doc = Await(PdfDocument.OpenAsync(Fixture(file)), 30000).Document!;
                    EngineChecks.Info(doc, 0);
                    Await(doc.CloseAsync());
                }
                var now = Settle();
                Console.WriteLine($"{step * 50,4} opens: managed {(now.Managed - start.Managed) / 1024.0:+0;-0} KB, private {(now.Private - start.Private) / 1024.0:+0;-0} KB, threads {Process.GetCurrentProcess().Threads.Count}");
            }
            return 0;
        }

        foreach (var name in names)
        {
            var password = name.Contains("encrypted", StringComparison.Ordinal) ? "secret" : null;
            void Once(bool full)
            {
                var doc = Await(PdfDocument.OpenAsync(Fixture(name), password), 30000).Document!;
                if (full)
                {
                    EngineChecks.Info(doc, 0);
                    Await(doc.LoadOutlineAsync());
                    PdfEngine.Instance.Invoke(() =>
                    {
                        using var canvas = doc.RenderCanvas(0, 612, 792, 0, 0, 0, 512, 512, keepPage: false, out _);
                        return 0;
                    });
                }
                Await(doc.CloseAsync());
            }
            foreach (var full in new[] { false, true })
            {
                for (var i = 0; i < 5; i++) Once(full);
                var a = Settle();
                for (var i = 0; i < rounds; i++) Once(full);
                var b = Settle();
                Console.WriteLine($"{name,-24} {(full ? "read" : "open"),-5} {rounds} times: managed {(b.Managed - a.Managed) / 1024.0:+0;-0} KB, private {(b.Private - a.Private) / 1024.0:+0;-0} KB");
            }
        }
        return 0;
    }

    /// <summary>How the process's memory grows as pages of a document are drawn, with and without reading their links.</summary>
    public static unsafe int DiagMemory(string[] args)
    {
        var file = args.First(a => !a.StartsWith("--", StringComparison.Ordinal));
        var mode = args.Contains("--info-only") ? "info" : args.Contains("--render-only") ? "render" : "both";
        using var process = Process.GetCurrentProcess();
        long Private()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            process.Refresh();
            return process.PrivateMemorySize64 >> 20;
        }

        Console.WriteLine($"start: {Private()} MB ({mode})");
        var doc = Await(PdfDocument.OpenAsync(file), 600_000).Document!;
        Console.WriteLine($"open: {Private()} MB");
        var all = args.Contains("--all");
        var step = all ? 1 : Math.Max(1, doc.PageCount / 200);
        var visited = 0;
        var limit = all ? int.MaxValue : 200;
        for (var page = 0; page < doc.PageCount && visited < limit; page += step, visited++)
        {
            var size = doc.PageSizes[page];
            int w = (int)(size.Width * 1.25), h = (int)(size.Height * 1.25);
            if (mode != "info")
            {
                PdfEngine.Instance.Invoke(() =>
                {
                    using var canvas = doc.RenderCanvas(page, w, h, 0, 0, 0, Math.Min(512, w), Math.Min(512, h), keepPage: false, out _);
                    return 0;
                }, JobPriority.Interactive);
            }
            if (mode != "render") EngineChecks.Info(doc, page);
            if (visited % (all ? 250 : 50) == (all ? 249 : 49)) Console.WriteLine($"{visited + 1} pages: {Private()} MB");
        }
        Await(doc.CloseAsync());
        Console.WriteLine($"closed: {Private()} MB");
        return 0;
    }

    /// <summary>Times each step of opening and drawing a document's first pages, without a window.</summary>
    public static unsafe int DiagFile(string[] args)
    {
        var file = args.First(a => !a.StartsWith("--", StringComparison.Ordinal));
        var sw = Stopwatch.StartNew();
        using (var fs = FileSource.Open(file))
        {
            for (var round = 0; round < 3; round++)
            {
                sw.Restart();
                var reader = PdfObjectReader.Open(fs)!;
                var xref = sw.ElapsedMilliseconds;
                sw.Restart();
                var fields = reader.CountFormFields(PdfDocument.MaxFormFields);
                var counted = sw.ElapsedMilliseconds;
                sw.Restart();
                var pages = reader.PageCount;
                Console.WriteLine($"reader: cross-reference {xref} ms ({reader.ObjectCount} objects), {fields}+ fields in {counted} ms, {pages} pages in {sw.ElapsedMilliseconds} ms");
            }
        }
        sw.Restart();
        var result = Await(PdfDocument.OpenAsync(file), 600_000);
        Console.WriteLine($"open: {sw.ElapsedMilliseconds} ms, {result.Failure}, pages {result.Document?.PageCount}");
        if (result.Document is not { } doc) return 1;
        foreach (var page in new[] { 1000, 1200 }.Where(p => p < doc.PageCount))
        {
            sw.Restart();
            var info = EngineChecks.Info(doc, page);
            Console.WriteLine($"page {page + 1}: {info.Links.Count} links in {sw.ElapsedMilliseconds} ms: " +
                string.Join("; ", info.Links.GroupBy(l => l.Target.GetType().Name).Select(g => $"{g.Count()} {g.Key}")) +
                $" - first: {info.Links.FirstOrDefault()?.Target}");
        }

        foreach (var page in new[] { 0, 1, 1000 })
        {
            if (page >= doc.PageCount) continue;
            var size = doc.PageSizes[page];
            int w = (int)(size.Width * 1.25), h = (int)(size.Height * 1.25);
            sw.Restart();
            var ink = PdfEngine.Instance.Invoke(() =>
            {
                using var canvas = doc.RenderCanvas(page, w, h, 0, 0, 0, Math.Min(512, w), Math.Min(512, h), keepPage: true, out var ok);
                if (!ok) return -1;
                var count = 0;
                for (var y = 0; y < Math.Min(512, h); y++)
                {
                    var row = canvas.At(0, y);
                    for (var x = 0; x < Math.Min(512, w); x++)
                        if (row[x * 4] + row[(x * 4) + 1] + row[(x * 4) + 2] < 600) count++;
                }
                return count;
            }, JobPriority.Interactive);
            var first = sw.ElapsedMilliseconds;
            sw.Restart();
            PdfEngine.Instance.Invoke(() =>
            {
                using var canvas = doc.RenderCanvas(page, w, h, 0, 512, 0, Math.Min(512, w - 512), Math.Min(512, h), keepPage: true, out _);
                return 0;
            }, JobPriority.Interactive);
            Console.WriteLine($"page {page + 1}: first tile {first} ms ({ink} non-white pixels), second tile {sw.ElapsedMilliseconds} ms");
        }
        Await(doc.CloseAsync());
        return 0;
    }

    /// <summary>Times opening and reading a document in a real window.</summary>
    public static int Bench(string[] args)
    {
        var file = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? BigPdf;
        MainWindow.ShellOpen = _ => true;
        MainWindow.NoticeSink = (t, m) => Console.WriteLine($"{t}: {m}");

        // Warm-up: the first open pays for loading PDFium's fonts and JIT.
        var warm = App.OpenEmptyWindow(null);
        Await(warm.OpenAsync(Fixture("links.pdf")));
        PumpUntil(() => warm.PageView.IsScreenComplete(), "warm-up");
        warm.Close();
        Pump(50);

        var sw = Stopwatch.StartNew();
        var window = App.OpenEmptyWindow(null);
        window.Width = 1400;
        window.Height = 1000;
        Await(window.OpenAsync(file));
        var opened = sw.Elapsed.TotalMilliseconds;
        PumpUntil(() => window.PageView.IsScreenComplete() && !window.PageView.Tiles.IsBusy, "first screen");
        var firstScreen = sw.Elapsed.TotalMilliseconds;

        var view = window.PageView;
        var screens = 0;
        var gen2Before = GC.CollectionCount(2);
        var gen0Before = GC.CollectionCount(0);
        sw.Restart();
        while (screens < 200 && view.CurrentPage < view.PageCount - 1)
        {
            view.ScrollTo(view.Offset.X, view.Offset.Y + view.ViewportPixelsHeight, animate: false);
            PumpUntil(() => view.IsScreenComplete(), "a screen");
            screens++;
        }
        var perScreen = sw.Elapsed.TotalMilliseconds / Math.Max(1, screens);
        Console.WriteLine($"GCs while scrolling: {GC.CollectionCount(0) - gen0Before} gen0, {GC.CollectionCount(2) - gen2Before} gen2");

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var beforeWs = process.WorkingSet64;
        var beforePrivate = process.PrivateMemorySize64;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        process.Refresh();
        Console.WriteLine($"After a full GC:   {process.WorkingSet64 >> 20} MiB working set, {process.PrivateMemorySize64 >> 20} MiB private (was {beforeWs >> 20} / {beforePrivate >> 20})");
        Pump(300);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Pump(300);
        process.Refresh();
        Console.WriteLine($"GC, pump, GC:      {process.WorkingSet64 >> 20} MiB working set, {process.PrivateMemorySize64 >> 20} MiB private");

        // How many tile pixels carry an opaque fourth byte (BGRx rendered as if it were BGRA).
        long opaque = 0, total = 0;
        foreach (var (_, _, drawing) in view.PageDrawings())
        {
            if (drawing is null) continue;
            foreach (var child in drawing.Children.OfType<ImageDrawing>())
            {
                if (child.ImageSource is not BitmapSource bitmap) continue;
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                for (var i = 3; i < pixels.Length; i += 4) { total++; if (pixels[i] == 255) opaque++; }
                Console.WriteLine($"Tile format:       {bitmap.Format}");
                break;
            }
        }
        Console.WriteLine($"Opaque 4th bytes:  {opaque} of {total}");

        void Measure(string what)
        {
            Pump(200);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Pump(100);
            process.Refresh();
            Console.WriteLine($"{what,-19}{process.WorkingSet64 >> 20} MiB working set, {process.PrivateMemorySize64 >> 20} MiB private");
        }

        var document = window.Document!;
        view.SetDocument(null);
        Measure("Tiles dropped:");
        Await(document.CloseAsync());
        Measure("Document closed:");
        window.Close();
        Measure("Window closed:");
        Console.WriteLine($"File:              {file}");
        Console.WriteLine($"Pages:             {view.PageCount}");
        Console.WriteLine($"Open:              {opened:0.0} ms");
        Console.WriteLine($"First screen:      {firstScreen:0.0} ms");
        Console.WriteLine($"Per screen scroll: {perScreen:0.0} ms over {screens} screens (1400x1000)");
        Console.WriteLine($"Tile cache:        {view.Tiles.CachedBytes >> 20} MiB ({view.Tiles.CachedCount} tiles, budget {view.Tiles.Budget >> 20} MiB)");
        Console.WriteLine($"Process:           {process.WorkingSet64 >> 20} MiB working set, {process.PrivateMemorySize64 >> 20} MiB private");
        return 0;
    }
}
