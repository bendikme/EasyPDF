using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Controls;
using System.Windows.Threading;
using EasyPDF.Services;
using EasyPDF.Views;
using static Program;

/// <summary>Updating from GitHub releases - against a stand-in for GitHub: the checks never reach the network.</summary>
internal static class UpdateChecks
{
    public static IEnumerable<(string, Action)> All() =>
    [
        ("Versions compare by number, and a check is due once a day", VersionsAndSchedule),
        ("An installed EasyPDF downloads a newer release, checks it, offers it, and installs it on closing", DownloadsChecksAndInstallsOnExit),
        ("Restarting to update installs at once and opens the same documents again", RestartToUpdate),
        ("A download that does not check out is thrown away, and nothing installs", BadDownloadsAreRefused),
        ("A portable copy is pointed to the release; up to date, nothing is offered; only GitHub is trusted", PortableUpToDateAndUntrusted),
    ];

    /// <summary>GitHub, as far as the updater sees it: the latest release, its installer and its checksums.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        public string? Version = "9.9.9";
        public byte[] Installer = RandomNumberGenerator.GetBytes(300_000);
        public string? Sums;
        public long? DeclaredSize;
        public string InstallerHost = "github.com";
        public bool NoRelease;
        public readonly List<Uri> Requests = new();

        public string InstallerName => $"EasyPDF-{Version}-x64.msi";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            lock (Requests) Requests.Add(uri);
            var download = $"https://{InstallerHost}/bendikme/EasyPDF/releases/download/v{Version}/";
            HttpResponseMessage response;
            if (uri.AbsoluteUri == "https://api.github.com/repos/bendikme/EasyPDF/releases/latest")
            {
                response = NoRelease
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : Json($$"""
                        {
                          "tag_name": "v{{Version}}",
                          "html_url": "https://github.com/bendikme/EasyPDF/releases/tag/v{{Version}}",
                          "draft": false, "prerelease": false,
                          "assets": [
                            { "name": "{{InstallerName}}", "browser_download_url": "{{download}}{{InstallerName}}", "size": {{DeclaredSize ?? Installer.Length}} },
                            { "name": "EasyPDF-{{Version}}-win-x64.zip", "browser_download_url": "{{download}}EasyPDF-{{Version}}-win-x64.zip", "size": 1000 },
                            { "name": "SHA256SUMS.txt", "browser_download_url": "https://github.com/bendikme/EasyPDF/releases/download/v{{Version}}/SHA256SUMS.txt", "size": 200 }
                          ]
                        }
                        """);
            }
            else if (uri.AbsoluteUri.EndsWith("/SHA256SUMS.txt", StringComparison.Ordinal))
            {
                var sums = Sums ?? $"{Convert.ToHexStringLower(SHA256.HashData(Installer))}  {InstallerName}\nffff  EasyPDF-{Version}-win-x64.zip\n";
                response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sums) };
            }
            else if (uri.AbsoluteUri == download + InstallerName)
            {
                response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Installer) };
            }
            else
            {
                response = new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            return Task.FromResult(response);
        }

        private static HttpResponseMessage Json(string text) =>
            new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }

    private static readonly List<ProcessStartInfo> Launched = new();

    private static Updater Make(FakeGitHub github, bool installed = true)
    {
        Updater.InstalledOverride = installed;
        Launched.Clear();
        Updater.LaunchOverride = info =>
        {
            Launched.Add(info);
            return true;
        };
        var updater = new Updater(Dispatcher.CurrentDispatcher, github);
        Updater.Current = updater;
        return updater;
    }

    private static string Args(ProcessStartInfo info) => string.Join(" ", info.ArgumentList);

    /// <summary>For the screenshots: an updater with 9.9.9 downloaded and ready, from the stand-in.</summary>
    internal static void MakeReady()
    {
        var updater = Make(new FakeGitHub());
        Await(updater.CheckAsync(download: true), 20000);
    }

    private static void VersionsAndSchedule()
    {
        foreach (var (a, b, sign) in new[]
                 {
                     ("1.0.1", "1.0.0", 1), ("1.0.0", "1.0.0", 0), ("1.0.10", "1.0.9", 1), ("v2.0", "1.9.9", 1),
                     ("1.0.0.1", "1.0.0", 1), ("1.0", "1.0.0", 0), ("1.0.0+abc", "1.0.0", 0), ("junk", "0.0.1", -1),
                 })
        {
            Equal(Math.Sign(Updater.CompareVersions(a, b)), sign, $"{a} against {b}");
        }

        var now = DateTime.UtcNow;
        Check(Updater.Due(null, now), "never checked: due");
        Check(!Updater.Due(now.AddHours(-3), now), "three hours ago: not yet");
        Check(Updater.Due(now.AddHours(-25), now), "a day ago: due");
        Check(Updater.Due(now.AddHours(5), now), "the clock went back: due");
        Check(SettingsStore.Current.Settings.AutoUpdate, "updating automatically is on by default");
    }

    private static void DownloadsChecksAndInstallsOnExit()
    {
        var github = new FakeGitHub();
        var updater = Make(github);
        var window = App.OpenEmptyWindow(null);
        Check(Await(window.OpenAsync(Fixture("links.pdf"))), "a document");
        Check(!window.UpdateOffered, "nothing offered before a check");

        Equal(Await(updater.CheckAsync(download: true), 20000), UpdateCheckOutcome.Ready, "the check");
        Equal(updater.State, UpdateState.Ready, "ready");
        Equal(updater.Release!.Version, "9.9.9", "the version");
        var path = updater.InstallerPath!;
        Check(path.StartsWith(Updater.UpdatesFolder, StringComparison.OrdinalIgnoreCase), $"downloaded into the updates folder: {path}");
        Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(github.Installer), "every byte");
        Check(!Directory.EnumerateFiles(Updater.UpdatesFolder, "*.part").Any(), "no partial file left");
        Check(SettingsStore.Current.Settings.LastUpdateCheckUtc is { } at && DateTime.UtcNow - at < TimeSpan.FromMinutes(1), "the check is remembered");
        Check(github.Requests.All(u => u.Host is "api.github.com" or "github.com"), "only GitHub was asked");

        // The header offers it, with a restart; nothing installs meanwhile.
        PumpUntil(() => window.UpdateOffered, "the header offers the update");
        Check(((string)window.UpdateButton.ToolTip).Contains("9.9.9"), $"which version: {window.UpdateButton.ToolTip}");
        var menu = window.BuildUpdateMenu();
        Check(((MenuItem)menu.Items[0]!).Header is string restart && restart.Contains("9.9.9"), "restart and update, first");
        Equal(Launched.Count, 0, "nothing installs by itself while EasyPDF is open");

        // Asked again: the same download, not another.
        var before = github.Requests.Count(u => u.AbsoluteUri.EndsWith(".msi", StringComparison.Ordinal));
        Equal(Await(updater.CheckAsync(download: true), 20000), UpdateCheckOutcome.Ready, "checked again");
        Equal(github.Requests.Count(u => u.AbsoluteUri.EndsWith(".msi", StringComparison.Ordinal)), before, "not downloaded twice");

        // Closing: it installs, silently, nothing to click and nothing restarting.
        Check(updater.InstallOnExit(), "it installs on closing");
        Equal(Launched.Count, 1, "one installer");
        Check(Launched[0].FileName.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase), $"through Windows Installer: {Launched[0].FileName}");
        Equal(Args(Launched[0]), $"/i {path} /qn /norestart", "the package, silently, no restart");

        // Switched off, a ready update does not install on closing.
        var second = Make(new FakeGitHub());
        Await(second.CheckAsync(download: true), 20000);
        SettingsStore.Current.Settings.AutoUpdate = false;
        Check(!second.InstallOnExit(), "not updating automatically: nothing installs on closing");
        Equal(Launched.Count, 0, "nothing started");
        SettingsStore.Current.Settings.AutoUpdate = true;

        // The menu says so too: "Check for updates" and the switch, in the More menu.
        var more = window.BuildMoreMenu();
        Check(more.Items.OfType<MenuItem>().Any(i => Equals(i.Header, EasyPDF.Resources.Strings.Update_Check)), "Check for updates in the More menu");
        Check(more.Items.OfType<MenuItem>().Any(i => Equals(i.Header, EasyPDF.Resources.Strings.Update_Automatic) && i.IsChecked), "and updating automatically, on");
    }

    private static void RestartToUpdate()
    {
        var updater = Make(new FakeGitHub());
        var path = Fixture("links.pdf");
        var window = App.OpenEmptyWindow(null);
        Check(Await(window.OpenAsync(path)), "a document");
        var other = App.OpenEmptyWindow(null);
        Check(Await(other.OpenAsync(Fixture("other.pdf"))), "and another");
        Equal(Await(updater.CheckAsync(download: true), 20000), UpdateCheckOutcome.Ready, "ready");

        Check(App.RestartToUpdate(), "restarted to update");
        Equal(Launched.Count, 1, "the installer started");
        Equal(Args(Launched[0]), $"/i {updater.InstallerPath}", "with its progress window, which starts EasyPDF again when done");
        Pump(50);
        Check(!App.DocumentWindows.Any(), "every window closed");
        var noted = SettingsStore.Current.Settings.ReopenAfterUpdate;
        Equal(string.Join("|", noted ?? []), $"{path}|{Fixture("other.pdf")}", "the documents to open again");

        // The new version starts with nothing on its command line: the same documents open.
        Check(App.ReopenAfterUpdate(), "the new version opens them");
        PumpUntil(() => App.DocumentWindows.Count(w => w.HasDocument) == 2, "both documents open again");
        Check(SettingsStore.Current.Settings.ReopenAfterUpdate is null, "once");
        Check(!App.ReopenAfterUpdate(), "and not again");
    }

    private static void BadDownloadsAreRefused()
    {
        foreach (var (what, spoil) in new (string, Action<FakeGitHub>)[]
                 {
                     ("a wrong checksum", g => g.Sums = $"{new string('0', 64)}  {g.InstallerName}\n"),
                     ("checksums that do not list it", g => g.Sums = "abc  something-else.msi\n"),
                     ("a size that does not match", g => g.DeclaredSize = g.Installer.Length + 10),
                 })
        {
            var github = new FakeGitHub();
            spoil(github);
            if (Directory.Exists(Updater.UpdatesFolder)) Directory.Delete(Updater.UpdatesFolder, recursive: true);
            var updater = Make(github);
            var outcome = Await(updater.CheckAsync(download: true), 20000);
            Equal(outcome, UpdateCheckOutcome.Failed, $"{what}: the check");
            Check(updater.State != UpdateState.Ready && updater.InstallerPath is null, $"{what}: not ready");
            Check(!Directory.Exists(Updater.UpdatesFolder) || !Directory.EnumerateFiles(Updater.UpdatesFolder).Any(), $"{what}: nothing left in the updates folder");
            Check(!updater.InstallOnExit(), $"{what}: nothing installs");
            Equal(Launched.Count, 0, $"{what}: nothing started");
        }
    }

    private static void PortableUpToDateAndUntrusted()
    {
        // A portable copy: told, pointed to the page, never installing.
        var github = new FakeGitHub();
        var updater = Make(github, installed: false);
        var window = App.OpenEmptyWindow(null);
        Equal(Await(updater.CheckAsync(download: true), 20000), UpdateCheckOutcome.Available, "a portable copy: available");
        Check(!github.Requests.Any(u => u.AbsoluteUri.EndsWith(".msi", StringComparison.Ordinal)), "nothing downloaded");
        PumpUntil(() => window.UpdateOffered, "offered in the header");
        var opened = new List<string>();
        MainWindow.ShellOpen = target =>
        {
            opened.Add(target);
            return true;
        };
        ((MenuItem)window.BuildUpdateMenu().Items[0]!).RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
        Equal(string.Join("|", opened), "https://github.com/bendikme/EasyPDF/releases/tag/v9.9.9", "the release page");
        Check(!updater.InstallOnExit(), "and nothing installs");

        // Up to date - or no release at all: nothing offered, and what was offered goes.
        github.Version = Updater.CurrentVersion;
        Equal(Await(updater.CheckAsync(download: true), 20000), UpdateCheckOutcome.UpToDate, "the same version: up to date");
        PumpUntil(() => !window.UpdateOffered, "nothing offered");
        github.Version = "0.0.1";
        Equal(Await(updater.CheckAsync(download: true), 20000), UpdateCheckOutcome.UpToDate, "an older one: up to date");
        github.NoRelease = true;
        Equal(Await(updater.CheckAsync(download: true), 20000), UpdateCheckOutcome.UpToDate, "no release yet: up to date");

        // An installer that is not on GitHub is not taken.
        var elsewhere = new FakeGitHub { InstallerHost = "downloads.example.com" };
        var trusting = Make(elsewhere);
        Equal(Await(trusting.CheckAsync(download: true), 20000), UpdateCheckOutcome.Available, "an installer elsewhere: only pointed to");
        Check(elsewhere.Requests.All(u => u.Host is "api.github.com" or "github.com"), "nothing fetched from elsewhere");
        Check(!trusting.InstallOnExit(), "and nothing installs");

        // Asked by hand in the window: it says what it found.
        github.NoRelease = false;
        github.Version = "9.9.9";
        Make(github);
        Await(window.CheckForUpdatesAsync(), 20000);
        PumpUntil(() => window.StatusText.Text.Contains("9.9.9"), "the window says the update is ready");
    }
}
