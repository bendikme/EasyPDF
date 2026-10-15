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
///
/// A page is on the trail once. Arriving somewhere (<see cref="Arrived"/>) takes every other
/// visit to that page off it, and of two visits to one page elsewhere on it the one nearer keeps
/// its place: following links back and forth between two pages leaves two places, not a growing
/// pile, and a link to somewhere on the page being read adds nothing.
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

    /// <summary>Called just before a jump, with where the view is now; <see cref="Arrived"/> follows once it has landed.</summary>
    public void Record(ViewPosition current)
    {
        Recorded?.Invoke(this, EventArgs.Empty);

        // Jumping again from the page last left: the later place on it stands for both.
        if (_back.Count > 0 && _back[^1].ShownPage == current.ShownPage) _back.RemoveAt(_back.Count - 1);

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

    /// <summary>
    /// The view has landed on <paramref name="page"/> (a jump, Back, Forward): every visit to it
    /// leaves the trail, and of the other pages each keeps only its visit nearest to here - the
    /// way back before the way forward at the same distance.
    /// </summary>
    public void Arrived(int page)
    {
        var seen = new HashSet<int> { page };
        var keepBack = new bool[_back.Count];
        var keepForward = new bool[_forward.Count];
        // Both lists end with the place nearest to here.
        for (int b = _back.Count - 1, f = _forward.Count - 1; b >= 0 || f >= 0; b--, f--)
        {
            if (b >= 0) keepBack[b] = seen.Add(_back[b].ShownPage);
            if (f >= 0) keepForward[f] = seen.Add(_forward[f].ShownPage);
        }

        var removed = Prune(_back, keepBack) | Prune(_forward, keepForward);
        if (removed) Changed?.Invoke(this, EventArgs.Empty);

        static bool Prune(List<ViewPosition> list, bool[] keep)
        {
            var before = list.Count;
            var i = 0;
            list.RemoveAll(_ => !keep[i++]);
            return list.Count != before;
        }
    }

    /// <summary>
    /// How many steps back (negative) or forward lead to the nearest place on another page than
    /// <paramref name="page"/>: Back from a page scrolled to by hand, which the trail has a visit
    /// to already, goes on past that visit. 0 when there is nothing that way.
    /// </summary>
    public int StepsAway(int direction, int page)
    {
        var list = direction < 0 ? _back : _forward;
        if (list.Count == 0) return 0;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].ShownPage != page) return Math.Sign(direction) * (list.Count - i);
        }
        // Only visits to this very page that way: one step still goes to where on it the visit was.
        return Math.Sign(direction);
    }

    public void Clear()
    {
        if (_back.Count == 0 && _forward.Count == 0) return;
        _back.Clear();
        _forward.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
