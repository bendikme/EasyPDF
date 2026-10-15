using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using EasyPDF.Pdf;
using EasyPDF.Resources;
using EasyPDF.Services;
using EasyPDF.Viewer;

namespace EasyPDF.Views;

public partial class MainWindow
{
    /// <summary>Opens a URI or file for the shell. Tests replace it to see what would have been opened.</summary>
    internal static Func<string, bool> ShellOpen { get; set; } = DefaultShellOpen;

    private static bool DefaultShellOpen(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Opening {target} failed.", ex);
            return false;
        }
    }

    /// <summary>
    /// Hands a target to the shell. The shell can take long to start opening a file on a network
    /// share, so it is asked off the UI thread; a failure is told when it comes.
    /// </summary>
    private void OpenOutside(string target)
    {
        var open = ShellOpen;
        if (open != DefaultShellOpen)
        {
            // A test's stand-in answers at once.
            if (!open(target)) ShowNotice(Strings.Link_Failed_Title, UiLanguage.Format(Strings.Link_Failed, target));
            return;
        }

        _ = Task.Run(() => open(target)).ContinueWith(task =>
        {
            if (!_closed && !(task.IsCompletedSuccessfully && task.Result))
                ShowNotice(Strings.Link_Failed_Title, UiLanguage.Format(Strings.Link_Failed, target));
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnLinkActivated(object? sender, LinkEventArgs e)
    {
        if (e.Target is { } target) Follow(target);
    }

    /// <summary>Follows a link that leaves the document. In-document links are the view's.</summary>
    internal void Follow(LinkTarget target)
    {
        var documentPath = _document?.FilePath;
        switch (target)
        {
            case GoToTarget go:
                _ = View.NavigateAsync(go.Destination, flash: true);
                return;

            case UriTarget uri:
                Act(LinkPolicy.ForUri(uri.Uri, documentPath), null);
                return;

            case LaunchTarget launch:
                Act(LinkPolicy.ForFile(launch.Path, documentPath), null);
                return;

            case RemoteGoToTarget remote:
                Act(LinkPolicy.ForFile(remote.Path, documentPath), remote);
                return;

            case NamedActionTarget named:
                // The view runs those that move through the document; these are the window's. They
                // run once the click is over, so no dialog opens under a captured mouse.
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                {
                    if (_closed || !HasDocument) return;
                    switch (named.Action)
                    {
                        case NamedAction.Find: FocusSearch(); break;
                        case NamedAction.GoToPage: FocusPageBox(); break;
                        case NamedAction.Print: Print(); break;
                        case NamedAction.FullScreen: ToggleFullScreen(); break;
                        default: View.Activate(named); break;
                    }
                });
                return;
        }
    }

    private void Act(LinkDecision decision, RemoteGoToTarget? remote)
    {
        switch (decision.Action)
        {
            case LinkAction.OpenWeb:
                OpenOutside(decision.Target);
                return;

            case LinkAction.OpenPdf:
                OpenLinkedPdf(decision.Target, remote, decision.Fragment);
                return;

            case LinkAction.AskThenOpen:
                if (!Confirm(Strings.Link_Confirm_Title, Strings.Link_Confirm_Message, decision.Target, Strings.Link_Confirm_Open)) return;
                if (decision.IsPdf) OpenLinkedPdf(decision.Target, remote, decision.Fragment);
                else OpenOutside(decision.Target);
                return;

            case LinkAction.Missing:
                ShowNotice(Strings.Link_Failed_Title, UiLanguage.Format(Strings.Link_Missing, decision.Target));
                return;

            default:
                AppLog.Warn($"A link to {decision.Target} was refused.");
                ShowNotice(Strings.Link_Refused_Title, UiLanguage.Format(Strings.Link_Refused, decision.Target));
                return;
        }
    }

    /// <summary>
    /// Another PDF opens in its own window, so this one keeps its place (and Back still works).
    /// A place inside it - the link's destination, or a "#page=3" / "#nameddest=intro" after its
    /// name - is found once it is open: the place can be a name that only the other file knows.
    /// </summary>
    private void OpenLinkedPdf(string path, RemoteGoToTarget? remote, string? fragment = null)
    {
        var sourcePath = _document?.FilePath;

        // A link to this very file is a jump inside it.
        if (_document is { } current && SamePath(current.FilePath, path))
        {
            _ = JumpInto(this, current);
            return;
        }

        _app.Open(path, null, opened =>
        {
            if (opened.Document is { } other) _ = JumpInto(opened, other);
        });

        async Task JumpInto(MainWindow window, PdfDocument other)
        {
            try
            {
                ResolvedDestination? place = null;
                if (remote?.Destination is { } given)
                {
                    // Read whole with the link (a button's): a page of the other file, counted from 0.
                    place = await other.ResolveAsync(given with { PageIndex = Math.Clamp(given.PageIndex, 0, other.PageCount - 1) }).ConfigureAwait(true);
                }
                else if (remote?.DestinationName is { } remoteName)
                {
                    place = await other.ResolveNamedAsync(remoteName).ConfigureAwait(true);
                }
                else if (remote is not null)
                {
                    // The destination is read from the link's own action. If this window has since
                    // reloaded its file, the reloaded document holds the same link.
                    if (_document is not { } from || !SamePath(from.FilePath, sourcePath)) return;
                    place = await from.ResolveRemoteAsync(remote, other).ConfigureAwait(true);
                }
                else if (ParseFragment(fragment) is var (page, name))
                {
                    if (page is { } p) place = new ResolvedDestination(Math.Clamp(p, 0, other.PageCount - 1), null, null);
                    else if (name is not null) place = await other.ResolveNamedAsync(name).ConfigureAwait(true);
                }

                if (place is { } target && ReferenceEquals(window.Document, other)) window.PageView.ShowDestination(target, flash: true);
            }
            catch (Exception ex)
            {
                AppLog.Warn("The place in the linked document could not be found.", ex);
            }
        }
    }

    /// <summary>
    /// The place a URI fragment names in a PDF, as the PDF open parameters spell it: "page=3" (from
    /// 1), "nameddest=intro", or a bare "intro"; other parameters ("zoom=...") are passed over.
    /// </summary>
    internal static (int? Page, string? Name)? ParseFragment(string? fragment)
    {
        if (string.IsNullOrWhiteSpace(fragment)) return null;
        foreach (var part in fragment.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = part.IndexOf('=');
            if (equals < 0) return (null, part);

            var key = part[..equals].Trim();
            var value = part[(equals + 1)..].Trim();
            if (key.Equals("page", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var page) && page >= 1)
                return (page - 1, null);
            if (key.Equals("nameddest", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
                return (null, value);
        }
        return null;
    }

    private static bool SamePath(string? a, string? b)
    {
        if (a is null || b is null) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
