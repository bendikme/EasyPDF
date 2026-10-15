using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using static Program;

/// <summary>
/// The real executable, launched as Explorer would launch it. It runs in a group of its own and
/// keeps its settings in a temporary folder, so it never meets the user's own EasyPDF.
/// </summary>
internal static class ProcessChecks
{
    public static IEnumerable<(string, Action)> All() =>
    [
        ("A second launch hands its file to the running EasyPDF and exits", HandOverEndToEnd),
    ];

    private static string Exe => Path.Combine(AppContext.BaseDirectory, "EasyPDF.exe");

    private static Process Launch(string group, string data, params string[] args)
    {
        var info = new ProcessStartInfo(Exe) { UseShellExecute = false };
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment["EASYPDF_INSTANCE"] = group;
        info.Environment["EASYPDF_DATA"] = data;
        // The checks never reach the network.
        info.Environment["EASYPDF_UPDATES"] = "off";
        return Process.Start(info) ?? throw new InvalidOperationException("EasyPDF did not start.");
    }

    private static void HandOverEndToEnd()
    {
        Check(File.Exists(Exe), $"{Exe} is not built");
        var group = Guid.NewGuid().ToString("N")[..12];
        var data = Path.Combine(Temp, "e2e-data");
        Directory.CreateDirectory(data);

        using var first = Launch(group, data, Fixture("links.pdf"));
        try
        {
            WaitFor(() => Titles(first.Id).Contains("links.pdf - EasyPDF"), "the first window", 20000);

            // A second launch with another file: it hands the file over and is gone quickly.
            var sw = Stopwatch.StartNew();
            using (var second = Launch(group, data, Fixture("other.pdf")))
            {
                Check(second.WaitForExit(10000), "the second launch did not exit");
                Equal(second.ExitCode, 0, "the second launch's exit code");
            }
            Console.WriteLine($"     a second launch handed over and exited in {sw.ElapsedMilliseconds} ms");
            WaitFor(() => Titles(first.Id).Contains("other.pdf - EasyPDF"), "other.pdf in the running EasyPDF", 10000);

            // The same file again: no second window for it.
            using (var third = Launch(group, data, Fixture("links.pdf")))
            {
                Check(third.WaitForExit(10000), "the third launch did not exit");
            }
            Thread.Sleep(500);
            Equal(Titles(first.Id).Count(t => t == "links.pdf - EasyPDF"), 1, "windows for links.pdf");

            // No file: a new empty window.
            using (var fourth = Launch(group, data))
            {
                Check(fourth.WaitForExit(10000), "the fourth launch did not exit");
            }
            WaitFor(() => Titles(first.Id).Contains("EasyPDF"), "an empty window", 10000);

            // Closing every window ends the process cleanly, and what was read is remembered.
            foreach (var hwnd in Windows(first.Id)) PostMessage(hwnd, 0x0010 /* WM_CLOSE */, 0, 0);
            Check(first.WaitForExit(10000), "EasyPDF did not exit when its windows closed");
            Equal(first.ExitCode, 0, "exit code");
            var settings = File.ReadAllText(Path.Combine(data, "settings.json"));
            Check(settings.Contains("links.pdf", StringComparison.Ordinal) && settings.Contains("other.pdf", StringComparison.Ordinal),
                "both files are in the recent list");
        }
        finally
        {
            if (!first.HasExited) first.Kill();
        }
    }

    private static void WaitFor(Func<bool> condition, string what, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"Timed out waiting for {what}.");
            Thread.Sleep(50);
        }
    }

    private static List<nint> Windows(int processId)
    {
        var found = new List<nint>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == processId && IsWindowVisible(hwnd)) found.Add(hwnd);
            return true;
        }, 0);
        return found;
    }

    private static List<string> Titles(int processId) =>
        Windows(processId).Select(hwnd =>
        {
            var sb = new StringBuilder(512);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }).ToList();

    private delegate bool EnumWindowsProc(nint hwnd, nint param);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint param);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out int processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
}
