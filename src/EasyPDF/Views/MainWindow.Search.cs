using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using EasyPDF.Pdf;
using EasyPDF.Resources;
using EasyPDF.Services;
using EasyPDF.Viewer;

namespace EasyPDF.Views;

public partial class MainWindow
{
    private SearchSession? _search;
    private DispatcherTimer? _searchDelay;
    private bool _matchCase;
    private bool _wholeWord;

    internal SearchSession? Search => _search;

    public void FocusSearch()
    {
        if (!SearchBox.IsEnabled) return;
        if (_fullScreen) ExitFullScreen();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Searching starts a moment after typing stops, so each keystroke does not restart it.
        _searchDelay ??= new DispatcherTimer(TimeSpan.FromMilliseconds(280), DispatcherPriority.Input, (_, _) =>
        {
            _searchDelay!.Stop();
            StartSearch(immediate: true);
        }, Dispatcher);
        _searchDelay.Stop();

        if (SearchBox.Text.Trim().Length == 0)
        {
            EndSearch(clearText: false);
            return;
        }
        _searchDelay.Start();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                var backwards = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
                if (_search is { } running && running.Query == SearchBox.Text.Trim() && !running.IsCancelled)
                {
                    // Until the first match is in, it will be shown by itself.
                    if (running.Current >= 0) MoveMatch(backwards ? -1 : +1);
                }
                else
                {
                    StartSearch(immediate: true);
                }
                break;

            case Key.Escape:
                e.Handled = true;
                SearchBox.Text = string.Empty;
                EndSearch(clearText: true);
                View.Focus();
                break;
        }
    }

    /// <summary>The typed text stops short of the count and the buttons, whatever their width.</summary>
    private void OnSearchToolsSizeChanged(object sender, SizeChangedEventArgs e) => FitSearchPadding();

    private void OnSearchToolsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => FitSearchPadding();

    /// <summary>In a tight box the count goes; the arrows and the typed text stay.</summary>
    private void OnSearchAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var tight = SearchArea.ActualWidth < 240;
        var visibility = tight ? Visibility.Collapsed : Visibility.Visible;
        if (SearchStatus.Visibility != visibility) SearchStatus.Visibility = visibility;
        FitSearchPadding();
    }

    private void FitSearchPadding()
    {
        var right = SearchTools.IsVisible ? SearchTools.ActualWidth + SearchTools.Margin.Right + 6 : 30;
        SearchBox.Padding = SearchBox.Padding with { Right = right };
    }

    private void OnSearchNext(object sender, RoutedEventArgs e) => MoveMatch(+1);

    private void OnSearchPrevious(object sender, RoutedEventArgs e) => MoveMatch(-1);

    private void OnSearchClear(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        EndSearch(clearText: true);
        SearchBox.Focus();
    }

    internal void MoveMatch(int direction)
    {
        if (_search is null)
        {
            if (SearchBox.Text.Trim().Length > 0) StartSearch(immediate: true);
            else FocusSearch();
            return;
        }
        if (direction > 0) _search.Next();
        else _search.Previous();
    }

    /// <param name="reveal">Whether the first match is scrolled to. A search run again after the file reloads only marks it: the reader stays where they were.</param>
    internal void StartSearch(bool immediate, bool reveal = true)
    {
        _searchDelay?.Stop();
        var query = SearchBox.Text.Trim();
        if (_document is not { } document || query.Length == 0)
        {
            EndSearch(clearText: false);
            return;
        }

        EndSearch(clearText: false);
        var session = new SearchSession(document, query, _matchCase, _wholeWord, Math.Max(0, View.CurrentPage));
        _search = session;
        session.MatchesFound += matches =>
        {
            if (ReferenceEquals(_search, session)) View.AddSearchMatches(matches);
        };
        session.CurrentChanged += match =>
        {
            if (!ReferenceEquals(_search, session)) return;
            View.ShowMatch(match, reveal);
            reveal = true; // stepping on with Enter or F3 goes to the match
        };
        session.Progress += () =>
        {
            if (ReferenceEquals(_search, session)) UpdateSearchStatus();
        };
        UpdateSearchStatus();
        _ = session.RunAsync();
    }

    private void EndSearch(bool clearText)
    {
        _searchDelay?.Stop();
        _search?.Cancel();
        _search = null;
        View.ClearSearch();
        if (clearText && SearchBox.Text.Length > 0) SearchBox.Text = string.Empty;
        UpdateSearchStatus();
    }

    private void UpdateSearchStatus()
    {
        if (_search is not { } s)
        {
            SearchTools.Visibility = SearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            SearchStatus.Text = string.Empty;
            return;
        }

        SearchTools.Visibility = Visibility.Visible;
        var count = s.Matches.Count;
        if (count == 0)
        {
            SearchStatus.Text = s.IsComplete ? Strings.Main_Search_None : Strings.Main_Search_Searching;
            SearchStatus.SetResourceReference(TextBlock.ForegroundProperty, s.IsComplete ? "DangerBrush" : "TextMutedBrush");
            return;
        }

        SearchStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        var more = s.IsComplete && !s.IsTruncated ? string.Empty : "+";
        SearchStatus.Text = UiLanguage.Format(Strings.Main_Search_Count, Math.Max(1, s.Current + 1), count, more);
    }

    internal void SetSearchOptions(bool matchCase, bool wholeWord)
    {
        _matchCase = matchCase;
        _wholeWord = wholeWord;
        if (_search is not null || SearchBox.Text.Trim().Length > 0) StartSearch(immediate: true);
    }
}
