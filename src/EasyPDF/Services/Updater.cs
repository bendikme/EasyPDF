using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace EasyPDF.Services;

/// <summary>Where updating stands.</summary>
public enum UpdateState
{
    /// <summary>Nothing newer is known.</summary>
    None,

    /// <summary>A newer release exists that cannot be installed from here: a portable or development copy, or a release without its checksums.</summary>
    Available,

    /// <summary>A newer release's installer is being downloaded.</summary>
    Downloading,

    /// <summary>A newer release's installer is downloaded and checked: it installs when EasyPDF closes, or at once with a restart.</summary>
    Ready,
}

/// <summary>A newer release: its version, its page, and the installer and checksums it carries.</summary>
public sealed record UpdateRelease(string Version, string PageUrl, string? InstallerName, Uri? InstallerUrl, long InstallerSize, Uri? ChecksumsUrl);

/// <summary>What a check found, for the one who asked.</summary>
public enum UpdateCheckOutcome
{
    UpToDate,
    Available,
    Ready,
    Failed,
}

/// <summary>
/// Keeps EasyPDF up to date from its GitHub releases. A day apart (the first time a little after
/// start-up, so it never slows it), it asks GitHub for the newest release. When that is newer,
/// the installer is downloaded in the background and checked against the release's
/// SHA256SUMS.txt; it then installs by itself when EasyPDF closes - silently, nothing to click -
/// or at once with a restart, which opens the same documents again afterwards.
///
/// Only an installed EasyPDF (the per-user MSI, in %LOCALAPPDATA%\Programs\EasyPDF) installs
/// updates: a portable or development copy is told about a newer release and pointed to its page,
/// rather than having a second copy installed beside it.
///
/// It writes nothing but its own downloads, in the "updates" folder of the settings folder - never
/// the settings, never anything else - and talks to nothing but GitHub, over https.
/// </summary>
public sealed partial class Updater
{
    public const string Repository = "bendikme/EasyPDF";

    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(30);
    private const long MaxInstallerBytes = 512L << 20;

    private readonly Dispatcher _dispatcher;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task? _loop;

    /// <param name="handler">Tests: a stand-in for GitHub. By default, the network.</param>
    public Updater(Dispatcher dispatcher, HttpMessageHandler? handler = null)
    {
        _dispatcher = dispatcher;
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        // GitHub answers 403 to a request without a User-Agent.
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"EasyPDF/{CurrentVersion}");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
    }

    /// <summary>The updater of this process, once started; null in a test host and before start-up is done.</summary>
    public static Updater? Current { get; internal set; }

    /// <summary>Raised on the UI thread whenever <see cref="State"/> or <see cref="Release"/> changes.</summary>
    public static event EventHandler? StateChanged;

    public UpdateState State { get; private set; }

    /// <summary>The newer release, while there is one.</summary>
    public UpdateRelease? Release { get; private set; }

    /// <summary>The checked installer of <see cref="Release"/>, once <see cref="State"/> is <see cref="UpdateState.Ready"/>.</summary>
    public string? InstallerPath { get; private set; }

    /// <summary>The running build's version, without "+build" metadata.</summary>
    public static string CurrentVersion => VersionOverride ?? RunningVersion;

    private static readonly string RunningVersion = ResolveCurrentVersion();

    /// <summary>Tools: the version to compare releases with, instead of this build's.</summary>
    internal static string? VersionOverride { get; set; }

    /// <summary>Tests: whether this copy counts as installed by the MSI. By default, whether it runs from the MSI's folder.</summary>
    internal static bool? InstalledOverride { get; set; }

    /// <summary>Tests: what would have been started, instead of starting it. Returns whether it "started".</summary>
    internal static Func<ProcessStartInfo, bool>? LaunchOverride { get; set; }

    /// <summary>Where the MSI installs EasyPDF; only a copy running from here installs updates.</summary>
    public static string InstallFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "EasyPDF");

    public static bool IsInstalled => InstalledOverride ?? IsUnder(AppContext.BaseDirectory, InstallFolder);

    /// <summary>The downloads, and nothing else: a folder of its own inside the settings folder.</summary>
    public static string UpdatesFolder => Path.Combine(AppLog.DataDirectory, "updates");

    // ===================================================================== the background loop

    /// <summary>
    /// Starts checking in the background: soon after start-up, then daily. EASYPDF_UPDATES=off
    /// stops it; =soon has the first check two seconds on rather than thirty. A debug build checks
    /// only when asked to (or told "soon"), so development runs are not offered releases.
    /// </summary>
    public void Start()
    {
        if (_loop is not null) return;
        var setting = Environment.GetEnvironmentVariable("EASYPDF_UPDATES");
        if (string.Equals(setting, "off", StringComparison.OrdinalIgnoreCase)) return;
        var soon = string.Equals(setting, "soon", StringComparison.OrdinalIgnoreCase);
#if DEBUG
        if (!soon) return;
#endif
        _loop = Task.Run(() => LoopAsync(soon ? TimeSpan.FromSeconds(2) : FirstCheckDelay, _stop.Token));
    }

    private async Task LoopAsync(TimeSpan firstDelay, CancellationToken stop)
    {
        try
        {
            await Task.Delay(firstDelay, stop).ConfigureAwait(false);
            while (!stop.IsCancellationRequested)
            {
                if (SettingsStore.Current.Settings.AutoUpdate && Due(SettingsStore.Current.Settings.LastUpdateCheckUtc, DateTime.UtcNow))
                    await CheckAsync(download: true, stop).ConfigureAwait(false);
                // A window left open for days still updates: the check comes round again.
                await Task.Delay(TimeSpan.FromHours(1), stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
        catch (Exception ex)
        {
            AppLog.Warn("The update loop stopped.", ex);
        }
    }

    /// <summary>Whether a day has gone by since the last check (or the clock went back).</summary>
    internal static bool Due(DateTime? lastUtc, DateTime nowUtc) =>
        lastUtc is not { } last || nowUtc - last >= CheckInterval || nowUtc < last;

    /// <summary>Stops checking and downloading. A download cut short leaves nothing behind.</summary>
    public void Stop()
    {
        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // ===================================================================== checking and downloading

    /// <summary>
    /// Asks GitHub for the newest release and, when it is newer and this copy is installed (and
    /// <paramref name="download"/>), downloads and checks its installer. Never throws.
    /// </summary>
    public async Task<UpdateCheckOutcome> CheckAsync(bool download, CancellationToken cancel = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, _stop.Token);
        var token = linked.Token;
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return UpdateCheckOutcome.Failed;
        }

        try
        {
            var release = await FetchLatestAsync(token).ConfigureAwait(false);
            await OnUi(() =>
            {
                SettingsStore.Current.Settings.LastUpdateCheckUtc = DateTime.UtcNow;
                SettingsStore.Current.Save();
            }).ConfigureAwait(false);

            if (release is null || CompareVersions(release.Version, CurrentVersion) <= 0)
            {
                // Nothing newer - perhaps the version installed meanwhile: what was offered goes.
                await SetAsync(UpdateState.None, null, null).ConfigureAwait(false);
                return UpdateCheckOutcome.UpToDate;
            }

            if (State == UpdateState.Ready && Release?.Version == release.Version && File.Exists(InstallerPath))
                return UpdateCheckOutcome.Ready;

            if (!IsInstalled || !download || release.InstallerUrl is null || release.ChecksumsUrl is null)
            {
                await SetAsync(UpdateState.Available, release, null).ConfigureAwait(false);
                return UpdateCheckOutcome.Available;
            }

            await SetAsync(UpdateState.Downloading, release, null).ConfigureAwait(false);
            var path = await DownloadAsync(release, token).ConfigureAwait(false);
            if (path is null)
            {
                await SetAsync(UpdateState.Available, release, null).ConfigureAwait(false);
                return UpdateCheckOutcome.Failed;
            }

            AppLog.Info($"EasyPDF {release.Version} is downloaded and checked: {path}");
            await SetAsync(UpdateState.Ready, release, path).ConfigureAwait(false);
            return UpdateCheckOutcome.Ready;
        }
        catch (OperationCanceledException)
        {
            return UpdateCheckOutcome.Failed;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AppLog.Warn("Checking for updates failed.", ex);
            if (State == UpdateState.Downloading) await SetAsync(UpdateState.Available, Release, null).ConfigureAwait(false);
            return UpdateCheckOutcome.Failed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<UpdateRelease?> FetchLatestAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ApiTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        // No release yet is not a failure.
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"GitHub answered {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength > 4 << 20) throw new InvalidDataException("The release description is too large.");

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        var json = await JsonSerializer.DeserializeAsync(stream, UpdateJson.Default.ReleaseJson, timeout.Token).ConfigureAwait(false);
        return json is null || json.Draft || json.Prerelease ? null : ToRelease(json);
    }

    /// <summary>The release as offered, or null when it carries no usable version. Only https GitHub addresses are taken from it.</summary>
    internal static UpdateRelease? ToRelease(ReleaseJson json)
    {
        var version = (json.TagName ?? string.Empty).Trim();
        if (version.StartsWith('v') || version.StartsWith('V')) version = version[1..];
        if (!VersionPattern().IsMatch(version)) return null;

        var page = Uri.TryCreate(json.HtmlUrl, UriKind.Absolute, out var pageUri) && IsGitHub(pageUri)
            ? pageUri.AbsoluteUri
            : $"https://github.com/{Repository}/releases";

        // The installer is the one named for this version - EasyPDF-1.2.3-x64.msi - and nothing else.
        string? installerName = null;
        Uri? installerUrl = null;
        long installerSize = 0;
        Uri? checksums = null;
        foreach (var asset in json.Assets ?? [])
        {
            if (asset?.Name is not { } name || !Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var url) || !IsGitHub(url)) continue;
            if (name.Equals($"EasyPDF-{version}-x64.msi", StringComparison.OrdinalIgnoreCase) && asset.Size is > 0 and <= MaxInstallerBytes)
            {
                installerName = name;
                installerUrl = url;
                installerSize = asset.Size;
            }
            else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
            {
                checksums = url;
            }
        }
        return new UpdateRelease(version, page, installerName, installerUrl, installerSize, checksums);
    }

    /// <summary>
    /// Downloads the installer into the updates folder and checks its size and SHA-256 against the
    /// release's checksums. Returns its path, or null (and nothing left behind) when anything is off.
    /// </summary>
    private async Task<string?> DownloadAsync(UpdateRelease release, CancellationToken token)
    {
        var expected = await ExpectedHashAsync(release, token).ConfigureAwait(false);
        if (expected is null)
        {
            AppLog.Warn($"EasyPDF {release.Version}: the release's checksums do not list its installer; it is not downloaded.");
            return null;
        }

        Directory.CreateDirectory(UpdatesFolder);
        PruneDownloads(keep: release.InstallerName!);
        var target = Path.Combine(UpdatesFolder, release.InstallerName!);
        var partial = target + ".part";

        // Downloaded before: kept if it still checks out.
        if (File.Exists(target) && await HashOfAsync(target, token).ConfigureAwait(false) == expected) return target;

        try
        {
            using var response = await _http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"GitHub answered {(int)response.StatusCode} for the installer.");
            if (response.Content.Headers.ContentLength is { } length && length != release.InstallerSize)
                throw new InvalidDataException($"The installer is {length} bytes, the release says {release.InstallerSize}.");

            using var sha = SHA256.Create();
            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > release.InstallerSize) throw new InvalidDataException("The installer is larger than the release says.");
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                }
                if (total != release.InstallerSize) throw new InvalidDataException("The installer is shorter than the release says.");
            }
            sha.TransformFinalBlock([], 0, 0);
            var actual = Convert.ToHexStringLower(sha.Hash!);
            if (actual != expected) throw new InvalidDataException($"The installer's SHA-256 is {actual}; the release lists {expected}.");

            File.Move(partial, target, overwrite: true);
            return target;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn($"Downloading EasyPDF {release.Version} failed.", ex);
            TryDelete(partial);
            return null;
        }
        catch (OperationCanceledException)
        {
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>The installer's SHA-256 as SHA256SUMS.txt lists it, lower case; null when it is not listed.</summary>
    private async Task<string?> ExpectedHashAsync(UpdateRelease release, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ApiTimeout);
        using var response = await _http.GetAsync(release.ChecksumsUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 64 << 10) return null;
        var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (text.Length > 64 << 10) return null;
        foreach (var line in text.Split('\n'))
        {
            var match = ChecksumLine().Match(line.Trim());
            if (match.Success && match.Groups[2].Value.Equals(release.InstallerName, StringComparison.OrdinalIgnoreCase))
                return match.Groups[1].Value.ToLowerInvariant();
        }
        return null;
    }

    private static async Task<string> HashOfAsync(string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token).ConfigureAwait(false));
    }

    /// <summary>Removes earlier downloads: installers and partial files of this folder only, never anything else.</summary>
    private static void PruneDownloads(string keep)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(UpdatesFolder, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                if (name.Equals(keep, StringComparison.OrdinalIgnoreCase)) continue;
                if (name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) TryDelete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Old update downloads could not be removed.", ex);
        }
    }

    // ===================================================================== installing

    /// <summary>
    /// EasyPDF is closing: a downloaded update installs now, silently - the next start is the new
    /// version. Only when updating automatically. Returns whether the installer was started.
    /// </summary>
    public bool InstallOnExit()
    {
        if (State != UpdateState.Ready || !SettingsStore.Current.Settings.AutoUpdate) return false;
        return Launch(silent: true);
    }

    /// <summary>
    /// Starts the installer with its progress window; it starts the new EasyPDF when it is done.
    /// The caller closes EasyPDF straight after. Returns whether the installer was started.
    /// </summary>
    public bool InstallNow()
    {
        if (State != UpdateState.Ready) return false;
        var started = Launch(silent: false);
        if (started) Stop();
        return started;
    }

    private bool Launch(bool silent)
    {
        var path = InstallerPath;
        if (path is null || !IsUnder(path, UpdatesFolder) || !File.Exists(path)) return false;

        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"))
        {
            UseShellExecute = false,
            WorkingDirectory = UpdatesFolder,
        };
        // The package and, when silent, no user interface and no restart: no property here can
        // reach the settings folder.
        info.ArgumentList.Add("/i");
        info.ArgumentList.Add(path);
        if (silent)
        {
            info.ArgumentList.Add("/qn");
            info.ArgumentList.Add("/norestart");
        }

        try
        {
            if (LaunchOverride is { } stand_in) return stand_in(info);
            using var process = Process.Start(info);
            AppLog.Info($"The update to EasyPDF {Release?.Version} is installing{(silent ? " silently" : string.Empty)}.");
            return process is not null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("The update installer could not be started.", ex);
            return false;
        }
    }

    // ===================================================================== state

    private Task SetAsync(UpdateState state, UpdateRelease? release, string? installer) => OnUi(() =>
    {
        if (State == state && Equals(Release, release) && InstallerPath == installer) return;
        State = state;
        Release = release;
        InstallerPath = installer;
        StateChanged?.Invoke(this, EventArgs.Empty);
    });

    private Task OnUi(Action action) =>
        _dispatcher.CheckAccess() ? Run(action) : _dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task;

    private static Task Run(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    // ===================================================================== helpers

    /// <summary>"1.2.3" against "1.2.10": by number, missing parts count as 0; anything unreadable is lowest.</summary>
    public static int CompareVersions(string a, string b)
    {
        static int[]? Parse(string text)
        {
            var plain = text.Trim().TrimStart('v', 'V').Split('+', '-')[0];
            var parts = plain.Split('.');
            if (parts.Length is < 1 or > 4) return null;
            var numbers = new int[4];
            for (var i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return null;
            }
            return numbers;
        }

        var x = Parse(a);
        var y = Parse(b);
        if (x is null) return y is null ? 0 : -1;
        if (y is null) return 1;
        for (var i = 0; i < 4; i++)
        {
            if (x[i] != y[i]) return x[i].CompareTo(y[i]);
        }
        return 0;
    }

    private static bool IsGitHub(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static bool IsUnder(string path, string folder)
    {
        try
        {
            var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"{path} could not be removed.", ex);
        }
    }

    private static string ResolveCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(Updater).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational)) return informational.Split('+')[0].Trim();
        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+(\.\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    /// <summary>"&lt;64 hex&gt;  name", as sha256sum writes it (a '*' before the name marks binary mode).</summary>
    [GeneratedRegex(@"^([0-9A-Fa-f]{64})\s+\*?(\S.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ChecksumLine();
}

// ===================================================================== GitHub's answer

public sealed class ReleaseJson
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("assets")]
    public List<ReleaseAssetJson?>? Assets { get; set; }
}

public sealed class ReleaseAssetJson
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ReleaseJson))]
internal sealed partial class UpdateJson : JsonSerializerContext;
