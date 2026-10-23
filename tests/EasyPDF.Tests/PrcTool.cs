using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EasyPDF.Pdf;
using EasyPDF.Prc;
using EasyPDF.Viewer;

/// <summary>
/// <c>--prc &lt;file.pdf&gt; [page] [--png out.png [--view name]] [--bench]</c>: reads the 3D models of a document's
/// 3D annotations and checks every compressed mesh decodes exactly (every stored value used, the
/// bounding box centred on the stored origin); draws the first model from its first view as the
/// 3D view does, and times frames of it. For checking the PRC reader and the renderer against
/// real files, which cannot be fixtures here.
/// </summary>
internal static class PrcTool
{
    public static int Run(string[] args)
    {
        var file = args.First(a => !a.StartsWith("--", StringComparison.Ordinal));
        var png = args.SkipWhile(a => a != "--png").Skip(1).FirstOrDefault();
        var bench = args.Contains("--bench");
        var viewName = args.SkipWhile(a => a != "--view").Skip(1).FirstOrDefault();
        int? onlyPage = args.Where(a => a != file && a != png && a != viewName && !a.StartsWith("--", StringComparison.Ordinal)).Select(a => int.TryParse(a, out var p) ? p - 1 : (int?)null).FirstOrDefault();

        using var source = FileSource.Open(file);
        var reader = PdfObjectReader.Open(source);
        if (reader is null)
        {
            Console.WriteLine("not a PDF this can read");
            return 1;
        }
        var sw = Stopwatch.StartNew();
        var found = new List<(int Page, Pdf3DModel Model)>();
        var pages = onlyPage is { } single ? [single] : Enumerable.Range(0, reader.PageCount);
        foreach (var page in pages)
            foreach (var annotation in reader.ThreeDAnnotations(page))
                if (reader.ThreeDModel(page, annotation, 256 << 20) is { } model) found.Add((page, model));
        Console.WriteLine($"{found.Count} 3D model(s) found in {sw.ElapsedMilliseconds} ms");

        var failures = 0;
        var drawn = false;
        foreach (var (page, model) in found)
        {
            Console.WriteLine($"page {page + 1}: {model.Kind}, {model.Data.Length:N0} bytes, {model.Views.Count} views, starting at \"{model.Views.FirstOrDefault()?.Name}\"");
            if (model.Kind != "PRC") continue;
            sw.Restart();
            PrcFile prc;
            try
            {
                prc = PrcFile.Read(model.Data);
            }
            catch (PrcFormatException e)
            {
                Console.WriteLine($"  not read: {e.Message}");
                failures++;
                continue;
            }
            Console.WriteLine($"  read in {sw.ElapsedMilliseconds} ms: {prc.FileStructures.Count} file structures, {prc.FileStructures.Sum(f => f.Occurrences.Count)} product occurrences");

            sw.Restart();
            var meshes = prc.FileStructures.SelectMany(f => f.Tessellations.OfType<PrcCompressedTessellation>()).ToList();
            var bad = new List<string>();
            long triangles = 0;
            foreach (var (t, i) in meshes.Select((t, i) => (t, i)))
            {
                var mesh = t.Decode(out var report);
                triangles += t.TriangleFaces.Length;
                if (!report.Exact) bad.Add($"#{i} stops at {report.Triangles}/{report.TrianglesExpected}: {report.Failure ?? "not all values used"}");
                else if (CentreError(t, mesh) > 0.01) bad.Add($"#{i} off its stored centre by {CentreError(t, mesh):P1} of its size");
            }
            Console.WriteLine($"  {meshes.Count} compressed meshes, {triangles:N0} triangles, decoded in {sw.ElapsedMilliseconds} ms: {meshes.Count - bad.Count} exact");
            foreach (var b in bad) Console.WriteLine($"    {b}");
            failures += bad.Count;

            if (drawn || (png is null && !bench)) continue;
            drawn = true;
            sw.Restart();
            var scene = ModelScene.Read(prc);
            var renderer = new ModelRenderer(scene);
            Console.WriteLine($"  placed in {sw.ElapsedMilliseconds} ms: {scene.TriangleCount:N0} triangles ({scene.TriangleCount - scene.OpaqueTriangles:N0} see-through), {scene.LineCount:N0} edge segments, {scene.PartsLeftOut} parts left out");
            var view = viewName is null ? model.Views.FirstOrDefault() : model.Views.FirstOrDefault(v => v.Name == viewName);
            var camera = (view is null ? null : ModelCamera.FromView(view.CameraToWorld, view.FieldOfView, model.Aspect)) ?? ModelCamera.Overview(scene.Min, scene.Max);
            if (png is not null) Save(renderer, camera, png, 1000, 1400);
            if (bench) Bench(renderer, camera);
        }
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// <c>--model-cost &lt;file.pdf&gt;</c>: what looking for 3D annotations adds to reading each
    /// page's links and text, page by page through the whole document - the one thing the 3D
    /// view costs a document that is only read. Through reflection, on PDFium's thread, as the
    /// viewer does it.
    /// </summary>
    public static int Cost(string[] args)
    {
        var file = args.First(a => !a.StartsWith("--", StringComparison.Ordinal));
        var document = Program.Await(PdfDocument.OpenAsync(file), 60000).Document ?? throw new InvalidOperationException("not opened");
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
        var getPage = typeof(PdfDocument).GetMethod("GetPage", flags)!;
        var release = typeof(PdfDocument).GetMethod("Release", flags)!;
        var loadInfo = typeof(PdfDocument).GetMethod("LoadPageInfo", flags)!;
        var readLinks = typeof(PdfDocument).GetMethod("ReadLinks", flags)!;
        var addModels = typeof(PdfDocument).GetMethod("AddModelLinks", flags)!;
        var addActions = typeof(PdfDocument).GetMethod("AddActionLinks", flags)!;

        var count = document.PageCount;
        double info = 0, search = 0;
        int searched = 0, models = 0;
        Program.Await(document.Engine.Run(JobPriority.Interactive, () =>
        {
            for (var page = 0; page < count; page++)
            {
                // A page's info as the viewer reads it, whole...
                var started = Stopwatch.GetTimestamp();
                var result = (PageInfo?)loadInfo.Invoke(document, [page]);
                info += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                models += result?.Links.Count(l => l.Target is ModelTarget) ?? 0;

                // ...and the looking for models on its own, the page still loaded.
                var entry = getPage.Invoke(document, [page, true]);
                if (entry is null) continue;
                object?[] linkArgs = [entry, false, 0];
                var links = (List<PdfLink>)readLinks.Invoke(document, linkArgs)!;
                object?[] actionArgs = [entry, links, linkArgs[1], null, null];
                addActions.Invoke(document, actionArgs);
                started = Stopwatch.GetTimestamp();
                addModels.Invoke(null, [entry, links, (int)linkArgs[2]!, actionArgs[4]]);
                var took = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                search += took;
                if (took > 0.002) searched++;
                release.Invoke(document, [entry]);
            }
            return 0;
        }), 600000);
        Console.WriteLine($"{count} pages: reading their links and text took {info:0} ms ({info / count:0.000} ms a page), of which looking for 3D annotations {search:0.0} ms ({search / count * 1000:0} us a page, {search / info:P2})");
        Console.WriteLine($"{models} 3D annotation(s) found; the pages with annotations besides links were looked through: about {searched}");
        Program.Await(document.CloseAsync());
        return 0;
    }

    /// <summary>
    /// <c>--model-window &lt;file.pdf&gt; &lt;page&gt; &lt;out.png&gt;</c>: the 3D view in a real window, 1400 x 1000:
    /// the model on <c>page</c> clicked open, how long it takes to show and settle, frames a second
    /// while it is turned, memory with it and after, and the window as an XPS page (and a second,
    /// turned, beside it).
    /// </summary>
    public static int Window(string[] args)
    {
        var (file, page, output) = (args[0], int.Parse(args[1]) - 1, args[2]);
        EasyPDF.Views.MainWindow.ShellOpen = _ => true;
        EasyPDF.Views.MainWindow.NoticeSink = (t, m) => Console.WriteLine($"{t}: {m}");
        var window = Program.App.OpenEmptyWindow(null);
        window.Width = 1400;
        window.Height = 1000;
        Program.Await(window.OpenAsync(file), 60000);
        var pages = window.PageView;
        pages.GoToPage(page);
        Program.PumpUntil(() => pages.IsScreenComplete() && !pages.Tiles.IsBusy, "the page", 60000);
        pages.RequestPageInfo(page, JobPriority.Interactive);
        Program.PumpUntil(() => pages.InfoFor(page) is { MoreLinksPending: null }, "its links", 60000);
        var target = pages.InfoFor(page)!.Links.Select(l => l.Target).OfType<ModelTarget>().First();

        var before = Memory();
        var sw = Stopwatch.StartNew();
        window.Follow(target);
        Program.PumpUntil(() => window.ModelPanel?.View.Picture is not null, "the first frame", 60000);
        var first = sw.ElapsedMilliseconds;
        var view = window.ModelPanel!.View;
        Program.PumpUntil(() => view.Settled, "the model to settle", 60000);
        Console.WriteLine($"click to first frame {first} ms, to smooth edges {sw.ElapsedMilliseconds} ms; picture {view.Picture!.PixelWidth} x {view.Picture.PixelHeight}");
        Shoot(window, output);

        var frames = 0;
        view.FramePresented += (_, _) => frames++;
        var turning = Stopwatch.StartNew();
        while (turning.ElapsedMilliseconds < 2000)
        {
            view.Turn(4, 0.6);
            Program.Pump(16);
        }
        Console.WriteLine($"turning: {frames / turning.Elapsed.TotalSeconds:0} frames a second, at {view.Picture!.PixelWidth} x {view.Picture.PixelHeight} as they come");
        Program.PumpUntil(() => view.Settled, "the turned model to settle", 60000);
        Shoot(window, Path.ChangeExtension(output, null) + "-turned.png");
        var shown = Memory();

        window.CloseModel();
        Program.Pump(100);
        var after = Memory();
        Console.WriteLine($"managed heap: {before} MiB before, {shown} MiB with the model, {after} MiB after closing it");
        window.Close();
        return 0;

        static long Memory()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return GC.GetTotalMemory(forceFullCollection: true) >> 20;
        }

        // As an XPS page, which needs no screen (RenderTargetBitmap draws nothing in a disconnected
        // session); tests/tools/xps2png.py turns it into a PNG.
        static void Shoot(System.Windows.Window window, string path)
        {
            Program.Pump(80);
            var xps = Path.ChangeExtension(path, ".xps");
            if (File.Exists(xps)) File.Delete(xps);
            using var document = new System.Windows.Xps.Packaging.XpsDocument(xps, FileAccess.ReadWrite);
            System.Windows.Xps.Packaging.XpsDocument.CreateXpsDocumentWriter(document).Write((System.Windows.FrameworkElement)window.Content);
        }
    }

    private static void Save(ModelRenderer renderer, ModelCamera camera, string path, int width, int height)
    {
        var pixels = new int[width * height];
        var sw = Stopwatch.StartNew();
        renderer.Render(camera, width, height, 2, 0xF4F5F7, 0xCDD0D4, pixels);
        Console.WriteLine($"  drew {width} x {height} with 4 samples a pixel in {sw.ElapsedMilliseconds} ms, to {path}");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4)));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    /// <summary>Frames as the 3D view draws them, turning the model a little each time: full HD at one and four samples a pixel.</summary>
    private static void Bench(ModelRenderer renderer, ModelCamera camera)
    {
        Console.WriteLine($"  {Environment.ProcessorCount} processors");
        foreach (var (width, height) in ((int, int)[])[(1920, 1080), (640, 360)])
            Frames(width, height);
        var pick = Stopwatch.StartNew();
        var hit = renderer.Pick(camera.Eye, camera.Forward);
        Console.WriteLine($"  the point under the middle: {(hit is { } h ? $"{h.X:0.0}, {h.Y:0.0}, {h.Z:0.0}" : "none")}, found in {pick.Elapsed.TotalMilliseconds:0.0} ms");

        void Frames(int width, int height)
        {
        var pixels = new int[width * height];
        var scene = renderer.Scene;
        var up = ModelCamera.NearestAxis(camera.Up);
        foreach (var samples in (int[])[1, 2])
        {
            renderer.Render(camera, width, height, samples, 0xF4F5F7, 0xCDD0D4, pixels);
            var times = new List<(double Total, double Place, double Sort, double Draw)>();
            var c = camera;
            for (var i = 0; i < (samples == 1 ? 60 : 12); i++)
            {
                c = c.Orbit(scene.Centre, up, 0.03, 0.005);
                var sw = Stopwatch.StartNew();
                renderer.Render(c, width, height, samples, 0xF4F5F7, 0xCDD0D4, pixels);
                var (place, sort, draw) = renderer.LastTimes;
                if (i >= times.Capacity / 3) times.Add((sw.Elapsed.TotalMilliseconds, place, sort, draw));
            }
            times.Sort();
            var m = times[times.Count / 2];
            Console.WriteLine($"  {width} x {height}, {samples * samples} sample(s) a pixel: median {m.Total:0.0} ms (placing {m.Place:0.0}, sorting {m.Sort:0.0}, drawing {m.Draw:0.0}), best {times[0].Total:0.0}, worst {times[^1].Total:0.0}");
        }
        }
    }

    /// <summary>How far the mesh's bounding-box centre is from the origin the file stores for it, against the box's diagonal.</summary>
    private static double CentreError(PrcCompressedTessellation t, PrcMesh m)
    {
        if (m.Positions.Length < 3) return 1;
        double[] lo = [double.MaxValue, double.MaxValue, double.MaxValue], hi = [double.MinValue, double.MinValue, double.MinValue];
        for (var i = 0; i < m.Positions.Length; i += 3)
            for (var k = 0; k < 3; k++)
            {
                lo[k] = Math.Min(lo[k], m.Positions[i + k]);
                hi[k] = Math.Max(hi[k], m.Positions[i + k]);
            }
        var size = Math.Max(1e-9, Math.Sqrt(Enumerable.Range(0, 3).Sum(k => (hi[k] - lo[k]) * (hi[k] - lo[k]))));
        var d = Math.Sqrt(Enumerable.Range(0, 3).Sum(k => Math.Pow((lo[k] + hi[k]) / 2 - t.Origin[k], 2)));
        return Math.Min(1, d / size);
    }
}
