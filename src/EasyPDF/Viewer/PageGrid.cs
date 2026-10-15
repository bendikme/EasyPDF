using System.Globalization;

namespace EasyPDF.Viewer;

/// <summary>
/// How many pages are shown at a time, and how: <see cref="Columns"/> across and
/// <see cref="Rows"/> down, read row by row. With <see cref="CoverAlone"/> the first page stands
/// on its own in the last column, as a book's cover does, so that the pages after it pair up as
/// the book's spreads.
/// </summary>
public readonly record struct PageGrid
{
    public const int MaxColumns = 6;
    public const int MaxRows = 4;

    public PageGrid(int columns, int rows, bool coverAlone = false)
    {
        Columns = Math.Clamp(columns, 1, MaxColumns);
        Rows = Math.Clamp(rows, 1, MaxRows);
        CoverAlone = coverAlone && Columns > 1;
    }

    public int Columns { get; }

    public int Rows { get; }

    public bool CoverAlone { get; }

    /// <summary>Pages on a full sheet.</summary>
    public int PerSheet => Math.Max(1, Columns) * Math.Max(1, Rows);

    /// <summary>Empty cells before the first page: one with the cover alone.</summary>
    public int Lead => CoverAlone ? 1 : 0;

    public static PageGrid One => new(1, 1);

    /// <summary>The ones offered in the menus, in order.</summary>
    public static IReadOnlyList<PageGrid> Presets { get; } =
    [
        new(1, 1),
        new(2, 1),
        new(2, 1, coverAlone: true),
        new(1, 2),
        new(2, 2),
        new(3, 2),
        new(3, 3),
    ];

    /// <summary>"2x1", or "2x1c" with the cover alone: how it is remembered.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Columns}x{Rows}{(CoverAlone ? "c" : string.Empty)}");

    /// <summary>The inverse of <see cref="ToString"/>; null for anything else.</summary>
    public static PageGrid? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim().ToLowerInvariant();
        var cover = s.EndsWith('c');
        if (cover) s = s[..^1];
        var x = s.IndexOf('x');
        if (x <= 0 || x == s.Length - 1) return null;
        if (!int.TryParse(s.AsSpan(0, x), NumberStyles.None, CultureInfo.InvariantCulture, out var columns) ||
            !int.TryParse(s.AsSpan(x + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var rows))
        {
            return null;
        }
        if (columns is < 1 or > MaxColumns || rows is < 1 or > MaxRows) return null;
        return new PageGrid(columns, rows, cover);
    }
}
