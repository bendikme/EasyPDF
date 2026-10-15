namespace EasyPDF.Viewer;

/// <summary>A place in the document: a page and how far down it the view starts.</summary>
/// <param name="Seen">The page the reader saw most of there, when known: what the place is called.</param>
public readonly record struct ViewPosition(int Page, double Offset, int Seen = -1)
{
    /// <summary>The page to name the place by: the one seen most, or the one at the top.</summary>
    public int ShownPage => Seen >= 0 ? Seen : Page;
}

/// <summary>
/// Back and forward, as in a browser: following a link remembers where the reader was, Back
/// returns there, and following a new link after going back drops what was ahead. The places
/// behind and ahead are the trail the window shows, to go back (or forward) several at once.
/// </summary>
public sealed class NavigationHistory
{
    public const int Limit = 100;

    private readonly List<ViewPosition> _back = new();
    private readonly List<ViewPosition> _forward = new();

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    /// <summary>The places Back goes to, the oldest first: the next one Back goes to is the last.</summary>
    public IReadOnlyList<ViewPosition> BackEntries => _back;

    /// <summary>The places Forward goes to, the farthest first: the next one Forward goes to is the last.</summary>
    public IReadOnlyList<ViewPosition> ForwardEntries => _forward;

    public event EventHandler? Changed;

    /// <summary>Raised when a jump is recorded (before <see cref="Changed"/>), as opposed to going back or forward.</summary>
    public event EventHandler? Recorded;

    /// <summary>Called just before a jump, with where the view is now.</summary>
    public void Record(ViewPosition current)
    {
        Recorded?.Invoke(this, EventArgs.Empty);

        // Jumping twice from the same place keeps one entry.
        if (_back.Count > 0 && _back[^1].Page == current.Page && Math.Abs(_back[^1].Offset - current.Offset) < 0.02)
        {
            _forward.Clear();
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        _back.Add(current);
        if (_back.Count > Limit) _back.RemoveAt(0);
        _forward.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Where Back goes, having remembered <paramref name="current"/> for Forward.</summary>
    public ViewPosition? Back(ViewPosition current) => Go(-1, current);

    public ViewPosition? Forward(ViewPosition current) => Go(1, current);

    /// <summary>
    /// Where going <paramref name="steps"/> back (negative) or forward leads, with every place
    /// passed - <paramref name="current"/> first - remembered for the way back. Null when there are
    /// not that many places that way.
    /// </summary>
    public ViewPosition? Go(int steps, ViewPosition current)
    {
        if (steps == 0 || (steps < 0 ? -steps > _back.Count : steps > _forward.Count)) return null;

        var at = current;
        for (var i = 0; i < Math.Abs(steps); i++)
        {
            var (from, to) = steps < 0 ? (_back, _forward) : (_forward, _back);
            var target = from[^1];
            from.RemoveAt(from.Count - 1);
            to.Add(at);
            at = target;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return at;
    }

    public void Clear()
    {
        if (_back.Count == 0 && _forward.Count == 0) return;
        _back.Clear();
        _forward.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
