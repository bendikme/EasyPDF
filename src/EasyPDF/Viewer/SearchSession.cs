using EasyPDF.Pdf;
using EasyPDF.Services;

namespace EasyPDF.Viewer;

/// <summary>
/// One search through a document. Pages are searched one job at a time, starting at the page
/// being read and wrapping round, so the first match near the reader shows almost at once and
/// a long document never holds the screen up. Matches are kept in document order.
/// </summary>
public sealed class SearchSession
{
    /// <summary>Beyond this many matches the search stops collecting: no one steps through more.</summary>
    public const int MaxMatches = 100_000;

    private readonly PdfDocument _document;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<SearchMatch> _matches = new();
    private readonly int _startPage;
    private int _pendingSteps;

    public SearchSession(PdfDocument document, string query, bool matchCase, bool wholeWord, int startPage)
    {
        _document = document;
        Query = query;
        MatchCase = matchCase;
        WholeWord = wholeWord;
        _startPage = Math.Clamp(startPage, 0, Math.Max(0, document.PageCount - 1));
    }

    public string Query { get; }

    public bool MatchCase { get; }

    public bool WholeWord { get; }

    public IReadOnlyList<SearchMatch> Matches => _matches;

    /// <summary>Index of the current match in <see cref="Matches"/>, or -1.</summary>
    public int Current { get; private set; } = -1;

    public SearchMatch? CurrentMatch => Current >= 0 && Current < _matches.Count ? _matches[Current] : null;

    public bool IsComplete { get; private set; }

    public bool IsTruncated { get; private set; }

    public int PagesSearched { get; private set; }

    public bool IsCancelled => _cancellation.IsCancellationRequested;

    /// <summary>A page's matches arrived (on the UI thread).</summary>
    public event Action<IReadOnlyList<SearchMatch>>? MatchesFound;

    /// <summary>The current match changed and should be shown.</summary>
    public event Action<SearchMatch>? CurrentChanged;

    /// <summary>Progress: counts or completion changed.</summary>
    public event Action? Progress;

    public void Cancel() => _cancellation.Cancel();

    public async Task RunAsync()
    {
        var token = _cancellation.Token;
        var count = _document.PageCount;
        try
        {
            for (var k = 0; k < count; k++)
            {
                if (token.IsCancellationRequested || _document.IsClosed) return;
                var page = (_startPage + k) % count;

                var found = await _document.SearchPageAsync(page, Query, MatchCase, WholeWord, token).ConfigureAwait(true);
                if (token.IsCancellationRequested) return;
                PagesSearched++;

                if (found.Count > 0) Add(page, found);
                if (_matches.Count >= MaxMatches)
                {
                    IsTruncated = true;
                    break;
                }
                if (found.Count > 0 || PagesSearched % 25 == 0) Progress?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Searching for \"{Query}\" failed.", ex);
        }

        if (token.IsCancellationRequested) return;
        IsComplete = true;
        Progress?.Invoke();
    }

    private void Add(int page, IReadOnlyList<SearchMatch> found)
    {
        // Pages arrive out of order (from the reader's page round to it), so find this page's place.
        int lo = 0, hi = _matches.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_matches[mid].PageIndex < page) lo = mid + 1;
            else hi = mid;
        }

        var take = Math.Min(found.Count, MaxMatches - _matches.Count);
        var kept = take == found.Count ? found : found.Take(take).ToList();
        _matches.InsertRange(lo, kept);
        if (Current >= lo) Current += take;
        MatchesFound?.Invoke(kept);

        if (Current < 0)
        {
            // The first match found is the first at or after where the reader is.
            Current = lo;
            CurrentChanged?.Invoke(_matches[Current]);
            if (_pendingSteps != 0)
            {
                var steps = _pendingSteps;
                _pendingSteps = 0;
                Step(steps);
            }
        }
    }

    public void Next() => Step(+1);

    public void Previous() => Step(-1);

    private void Step(int direction)
    {
        if (_matches.Count == 0)
        {
            if (!IsComplete) _pendingSteps += direction;
            return;
        }
        var count = _matches.Count;
        Current = (((Current < 0 ? 0 : Current + direction) % count) + count) % count;
        CurrentChanged?.Invoke(_matches[Current]);
        Progress?.Invoke();
    }
}
