using System.IO;

namespace EasyPDF.Services;

/// <summary>Whether a file can be opened, as far as is known.</summary>
public enum Availability
{
    /// <summary>Not asked yet, or still being asked.</summary>
    Unknown,

    Present,

    /// <summary>Its drive or share answers, and the file is not there.</summary>
    Missing,

    /// <summary>Its drive or share does not answer (unplugged, offline, slow): the file may come back.</summary>
    Unavailable,
}

/// <summary>
/// Whether files are there, found out without anyone ever waiting for it: a network share that
/// does not answer can hold a single file call for half a minute, and a sleeping disk for seconds.
/// Each drive or share is asked on a background thread of its own, one file at a time, so one dead
/// server holds up only its own files; one that has not answered within <see cref="Patience"/>
/// counts as unavailable until it does. Answers are kept and asked again only once they are old.
/// </summary>
public static class FileAvailability
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(2);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, (Availability State, DateTime CheckedUtc)> Known = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Queue<string>> Waiting = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (string Path, DateTime SinceUtc)> Asking = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised, on a background thread, whenever an answer comes in.</summary>
    public static event Action? Changed;

    /// <summary>What is known of a file now; never waits.</summary>
    public static Availability Get(string path)
    {
        lock (Gate)
        {
            if (Known.TryGetValue(path, out var known)) return known.State;

            // Its drive or share has been silent too long: unavailable, until it answers.
            var root = RootOf(path);
            if (Asking.TryGetValue(root, out var asking) && DateTime.UtcNow - asking.SinceUtc > Patience) return Availability.Unavailable;
            return Availability.Unknown;
        }
    }

    /// <summary>Asks about files not asked about lately (all of them, with <paramref name="again"/>). Returns at once.</summary>
    public static void Check(IEnumerable<string> paths, bool again = false)
    {
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (!again && Known.TryGetValue(path, out var known) && now - known.CheckedUtc < Stale) continue;

                var root = RootOf(path);
                if (!Waiting.TryGetValue(root, out var queue)) Waiting[root] = queue = new Queue<string>();
                if (queue.Contains(path, StringComparer.OrdinalIgnoreCase) || (Asking.TryGetValue(root, out var asking) && string.Equals(asking.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
                queue.Enqueue(path);

                if (Asking.ContainsKey(root)) continue;
                Asking[root] = (string.Empty, now);
                new Thread(() => AskAll(root)) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "EasyPDF.Availability" }.Start();
            }
        }
    }

    /// <summary>What opening a file found out: there, or not there.</summary>
    public static void Report(string path, Availability state)
    {
        lock (Gate) Known[path] = (state, DateTime.UtcNow);
        Changed?.Invoke();
    }

    /// <summary>Forgets every answer. For tests.</summary>
    internal static void Reset()
    {
        lock (Gate) Known.Clear();
    }

    private static void AskAll(string root)
    {
        while (true)
        {
            string path;
            lock (Gate)
            {
                if (!Waiting.TryGetValue(root, out var queue) || !queue.TryDequeue(out path!))
                {
                    Waiting.Remove(root);
                    Asking.Remove(root);
                    return;
                }
                Asking[root] = (path, DateTime.UtcNow);
            }

            var state = Ask(path);
            lock (Gate) Known[path] = (state, DateTime.UtcNow);
            try
            {
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Telling of a file's availability failed.", ex);
            }
        }
    }

    /// <summary>Stands in for the file system. Tests set it to play a share that does not answer.</summary>
    internal static Func<string, Availability>? AskOverride { get; set; }

    private static Availability Ask(string path)
    {
        if (AskOverride is { } ask) return ask(path);
        try
        {
            if (File.Exists(path)) return Availability.Present;

            // The file is not there - or its drive or share is not, and then it may come back.
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && Directory.Exists(root) ? Availability.Missing : Availability.Unavailable;
        }
        catch (Exception)
        {
            return Availability.Unavailable;
        }
    }

    /// <summary>The drive ("C:\") or share ("\\server\share") a path is on.</summary>
    private static string RootOf(string path)
    {
        try
        {
            return Path.GetPathRoot(path) is { Length: > 0 } root ? root : path;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }
}
