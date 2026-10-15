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
    private static bool _firstScreenDone;

    public static bool Enabled => !string.IsNullOrEmpty(Path);

    public static bool ExitAfterFirstScreen => Enabled && Environment.GetEnvironmentVariable("EASYPDF_TRACE_EXIT") == "1";

    /// <summary>Records a milestone, timed from the process start.</summary>
    public static void Mark(string milestone)
    {
        if (!Enabled) return;
        try
        {
            var sinceStart = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            var sinceMain = Stopwatch.GetElapsedTime(Origin).TotalMilliseconds;
            File.AppendAllText(Path!, $"{milestone}\t{sinceStart:0.0}\t{sinceMain:0.0}{Environment.NewLine}");
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
