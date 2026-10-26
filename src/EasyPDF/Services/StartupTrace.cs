using System.Diagnostics;
using System.IO;

namespace EasyPDF.Services;

/// <summary>
/// Measures start-up for performance work: with EASYPDF_TRACE set to a file, the time from the
/// process starting to milestones - the window shown, the document open, the first screen fully
/// rendered - is appended to that file. With EASYPDF_TRACE_EXIT=1 as well, the app closes once
/// the first screen is rendered, so a script can time many cold starts. Off unless asked for.
/// </summary>
public static class StartupTrace
{
    private static readonly string? Path = Environment.GetEnvironmentVariable("EASYPDF_TRACE");
    private static readonly long Origin = Stopwatch.GetTimestamp();
    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static DateTime _processStart;
    private static bool _firstScreenDone;

    public static bool Enabled => !string.IsNullOrEmpty(Path);

    public static bool ExitAfterFirstScreen => Enabled && Environment.GetEnvironmentVariable("EASYPDF_TRACE_EXIT") == "1";

    /// <summary>
    /// Records a milestone: its name, the milliseconds since the process started and since Main,
    /// then how many methods the JIT has compiled so far and the milliseconds it spent on them
    /// (ReadyToRun code is not counted: a high count means the precompiled code is not used), and
    /// the assemblies loaded. Any thread.
    /// </summary>
    public static void Mark(string milestone)
    {
        if (!Enabled) return;
        try
        {
            var sinceMain = Stopwatch.GetElapsedTime(Origin).TotalMilliseconds;
            var jitted = System.Runtime.JitInfo.GetCompiledMethodCount();
            var jitTime = System.Runtime.JitInfo.GetCompilationTime().TotalMilliseconds;
            var assemblies = AppDomain.CurrentDomain.GetAssemblies().Length;
            lock (Gate)
            {
                // Asked once: the process start time costs a millisecond to look up, and so does
                // opening the file. The writer flushes every line, so a crash loses none.
                if (_processStart == default) _processStart = Process.GetCurrentProcess().StartTime;
                _writer ??= new StreamWriter(Path!, append: true) { AutoFlush = true };
                var sinceStart = (DateTime.Now - _processStart).TotalMilliseconds;
                _writer.Write($"{milestone}\t{sinceStart:0.0}\t{sinceMain:0.0}\tjit {jitted} {jitTime:0}ms\tasm {assemblies}{Environment.NewLine}");
            }
        }
        catch
        {
            // Measuring must never break the app.
        }
    }

    /// <summary>The first screen of the first document is rendered; true the first time only.</summary>
    public static bool FirstScreen()
    {
        if (!Enabled || _firstScreenDone) return false;
        _firstScreenDone = true;
        Mark("first-screen");
        return true;
    }
}
