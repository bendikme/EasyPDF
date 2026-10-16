using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using EasyPDF;
using EasyPDF.Pdf;
using EasyPDF.Services;
using EasyPDF.Views;

/// <summary>
/// EasyPDF's regression checks: a console program that exits nonzero when any check fails.
/// Windows are created off screen and never activated, links never reach the shell, and nothing
/// touches the user's own settings or log.
/// </summary>
public static partial class Program
{
    private static int _failed;
    private static int _passed;
    private static readonly List<string> Failures = new();

    internal static App App = null!;
    internal static string Fixtures = null!;
    internal static string Temp = null!;

    [STAThread]
    private static int Main(string[] args)
    {
        // Windows' default 15.6 ms timer would make every short wait in the pump that long.
        TimeBeginPeriod(1);
        Fixtures = FindFixtures();
        Temp = Path.Combine(Path.GetTempPath(), $"EasyPDF.Tests.{Environment.ProcessId}");
        Directory.CreateDirectory(Temp);

        // Nothing of the checks may reach the user's own data folder - set before anything of the
        // app is touched. And checks that provoke warnings do not log at all.
        Environment.SetEnvironmentVariable("EASYPDF_DATA", Path.Combine(Temp, "data"));
        typeof(AppLog).GetField("_enabled", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, false);

        // The app always runs inside Application.Run, where awaits come back to the UI thread;
        // this console program pumps the dispatcher itself, so it installs that context itself.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        App.HostedByTests = true;
        App = new App();
        App.InitializeComponent();
        App.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        App.QuietWindows = true;
        SettingsStore.Load(Temp);
        UiLanguage.Apply("en");
        ThemeService.Start(App, null);
        SingleInstance.Name = $"EasyPDF.Tests.{Guid.NewGuid():N}";
        Console.WriteLine($"UI language {UiLanguage.Current} ({UiLanguage.Culture.Name}); \"{EasyPDF.Resources.Strings.Main_PageOf}\"; thread {Thread.CurrentThread.CurrentUICulture.Name}");

        try
        {
            if (args is ["--write-icon", var iconPath]) return Tools.WriteIcon(iconPath);
            if (args is ["--write-big", var bigPath]) { BigPdfWriter.Write(bigPath, 2000, needles: [499, 1499]); return 0; }
            if (args.Length > 0 && args[0] == "--screenshot") return Tools.Screenshots(args.Skip(1).ToArray());
            if (args.Length > 0 && args[0] == "--bench") return Tools.Bench(args.Skip(1).ToArray());
            if (args.Length > 0 && args[0] == "--diag-file") return Tools.DiagFile(args.Skip(1).ToArray());
            if (args.Length > 0 && args[0] == "--diag-memory") return Tools.DiagMemory(args.Skip(1).ToArray());
            if (args.Length > 0 && args[0] == "--diag-leak2") return Tools.DiagLeak2(args.Skip(1).ToArray());
            if (args.Length > 0 && args[0] == "--update-e2e") return Tools.UpdateEndToEnd(args.Skip(1).ToArray());
            if (args.Length > 0 && args[0] == "--window-cost")
            {
                for (var i = 0; i < 3; i++)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var w = new MainWindow(App);
                    var built = sw.Elapsed.TotalMilliseconds;
                    w.Show();
                    Pump(1);
                    Console.WriteLine($"window {i + 1}: constructed {built:0.0} ms, shown {sw.Elapsed.TotalMilliseconds:0.0} ms");
                }
                return 0;
            }
            if (args.Length > 0 && args[0] == "--diag-leak") return Tools.DiagLeak();

            var filter = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
            var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyPDF");
            var userDataExisted = Directory.Exists(userData);
            foreach (var (name, test) in AllChecks())
            {
                if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                Run(name, test);
            }
            Run("Nothing was written to the user's own data folder", () =>
                Check(userDataExisted || !Directory.Exists(userData), $"{userData} was created by the checks"));

            Console.WriteLine();
            Console.WriteLine(_failed == 0
                ? $"All {_passed} regression checks passed."
                : $"{_failed} of {_passed + _failed} regression checks failed:\n  " + string.Join("\n  ", Failures));
            return _failed == 0 ? 0 : 1;
        }
        finally
        {
            foreach (var window in App.Windows.OfType<Window>().ToArray()) window.Close();
            Pump(50);
            try { Directory.Delete(Temp, recursive: true); } catch { }
        }
    }

    private static IEnumerable<(string, Action)> AllChecks() =>
        EngineChecks.All().Concat(LinkChecks.All()).Concat(LayoutChecks.All()).Concat(ViewerChecks.All()).Concat(FeatureChecks.All())
            .Concat(NavigationChecks.All()).Concat(UpdateChecks.All()).Concat(ProcessChecks.All());

    private static void Run(string name, Action test)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            test();
            _passed++;
            Console.WriteLine($"PASS {name} ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            _failed++;
            Failures.Add(name);
            Console.WriteLine($"FAIL {name}: {ex}");
        }
        finally
        {
            // Every check leaves no window and no hook behind for the next one.
            foreach (var window in App.Windows.OfType<Window>().ToArray()) window.Close();
            MainWindow.ShellOpen = _ => throw new InvalidOperationException("A link tried to reach the shell.");
            MainWindow.NoticeSink = (title, message) => throw new InvalidOperationException($"Unexpected notice: {title}: {message}");
            MainWindow.ConfirmPrompt = target => throw new InvalidOperationException($"Unexpected confirmation for {target}");
            MainWindow.PasswordPrompt = (file, _) => throw new InvalidOperationException($"Unexpected password prompt for {file}");
            Pump(20);
            SettingsStore.Current.Settings.Recent.Clear();
            SettingsStore.Current.Settings.SinglePage = false;
            SettingsStore.Current.Settings.Grid = null;
            SettingsStore.Current.Settings.AutoUpdate = true;
            SettingsStore.Current.Settings.LastUpdateCheckUtc = null;
            SettingsStore.Current.Settings.ReopenAfterUpdate = null;
            Updater.Current?.Stop();
            Updater.Current = null;
            Updater.InstalledOverride = null;
            Updater.LaunchOverride = _ => throw new InvalidOperationException("An update tried to start an installer.");
            FileAvailability.AskOverride = null;
            FileAvailability.Reset();
            PdfDocument.BeforeOpenFile = null;
        }
    }

    // ===================================================================== assertions

    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void Near(double actual, double expected, double tolerance, string what)
    {
        if (!(Math.Abs(actual - expected) <= tolerance))
            throw new InvalidOperationException($"{what}: expected {expected} ± {tolerance}, got {actual}");
    }

    internal static void Equal<T>(T actual, T expected, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
            throw new InvalidOperationException($"{what}: expected {expected}, got {actual}");
    }

    internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }

    // ===================================================================== dispatcher

    /// <summary>Runs everything queued on the UI thread, then keeps doing so for <paramref name="ms"/>.</summary>
    internal static void Pump(int ms = 0)
    {
        var sw = Stopwatch.StartNew();
        do
        {
            DoEvents();
            if (ms > 0) Thread.Sleep(1);
        } while (sw.ElapsedMilliseconds < ms);
    }

    internal static void PumpUntil(Func<bool> condition, string what, int timeoutMs = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"Timed out after {timeoutMs} ms waiting for {what}.");
            DoEvents();
            Thread.Sleep(1);
        }
    }

    internal static T Await<T>(Task<T> task, int timeoutMs = 15000)
    {
        PumpUntil(() => task.IsCompleted, "a task", timeoutMs);
        return task.GetAwaiter().GetResult();
    }

    internal static void Await(Task task, int timeoutMs = 15000)
    {
        PumpUntil(() => task.IsCompleted, "a task", timeoutMs);
        task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Lets the dispatcher run everything, idle work included - WPF's binding engine purges the
    /// collection views it cached at idle priority, which <see cref="Pump"/> never reaches.
    /// </summary>
    internal static void PumpIdle()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    // ===================================================================== fixtures

    private static string FindFixtures()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EasyPDF.slnx"))) dir = dir.Parent;
        if (dir is null) throw new DirectoryNotFoundException("The repository root (EasyPDF.slnx) was not found.");
        return Path.Combine(dir.FullName, "tests", "fixtures");
    }

    internal static string Fixture(string name) => Path.Combine(Fixtures, name);

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    internal static PdfDocument Open(string name, string? password = null)
    {
        var path = Path.IsPathFullyQualified(name) ? name : Fixture(name);
        var result = Await(PdfDocument.OpenAsync(path, password));
        Check(result.Document is not null, $"{name} did not open: {result.Failure} {result.Detail}");
        return result.Document!;
    }

    private static string? _big;

    /// <summary>A 2000-page document, written once per run (too big to keep in the repository).</summary>
    internal static string BigPdf => _big ??= BigPdfWriter.Write(Path.Combine(Temp, "big.pdf"), 2000, needles: [499, 1499]);
}
