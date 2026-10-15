using System.IO;
using EasyPDF.Pdf;
using EasyPDF.Services;

namespace EasyPDF;

/// <summary>
/// The entry point, ahead of WPF on purpose. Two things happen before a single window exists:
/// a second launch hands its files to the EasyPDF already running and exits at once, and the
/// first document starts opening on the PDFium thread while WPF is still starting - so by the
/// time the window is up, the document usually is too.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        StartupTrace.Mark("main");

        // The installer, having registered EasyPDF for PDF files: Explorer is told, and that is all.
        if (args is ["--associations-changed"])
        {
            ShellNotify.AssociationsChanged();
            return 0;
        }

        var files = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        var alone = args.Contains("--new-instance", StringComparer.OrdinalIgnoreCase);

        if (!alone && SingleInstance.TryForward(files, Environment.CurrentDirectory)) return 0;

        PdfEngine.Warm();
        // Read while WPF starts; the app waits for it only if it is not done by then.
        var settings = Task.Run(() => SettingsStore.Load());
        Task<OpenResult>? first = null;
        string? firstPath = null;
        if (files.Length > 0)
        {
            try
            {
                firstPath = Path.GetFullPath(files[0]);
                first = PdfDocument.OpenAsync(firstPath);
                // PDFium is idle while WPF starts: it uses the time to get the first page ready.
                first.ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully && t.Result.Document is { } document) document.WarmUp();
                }, TaskScheduler.Default);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                firstPath = null;
            }
        }

        StartupTrace.Mark("engine-started");
        var app = new App(files, firstPath, first, claimInstance: !alone, settings);
        StartupTrace.Mark("app-created");
        app.InitializeComponent();
        StartupTrace.Mark("app-resources");
        return app.Run();
    }
}
