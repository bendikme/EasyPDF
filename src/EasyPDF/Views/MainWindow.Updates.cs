using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using EasyPDF.Resources;
using EasyPDF.Services;

namespace EasyPDF.Views;

/// <summary>
/// Updates, as the window shows them: a button in the header once there is something to do - a
/// downloaded update to restart into, or (for a portable copy) a newer release to fetch - and
/// "Check for updates" and "Update automatically" in the More menu. Otherwise nothing: an update
/// that is ready installs by itself when EasyPDF closes.
/// </summary>
public partial class MainWindow
{
    private void InitializeUpdates()
    {
        Updater.StateChanged += OnUpdateStateChanged;
        ShowUpdateState();
    }

    private void UnloadUpdates() => Updater.StateChanged -= OnUpdateStateChanged;

    private void OnUpdateStateChanged(object? sender, EventArgs e)
    {
        if (_closed) return;
        ShowUpdateState();
        // Asked for by hand: the result is said when it comes.
        if (_announceUpdate && Updater.Current is { State: UpdateState.Ready, Release: { } release })
        {
            _announceUpdate = false;
            ShowToast(UiLanguage.Format(Strings.Update_ReadyToast, release.Version));
        }
    }

    /// <summary>A check asked for in this window is still under way: its result is told here.</summary>
    private bool _announceUpdate;

    /// <summary>The header button: shown with an update ready, or a newer release a portable copy cannot install.</summary>
    private void ShowUpdateState()
    {
        var updater = Updater.Current;
        var show = updater is { State: UpdateState.Ready or UpdateState.Available, Release: not null };
        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (UpdateButton.Visibility == visibility && !show) return;

        UpdateButton.Visibility = visibility;
        if (show)
        {
            var version = updater!.Release!.Version;
            UpdateButton.ToolTip = updater.State == UpdateState.Ready
                ? UiLanguage.Format(Strings.Update_Ready_Tooltip, version)
                : UiLanguage.Format(Strings.Update_Available_Tooltip, version);
        }
        FitHeader();
    }

    /// <summary>Whether the header offers an update. For tests.</summary>
    internal bool UpdateOffered => UpdateButton.Visibility == Visibility.Visible;

    private void OnUpdateClick(object sender, RoutedEventArgs e) => BuildUpdateMenu().IsOpen = true;

    internal ContextMenu BuildUpdateMenu()
    {
        var menu = new ContextMenu { PlacementTarget = UpdateButton, Placement = PlacementMode.Bottom, VerticalOffset = -6 };
        if (Updater.Current is not { Release: { } release } updater) return menu;

        if (updater.State == UpdateState.Ready)
        {
            var restart = new MenuItem { Header = UiLanguage.Format(Strings.Update_RestartNow, release.Version), FontWeight = FontWeights.SemiBold };
            restart.Click += (_, _) => RestartToUpdate();
            menu.Items.Add(restart);
        }
        else
        {
            var download = new MenuItem { Header = UiLanguage.Format(Strings.Update_Download, release.Version), FontWeight = FontWeights.SemiBold };
            download.Click += (_, _) => OpenOutside(release.PageUrl);
            menu.Items.Add(download);
        }

        var notes = new MenuItem { Header = UiLanguage.Format(Strings.Update_WhatsNew, release.Version) };
        notes.Click += (_, _) => OpenOutside(release.PageUrl);
        menu.Items.Add(notes);
        menu.Items.Add(new Separator());
        menu.Items.Add(AutoUpdateItem());
        return menu;
    }

    /// <summary>"Check for updates" and "Update automatically", for the More menu.</summary>
    internal IEnumerable<Control> UpdateMenuItems()
    {
        var check = new MenuItem { Header = Strings.Update_Check };
        check.Click += (_, _) => _ = CheckForUpdatesAsync();
        yield return check;
        yield return AutoUpdateItem();
    }

    private MenuItem AutoUpdateItem()
    {
        var automatic = new MenuItem
        {
            Header = Strings.Update_Automatic,
            IsCheckable = true,
            IsChecked = SettingsStore.Current.Settings.AutoUpdate,
            ToolTip = Strings.Update_Automatic_Tooltip,
        };
        automatic.Click += (_, _) =>
        {
            SettingsStore.Current.Settings.AutoUpdate = automatic.IsChecked;
            SettingsStore.Current.Save();
        };
        return automatic;
    }

    /// <summary>Asks GitHub now, and says what it found. A newer release downloads; the header offers it once it is ready.</summary>
    internal async Task CheckForUpdatesAsync()
    {
        if (Updater.Current is not { } updater) return;
        ShowToast(Strings.Update_Checking);
        _announceUpdate = true;
        var outcome = await updater.CheckAsync(download: true).ConfigureAwait(true);
        if (_closed) return;
        switch (outcome)
        {
            case UpdateCheckOutcome.UpToDate:
                _announceUpdate = false;
                ShowToast(UiLanguage.Format(Strings.Update_UpToDate, Updater.CurrentVersion));
                break;
            case UpdateCheckOutcome.Available when updater.Release is { } release:
                _announceUpdate = false;
                ShowToast(UiLanguage.Format(Strings.Update_Available_Tooltip, release.Version));
                break;
            case UpdateCheckOutcome.Ready when updater.Release is { } release:
                _announceUpdate = false;
                ShowToast(UiLanguage.Format(Strings.Update_ReadyToast, release.Version));
                break;
            default:
                _announceUpdate = false;
                ShowToast(Strings.Update_Failed);
                break;
        }
    }

    /// <summary>Installs the downloaded update now: EasyPDF closes, the installer runs, and the new EasyPDF opens the same documents.</summary>
    private void RestartToUpdate()
    {
        if (!_app.RestartToUpdate()) ShowNotice(Strings.Update_Title, Strings.Update_InstallFailed);
    }
}
