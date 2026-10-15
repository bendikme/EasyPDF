using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyPDF.Services;

/// <summary>Where a document was left: reopening it puts the reader back there.</summary>
public sealed class RecentFile
{
    public string Path { get; set; } = string.Empty;

    public DateTime OpenedUtc { get; set; }

    /// <summary>Zero-based page at the top of the view.</summary>
    public int Page { get; set; }

    /// <summary>How far down that page the view started, as a fraction of its height (negative in the gap above it).</summary>
    public double PageOffset { get; set; }

    /// <summary>"FitWidth", "FitPage" or "Custom".</summary>
    public string ZoomMode { get; set; } = "FitWidth";

    public double Zoom { get; set; } = 1;

    public int Rotation { get; set; }

    /// <summary>How its pages are shown: null for one continuous column, else a grid as "2x1" or "2x1c" (see PageGrid).</summary>
    public string? Grid { get; set; }

    /// <summary>Shown a fixed number of pages at a time; from before grids, when it meant one page.</summary>
    public bool SinglePage { get; set; }

    /// <summary>Pinned to the list: kept at its top, whatever is opened after it, until unpinned or removed.</summary>
    public bool Pinned { get; set; }
}

public sealed class WindowPlacement
{
    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public bool Maximized { get; set; }
}

public sealed class AppSettings
{
    public WindowPlacement? Window { get; set; }

    public bool SidebarOpen { get; set; } = true;

    public double SidebarWidth { get; set; } = 264;

    /// <summary>The sidebar's rows compact, for more of them in sight.</summary>
    public bool SidebarCompact { get; set; }

    /// <summary>Empty follows Windows; "en" or "nb".</summary>
    public string? Language { get; set; }

    /// <summary>How a document opened for the first time is shown: null for continuous, else a grid as "1x1" or "2x2" (see PageGrid).</summary>
    public string? Grid { get; set; }

    /// <summary>Documents opened for the first time are shown a fixed number of pages at a time; from before grids.</summary>
    public bool SinglePage { get; set; }

    public List<RecentFile> Recent { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;

/// <summary>
/// The app's settings, in %LOCALAPPDATA%\EasyPDF\settings.json. Read once at start-up; changed on
/// the UI thread; written shortly after a change, on a background thread, through a temporary
/// file so a crash mid-write never leaves a broken file behind.
/// </summary>
public sealed class SettingsStore
{
    /// <summary>Unpinned files kept; pinned ones are kept besides, up to <see cref="MaxPinned"/>.</summary>
    public const int MaxRecent = 30;

    public const int MaxPinned = 100;

    private readonly string _path;
    private readonly object _writeGate = new();
    private readonly object _fileGate = new();
    private Timer? _saveTimer;
    private string? _pending;

    private SettingsStore(string path, AppSettings settings)
    {
        _path = path;
        Settings = settings;
    }

    public static SettingsStore Current { get; private set; } = new(string.Empty, new AppSettings());

    public AppSettings Settings { get; }

    /// <summary>Raised on the UI thread when the recent list changes, so open windows can show it.</summary>
    public event EventHandler? RecentChanged;

    public static SettingsStore Load(string? directory = null)
    {
        var dir = directory ?? AppLog.DataDirectory;
        var path = Path.Combine(dir, "settings.json");
        var settings = new AppSettings();
        try
        {
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                settings = JsonSerializer.Deserialize(stream, SettingsJson.Default.AppSettings) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("The settings could not be read; starting from defaults.", ex);
            settings = new AppSettings();
        }

        Sanitize(settings);
        Current = new SettingsStore(path, settings);
        return Current;
    }

    private static void Sanitize(AppSettings s)
    {
        s.Recent ??= new List<RecentFile>();
        s.Recent.RemoveAll(r => r is null || string.IsNullOrWhiteSpace(r.Path));
        foreach (var r in s.Recent)
        {
            if (r.Page < 0) r.Page = 0;
            if (!double.IsFinite(r.PageOffset) || r.PageOffset is < -1 or > 2) r.PageOffset = 0;
            if (!double.IsFinite(r.Zoom) || r.Zoom is < 0.05 or > 64) r.Zoom = 1;
            if (r.ZoomMode is not ("FitWidth" or "FitPage" or "Custom")) r.ZoomMode = "FitWidth";
            r.Rotation = ((r.Rotation % 4) + 4) % 4;
            r.Grid = SanitizeGrid(r.Grid, r.SinglePage);
        }
        s.Grid = SanitizeGrid(s.Grid, s.SinglePage);
        Trim(s.Recent);
        if (!double.IsFinite(s.SidebarWidth) || s.SidebarWidth is < 180 or > 600) s.SidebarWidth = 264;
        if (s.Window is { } w && (!double.IsFinite(w.Left) || !double.IsFinite(w.Top) ||
                                  !double.IsFinite(w.Width) || !double.IsFinite(w.Height) || w.Width < 200 || w.Height < 200))
        {
            s.Window = null;
        }
    }

    /// <summary>A grid as remembered, or one page for a file remembered as "one page at a time" before there were grids.</summary>
    private static string? SanitizeGrid(string? grid, bool singlePage) =>
        EasyPDF.Viewer.PageGrid.Parse(grid)?.ToString() ?? (singlePage ? "1x1" : null);

    public RecentFile? FindRecent(string path) =>
        Settings.Recent.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Moves (or adds) a file to the top of the recent list with where it was left; a pinned file stays pinned.</summary>
    public void Remember(RecentFile entry)
    {
        var list = Settings.Recent;
        var existing = FindRecent(entry.Path);
        if (existing is not null) entry.Pinned = existing.Pinned;
        list.RemoveAll(r => string.Equals(r.Path, entry.Path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, entry);
        Trim(list);
        RecentChanged?.Invoke(this, EventArgs.Empty);
        Save();
    }

    /// <summary>The list, pinned files first: each part in the order they were last opened.</summary>
    public IReadOnlyList<RecentFile> Ordered() =>
        Settings.Recent.Where(r => r.Pinned).Concat(Settings.Recent.Where(r => !r.Pinned)).ToList();

    /// <summary>Takes a file off the list, pinned or not.</summary>
    public void Forget(string path)
    {
        if (Settings.Recent.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)) == 0) return;
        RecentChanged?.Invoke(this, EventArgs.Empty);
        Save();
    }

    /// <summary>Takes a file that could not be found off the list - unless it is pinned: a pinned file may come back (a drive plugged in, a share reconnected).</summary>
    public void ForgetUnlessPinned(string path)
    {
        if (FindRecent(path) is { Pinned: true }) return;
        Forget(path);
    }

    public void SetPinned(string path, bool pinned)
    {
        if (FindRecent(path) is not { } entry || entry.Pinned == pinned) return;
        entry.Pinned = pinned;
        Trim(Settings.Recent);
        RecentChanged?.Invoke(this, EventArgs.Empty);
        Save();
    }

    /// <summary>Empties the list of recent files; pinned files stay.</summary>
    public void ClearRecent()
    {
        if (Settings.Recent.RemoveAll(r => !r.Pinned) == 0) return;
        RecentChanged?.Invoke(this, EventArgs.Empty);
        Save();
    }

    /// <summary>At most <see cref="MaxRecent"/> unpinned files and <see cref="MaxPinned"/> pinned ones; the oldest go.</summary>
    private static void Trim(List<RecentFile> list)
    {
        int pinned = 0, recent = 0;
        list.RemoveAll(r => r.Pinned ? ++pinned > MaxPinned : ++recent > MaxRecent);
    }

    /// <summary>Schedules a write. Serialised now, on the caller's thread, so later changes cannot tear it.</summary>
    public void Save()
    {
        if (_path.Length == 0) return;

        string json;
        try
        {
            json = JsonSerializer.Serialize(Settings, SettingsJson.Default.AppSettings);
        }
        catch (Exception ex)
        {
            AppLog.Warn("The settings could not be serialised.", ex);
            return;
        }

        lock (_writeGate)
        {
            _pending = json;
            _saveTimer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(400, Timeout.Infinite);
        }
    }

    /// <summary>Writes any pending change now. Called at exit.</summary>
    public void Flush()
    {
        string? json;
        lock (_writeGate)
        {
            json = _pending;
            _pending = null;
        }
        if (json is null) return;

        // The timer and the exit path can both get here; one write at a time.
        lock (_fileGate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temp = _path + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                AppLog.Warn("The settings could not be saved.", ex);
            }
        }
    }
}
