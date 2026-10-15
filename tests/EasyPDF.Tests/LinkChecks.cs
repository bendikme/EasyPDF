using System.IO;
using EasyPDF.Services;
using static Program;

/// <summary>What following a link does: the policy that stands between a document and the shell.</summary>
internal static class LinkChecks
{
    public static IEnumerable<(string, Action)> All() =>
    [
        ("Web and mail links open directly", WebLinks),
        ("Script addresses and programs are refused", Refusals),
        ("Files resolve against the document's folder", RelativeFiles),
        ("Network paths ask first and are never probed", NetworkPaths),
        ("Other kinds of address ask first", OtherSchemes),
    ];

    private static readonly string Doc = Path.Combine(Fixtures, "links.pdf");

    private static void WebLinks()
    {
        Equal(LinkPolicy.ForUri("https://example.com/docs?a=1&b=2", Doc), new LinkDecision(LinkAction.OpenWeb, "https://example.com/docs?a=1&b=2"), "https");
        Equal(LinkPolicy.ForUri("http://example.com", Doc).Action, LinkAction.OpenWeb, "http");
        Equal(LinkPolicy.ForUri("HTTPS://EXAMPLE.COM/X", Doc).Action, LinkAction.OpenWeb, "upper case scheme");
        Equal(LinkPolicy.ForUri("mailto:support@example.com", Doc).Action, LinkAction.OpenWeb, "mailto");
        Equal(LinkPolicy.ForUri("  https://example.com/space  ", Doc).Target, "https://example.com/space", "trimmed");
        Equal(LinkPolicy.ForUri("www.example.com/page", Doc), new LinkDecision(LinkAction.OpenWeb, "https://www.example.com/page"), "www without a scheme");
        Equal(LinkPolicy.ForUri("someone@example.com", Doc), new LinkDecision(LinkAction.OpenWeb, "mailto:someone@example.com"), "a bare mail address");
        Equal(LinkPolicy.ForUri("https://example.com/a b", Doc).Action, LinkAction.OpenWeb, "a space is escaped, not refused");
        Check(LinkPolicy.ForUri("https://example.com/a b", Doc).Target.Contains("%20", StringComparison.Ordinal), "escaped space");
    }

    private static void Refusals()
    {
        foreach (var uri in new[]
                 {
                     "javascript:alert(1)", "JavaScript:alert(1)", "vbscript:msgbox(1)", "data:text/html,<script>x</script>",
                     "ms-msdt:/id PCWDiagnostic", "search-ms:query=x", "https://example.com/\nx", "", "shell:startup", "SHELL:::{20D04FE0}",
                     "ms-appinstaller:?source=https://example.com/x.msix", "ms-its:x.chm::/a.htm", "mk:@MSITStore:x.chm::/a.htm", "hcp://system",
                     "ms-word:ofe|u|https://example.com/x.docx", "search:query=x",
                 })
        {
            Equal(LinkPolicy.ForUri(uri, Doc).Action, LinkAction.Refuse, $"URI {uri}");
        }

        foreach (var file in new[]
                 {
                     "calc.exe", @"C:\Windows\System32\cmd.exe", "/C/Windows/notepad.exe", "script.ps1", "setup.msi", "x.bat", "x.lnk",
                     "x.vbs", "x.js", "x.hta", "x.scr", "x.reg", @"\\?\C:\x.pdf", @"\\.\PhysicalDrive0", "help.chm", "x.scf", "x.wsc",
                     "x.sct", "x.rdp", "x.theme", "x.diagcab", "x.appinstaller", "x.iso", "x.vhdx", "x.searchConnector-ms", "x.application",
                     // Device and object-manager paths reach anything, a network server included.
                     @"\??\UNC\no-such-server.invalid\share\x.pdf", @"\??\C:\x.pdf", @"\\?\UNC\server\share\x.pdf", @"\\.\pipe\x",
                     @"\\?\GLOBALROOT\Device\Mup\server\share\x.pdf", "/??/UNC/server/share/x.pdf",
                     // Alternate data streams, and the trailing dots and spaces Windows ignores.
                     "other.pdf:payload.exe", "notes.txt:hidden", "calc.exe.", "calc.exe ", "calc.exe. . ",
                 })
        {
            Equal(LinkPolicy.ForFile(file, Doc).Action, LinkAction.Refuse, $"file {file}");
        }

        Equal(LinkPolicy.ForUri("file:///C:/Windows/System32/calc.exe", Doc).Action, LinkAction.Refuse, "file URI to a program");
        Equal(LinkPolicy.ForUri("calc%2Eexe", Doc).Action, LinkAction.Refuse, "an escaped program name");
        Equal(LinkPolicy.ForUri("%5C%3F%3F%5CUNC%5Cserver%5Cshare%5Cx.pdf", Doc).Action, LinkAction.Refuse, "an escaped device path");
    }

    private static void RelativeFiles()
    {
        var other = Path.Combine(Fixtures, "other.pdf");
        Equal(LinkPolicy.ForFile("other.pdf", Doc), new LinkDecision(LinkAction.OpenPdf, other, true), "relative PDF");
        Equal(LinkPolicy.ForFile("./other.pdf", Doc).Target, other, "dot slash");
        Equal(LinkPolicy.ForUri("other.pdf", Doc), new LinkDecision(LinkAction.OpenPdf, other, true), "relative PDF as a URI");
        Equal(LinkPolicy.ForFile("../fixtures/other.pdf", Doc).Target, other, "dot dot");
        Equal(LinkPolicy.ForFile(other.Replace('\\', '/'), Doc).Target, other, "forward slashes");
        Equal(LinkPolicy.ForFile("missing.pdf", Doc), new LinkDecision(LinkAction.Missing, Path.Combine(Fixtures, "missing.pdf")), "missing");
        Equal(LinkPolicy.ForUri(new Uri(other).AbsoluteUri, Doc), new LinkDecision(LinkAction.OpenPdf, other, true), "file URI");

        // PDF's own absolute form: /C/dir/file is C:\dir\file.
        var drive = other[0];
        var pdfForm = "/" + drive + other[2..].Replace('\\', '/');
        Equal(LinkPolicy.ForFile(pdfForm, Doc).Target, other, "/C/... form");

        var text = Path.Combine(Temp, "notes.txt");
        File.WriteAllText(text, "notes");
        Equal(LinkPolicy.ForFile(text, Doc), new LinkDecision(LinkAction.AskThenOpen, text), "a document of another kind asks");
        Equal(LinkPolicy.ForFile(Temp, Doc).Action, LinkAction.AskThenOpen, "a folder asks");
        Equal(LinkPolicy.ForFile("other.pdf", null).Action, LinkAction.Refuse, "relative with no document folder");

        // Relative URIs are read as a browser reads them: escapes undone, the fragment kept for the PDF.
        Equal(LinkPolicy.ForUri("other.pdf#page=2", Doc), new LinkDecision(LinkAction.OpenPdf, other, true, "page=2"), "a page of another PDF");
        Equal(LinkPolicy.ForUri("other.pdf#nameddest=intro", Doc).Fragment, "nameddest=intro", "a named place in another PDF");
        Equal(LinkPolicy.ForUri(new Uri(other).AbsoluteUri + "#intro", Doc), new LinkDecision(LinkAction.OpenPdf, other, true, "intro"), "a file URI's fragment");
        Equal(LinkPolicy.ForUri("#page=2", Doc).Action, LinkAction.Refuse, "a fragment alone names no file");
        var spaced = Path.Combine(Temp, "My Other.pdf");
        File.Copy(other, spaced, overwrite: true);
        var inTemp = Path.Combine(Temp, "links.pdf");
        Equal(LinkPolicy.ForUri("My%20Other.pdf", inTemp), new LinkDecision(LinkAction.OpenPdf, spaced, true), "an escaped space");
        Equal(LinkPolicy.ForUri("My%20Other.pdf#page=1", inTemp).Fragment, "page=1", "escaped, with a fragment");
        Equal(LinkPolicy.ForUri("notes.txt#x", inTemp).Fragment, null, "only a PDF keeps its fragment");
    }

    private static void NetworkPaths()
    {
        // A server name that does not resolve: if the policy probed it, this would stall for seconds.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var pdf = LinkPolicy.ForFile(@"\\no-such-server.invalid\share\report.pdf", Doc);
        var doc = LinkPolicy.ForUri("file://no-such-server.invalid/share/notes.docx", Doc);
        Check(sw.ElapsedMilliseconds < 200, $"network paths were probed ({sw.ElapsedMilliseconds} ms)");
        Equal(pdf, new LinkDecision(LinkAction.AskThenOpen, @"\\no-such-server.invalid\share\report.pdf", true), "network PDF");
        Equal(doc.Action, LinkAction.AskThenOpen, "network document");
        Equal(LinkPolicy.ForFile(@"\\server\share\run.exe", Doc).Action, LinkAction.Refuse, "network program");
    }

    private static void OtherSchemes()
    {
        foreach (var uri in new[] { "tel:+4712345678", "ms-settings:display", "ftp://example.com/file", "zoommtg://zoom.us/join?confno=1" })
            Equal(LinkPolicy.ForUri(uri, Doc).Action, LinkAction.AskThenOpen, uri);
    }
}

/// <summary>Layout, history, print placement and the other pure logic.</summary>
internal static class LayoutChecks
{
    public static IEnumerable<(string, Action)> All() =>
    [
        ("Pages are laid out in whole pixels, centred, with gaps", Layout),
        ("The page at a point, in gaps and past the ends", PageAt),
        ("Rotating page fractions is undone exactly", Rotation),
        ("Back and forward work like a browser's", History),
        ("Pages are placed on paper turned and shrunk to fit", PrintPlacement),
        ("PDF dates are read in every form", PdfDates),
        ("Settings survive damage and keep recent files in order", Settings),
        ("Another launch hands its files to the running instance", HandOver),
        ("Both palettes meet their contrast targets", PaletteContrast),
    ];

    private static void PaletteContrast()
    {
        foreach (var name in new[] { "Dark", "Light" })
        {
            var palette = new System.Windows.ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/EasyPDF;component/Themes/Palette.{name}.xaml"),
            };
            var problems = ThemeService.FindContrastProblems(palette);
            Check(problems.Count == 0, $"{name}: {string.Join("; ", problems)}");
        }
    }

    private static readonly EasyPDF.Pdf.PageSize Letter = new(612, 792);
    private static readonly EasyPDF.Pdf.PageSize Landscape = new(842, 595);

    private static void Layout()
    {
        var sizes = new[] { Letter, Landscape, Letter };
        var layout = new EasyPDF.Viewer.PageLayout(sizes, 96.0 / 72 * 1.25, 0, gap: 12, margin: 17, viewportWidth: 1200);
        Equal(layout.Width(0), 1020, "letter width at 125%");
        Equal(layout.Height(0), 1320, "letter height at 125%");
        Equal(layout.Width(1), 1403, "landscape width");
        Equal(layout.Top(0), 17.0, "first top is the margin");
        Equal(layout.Top(1), 17.0 + 1320 + 12, "second top");
        Equal(layout.ContentWidth, 1403 + 34, "content width");
        Equal(layout.ExtentWidth, 1437, "extent is the content when wider than the viewport");
        Equal(layout.Left(1), 17, "a page wider than the viewport starts at the margin");
        Equal(layout.Left(0), (1200 - 1020) / 2, "a page that fits is centred in the viewport");

        var zoomedIn = new EasyPDF.Viewer.PageLayout(sizes, 96.0 / 72 * 1.25, 0, gap: 12, margin: 17, viewportWidth: 900);
        Equal(zoomedIn.Left(0), 17, "nothing fits: every page starts at the margin");
        Equal(zoomedIn.Left(1), 17, "nothing fits: the wide page too");
        Equal(layout.ExtentHeight, 17.0 + 1320 + 12 + 992 + 12 + 1320 + 17, "extent height");

        var turned = new EasyPDF.Viewer.PageLayout(sizes, 1, 1, 10, 10, 2000);
        Equal(turned.Width(0), 792, "rotated: width and height swap");
        Equal(turned.Height(1), 842, "rotated landscape");
        Equal(turned.Left(0), (2000 - 792) / 2, "centred in a wide viewport");
        Equal(turned.ExtentWidth, 2000, "the viewport is the extent when everything fits");

        var tiny = new EasyPDF.Viewer.PageLayout([new EasyPDF.Pdf.PageSize(1, 1)], 0.01, 0, 0, 0, 10);
        Equal(tiny.Width(0), 1, "never smaller than a pixel");
        var huge = new EasyPDF.Viewer.PageLayout([new EasyPDF.Pdf.PageSize(200000, 200000)], 64, 0, 0, 0, 10);
        Equal(huge.Width(0), EasyPDF.Viewer.PageLayout.MaxPagePixels, "never larger than the cap");

        var visible = layout.VisibleRange(1000, 1500);
        Equal(visible, (0, 1), "a band across two pages");
        Equal(layout.VisibleRange(0, 10), (-1, -1), "only the top margin");
        Equal(layout.VisibleRange(0, 20), (0, 0), "the top margin and the first page");
        Equal(layout.VisibleRange(layout.ExtentHeight + 10, layout.ExtentHeight + 20), (-1, -1), "past the end");
        Equal(layout.MostVisible(0, 1000), 0, "most visible");
        Equal(layout.MostVisible(1300, 2300), 1, "most visible second");
    }

    private static void PageAt()
    {
        var layout = new EasyPDF.Viewer.PageLayout([Letter, Letter, Letter], 1, 0, gap: 20, margin: 10, viewportWidth: 800);
        Equal(layout.PageAt(-50), 0, "above the first page");
        Equal(layout.PageAt(10), 0, "top of the first page");
        Equal(layout.PageAt(801), 0, "just under the first page, in the gap");
        Equal(layout.PageAt(819), 1, "just above the second page, in the gap");
        Equal(layout.PageAt(10 + 792 + 20), 1, "top of the second page");
        Equal(layout.PageAt(1e9), 2, "below the last page");
    }

    private static void Rotation()
    {
        foreach (var r in new[] { 0, 1, 2, 3, 4, -1 })
        foreach (var (x, y) in new[] { (0.0, 0.0), (1.0, 0.0), (0.25, 0.75), (0.9, 0.1) })
        {
            var (gx, gy) = EasyPDF.Viewer.PageLayout.Rotate(x, y, r);
            var (ux, uy) = EasyPDF.Viewer.PageLayout.Unrotate(gx, gy, r);
            Near(ux, x, 1e-12, $"x after rotation {r}");
            Near(uy, y, 1e-12, $"y after rotation {r}");
        }
        // A quarter turn clockwise takes the top left corner to the top right.
        Equal(EasyPDF.Viewer.PageLayout.Rotate(0, 0, 1), (1.0, 0.0), "top left after a quarter turn");
        Equal(EasyPDF.Viewer.PageLayout.Rotate(1, 0, 1), (1.0, 1.0), "top right after a quarter turn");

        var layout = new EasyPDF.Viewer.PageLayout([new EasyPDF.Pdf.PageSize(100, 200)], 1, 1, 0, 0, 100);
        var (px, py, pw, ph) = layout.ToPagePixels(0, new EasyPDF.Pdf.PageRect(0, 0, 0.5f, 0.25f));
        Equal((px, py, pw, ph), (150.0, 0.0, 50.0, 50.0), "a rectangle on a turned page");
    }

    private static void History()
    {
        var h = new EasyPDF.Viewer.NavigationHistory();
        Check(!h.CanGoBack && !h.CanGoForward, "empty");
        h.Record(new(0, 0));
        h.Record(new(5, 0.5));
        Equal(h.Back(new(9, 0)), new EasyPDF.Viewer.ViewPosition(5, 0.5), "back once");
        Equal(h.Back(new(5, 0.5)), new EasyPDF.Viewer.ViewPosition(0, 0), "back twice");
        Check(!h.CanGoBack, "nothing further back");
        Equal(h.Forward(new(0, 0)), new EasyPDF.Viewer.ViewPosition(5, 0.5), "forward");
        h.Record(new(5, 0.5));
        Check(!h.CanGoForward, "a new jump drops what was ahead");
        h.Record(new(5, 0.505));
        // Two jumps from one page are one place on the trail: the later spot on it, where the reader last was.
        Equal(h.Back(new(7, 0)), new EasyPDF.Viewer.ViewPosition(5, 0.505), "a jump from the same page is remembered once");
        Equal(h.BackEntries.Count, 1, "and only once");
        for (var i = 0; i < 500; i++) h.Record(new(i, 0));
        var steps = 0;
        while (h.CanGoBack) { h.Back(new(0, 0)); steps++; }
        Check(steps <= EasyPDF.Viewer.NavigationHistory.Limit, $"history is bounded: {steps}");
    }

    private static void PrintPlacement()
    {
        // Letter paper at 600 dpi with 1/4" margins: printable 4800 x 6300.
        var p = PrintService.Placement(Letter, 4800, 6300, 600, 600);
        Equal(p.Rotate, 0, "portrait on portrait");
        Check(p.Width <= 4800 && p.Height <= 6300, $"fits: {p}");
        Near(p.Width / (double)p.Height, 612 / 792.0, 0.002, "aspect kept");
        Equal(p.X, (4800 - p.Width) / 2, "centred across");

        var turned = PrintService.Placement(Landscape, 4800, 6300, 600, 600);
        Equal(turned.Rotate, 1, "landscape page turned onto portrait paper");
        Check(turned.Width < turned.Height, "turned page is tall");

        var small = PrintService.Placement(new EasyPDF.Pdf.PageSize(144, 144), 4800, 6300, 600, 600);
        Equal((small.Width, small.Height), (1200, 1200), "a small page is not enlarged");
        Equal((small.X, small.Y), ((4800 - 1200) / 2, (6300 - 1200) / 2), "and is centred");
    }

    private static void PdfDates()
    {
        var full = EasyPDF.Views.PropertiesDialog.FormatPdfDate("D:20240229133015+01'00'");
        var expected = new DateTimeOffset(2024, 2, 29, 13, 30, 15, TimeSpan.FromHours(1)).LocalDateTime.ToString("g", UiLanguage.Culture);
        Equal(full, expected, "full date with offset");
        Check(EasyPDF.Views.PropertiesDialog.FormatPdfDate("D:2023").Length > 0, "year only");
        Check(EasyPDF.Views.PropertiesDialog.FormatPdfDate("20231231235959Z").Length > 0, "no prefix, UTC");
        Equal(EasyPDF.Views.PropertiesDialog.FormatPdfDate("yesterday"), "yesterday", "not a date stays as written");
        Equal(EasyPDF.Views.PropertiesDialog.FormatPdfDate("D:20231340"), new DateTime(2023, 12, 31).ToString("g", UiLanguage.Culture), "out-of-range parts clamp");
        Equal(EasyPDF.Views.PropertiesDialog.FormatPdfDate(""), "", "empty");
    }

    private static void Settings()
    {
        var dir = Path.Combine(Temp, "settings-check");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{ not json");
        var store = SettingsStore.Load(dir);
        Equal(store.Settings.Recent.Count, 0, "a damaged file starts from defaults");

        File.WriteAllText(Path.Combine(dir, "settings.json"),
            """{ "recent": [ { "path": "a.pdf", "page": -4, "zoom": 1e9, "zoomMode": "Bogus", "rotation": 7, "pageOffset": 3 }, null, { "path": "" } ], "sidebarWidth": -5 }""");
        store = SettingsStore.Load(dir);
        var r = store.Settings.Recent.Single();
        Check(r is { Page: 0, Zoom: 1, ZoomMode: "FitWidth", Rotation: 3, PageOffset: 0 }, "bad values are repaired");
        Equal(store.Settings.SidebarWidth, 264.0, "sidebar width repaired");

        for (var i = 0; i < 40; i++) store.Remember(new RecentFile { Path = $"f{i}.pdf", OpenedUtc = DateTime.UtcNow });
        Equal(store.Settings.Recent.Count, SettingsStore.MaxRecent, "recent list is capped");
        Equal(store.Settings.Recent[0].Path, "f39.pdf", "most recent first");
        store.Remember(new RecentFile { Path = "F20.PDF", Page = 3 });
        Equal(store.Settings.Recent[0].Page, 3, "remembering again moves to the top");
        Equal(store.Settings.Recent.Count(x => x.Path.Equals("f20.pdf", StringComparison.OrdinalIgnoreCase)), 1, "no duplicates");
        store.Flush();
        var again = SettingsStore.Load(dir);
        Equal(again.Settings.Recent[0].Path, "F20.PDF", "written and read back");

        // Put the test run's own store back.
        SettingsStore.Load(Temp);
    }

    private static void HandOver()
    {
        var received = new List<string[]>();
        SingleInstance.Name = $"EasyPDF.Tests.HandOver.{Guid.NewGuid():N}";
        Check(!SingleInstance.TryForward(["x.pdf"], Temp, 500), "nothing to hand over to yet");
        SingleInstance.Claim((dir, args) =>
        {
            lock (received) received.Add([dir, .. args]);
            return true;
        });

        var sent = Task.Run(() => SingleInstance.TryForward(["a b.pdf", @"C:\ø\c.pdf"], @"D:\Work dir"));
        Check(sent.Wait(5000) && sent.Result, "the running instance took the files");
        lock (received)
        {
            Equal(received.Count, 1, "one hand-over");
            Equal(string.Join("|", received[0]), @"D:\Work dir|a b.pdf|C:\ø\c.pdf", "directory and files arrive intact");
        }

        SingleInstance.Stop();
        var refused = Task.Run(() => SingleInstance.TryForward(["late.pdf"], Temp, 1500));
        Check(refused.Wait(5000) && !refused.Result, "a stopping instance refuses, so the caller runs on its own");
    }
}
