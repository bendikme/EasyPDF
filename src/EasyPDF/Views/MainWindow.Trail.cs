using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using EasyPDF.Pdf;
using EasyPDF.Resources;
using EasyPDF.Services;

namespace EasyPDF.Views;

/// <summary>
/// The trail of jumps. Once a link (or the outline, or a page number typed in) has taken the reader
/// somewhere, a bar over the top of the page shows the places behind and ahead - where the jump
/// was made from first of all - each a click away, however many steps back or forward it is.
/// Pointing at one shows the page, and the part of the document it is in.
/// </summary>
public partial class MainWindow
{
    /// <summary>Places shown each side of where the reader is; the rest are a click on "..." away.</summary>
    private const int TrailReach = 3;

    /// <summary>Hidden with its close button: until the next jump.</summary>
    private bool _trailDismissed;

    /// <summary>The page number in the "you are here" place, which follows the reader.</summary>
    private TextBlock? _trailHere;

    /// <summary>The outline's entries by the page each leads to, in page order: what part of the document a page is in.</summary>
    private (int Page, string Title)[] _sections = [];

    private void InitializeTrail()
    {
        View.History.Recorded += (_, _) => _trailDismissed = false;
        View.History.Changed += (_, _) => RefreshTrail();
    }

    /// <summary>The trail's places, in order: behind (oldest first), here, ahead (nearest first), each with how many steps away it is.</summary>
    internal List<(int Steps, int Page)> TrailPlaces()
    {
        var history = View.History;
        var places = new List<(int Steps, int Page)>();
        var back = history.BackEntries;
        for (var i = 0; i < back.Count; i++) places.Add((i - back.Count, back[i].ShownPage));
        places.Add((0, View.CurrentPage));
        var forward = history.ForwardEntries;
        for (var i = forward.Count - 1; i >= 0; i--) places.Add((forward.Count - i, forward[i].ShownPage));
        return places;
    }

    /// <summary>Whether the trail is showing. For tests.</summary>
    internal bool TrailShown => TrailBar.Visibility == Visibility.Visible;

    /// <summary>The trail as shown: each place's page number (and "..." for those folded away). For tests.</summary>
    internal IEnumerable<(string Text, bool Here)> TrailChipsShown() =>
        TrailChips.Children.OfType<Button>().Select(b => (b.Content is TextBlock t ? t.Text : b.Content?.ToString() ?? string.Empty, b.Tag is "on"));

    private void RefreshTrail()
    {
        var history = View.History;
        TrailChips.Children.Clear();
        _trailHere = null;
        if (_document is null || _trailDismissed || !(history.CanGoBack || history.CanGoForward))
        {
            TrailBar.Visibility = Visibility.Collapsed;
            return;
        }

        TrailBar.Visibility = Visibility.Visible;
        TrailBackButton.IsEnabled = history.CanGoBack;
        TrailForwardButton.IsEnabled = history.CanGoForward;
        TrailBackButton.ToolTip = history.CanGoBack
            ? UiLanguage.Format(Strings.Trail_BackTo, history.BackEntries[^1].ShownPage + 1)
            : Strings.View_Back;
        TrailForwardButton.ToolTip = history.CanGoForward
            ? UiLanguage.Format(Strings.Trail_ForwardTo, history.ForwardEntries[^1].ShownPage + 1)
            : Strings.View_Forward;

        var places = TrailPlaces();
        var here = history.BackEntries.Count;
        var from = Math.Max(0, here - TrailReach);
        var to = Math.Min(places.Count - 1, here + TrailReach);
        if (from > 0)
        {
            AddMore(places.GetRange(0, from));
            AddChevron();
        }
        for (var i = from; i <= to; i++)
        {
            if (i > from) AddChevron();
            AddPlace(places[i].Steps, places[i].Page);
        }
        if (to < places.Count - 1)
        {
            AddChevron();
            AddMore(places.GetRange(to + 1, places.Count - to - 1));
        }
    }

    /// <summary>The reader moved: "you are here" follows.</summary>
    private void UpdateTrailHere(int page)
    {
        if (_trailHere is not null) _trailHere.Text = (page + 1).ToString(UiLanguage.Culture);
    }

    private void AddPlace(int steps, int page)
    {
        var text = new TextBlock { Text = (page + 1).ToString(UiLanguage.Culture), VerticalAlignment = VerticalAlignment.Center };
        var place = TrailButton(text, steps == 0);
        AutomationProperties.SetName(place, UiLanguage.Format(Strings.Trail_Page, page + 1));
        if (steps == 0)
        {
            // Where the reader is: marked, and following them as they read on.
            text.FontWeight = FontWeights.SemiBold;
            place.ToolTip = Strings.Trail_Here;
            _trailHere = text;
        }
        else
        {
            // Ahead is lighter than behind: the way back is the one usually wanted.
            if (steps > 0) place.Opacity = 0.72;
            place.ToolTip = TrailPreview(page, steps);
            place.Click += (_, _) => GoAlongTrail(steps);
        }
        TrailChips.Children.Add(place);
    }

    /// <summary>"...": the places folded away, in a menu.</summary>
    private void AddMore(List<(int Steps, int Page)> places)
    {
        var more = TrailButton(new TextBlock { Text = "…", VerticalAlignment = VerticalAlignment.Center }, here: false);
        more.ToolTip = UiLanguage.Format(Strings.Trail_More, places.Count);
        AutomationProperties.SetName(more, UiLanguage.Format(Strings.Trail_More, places.Count));
        more.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = more, Placement = PlacementMode.Bottom };
            foreach (var (steps, page) in places)
            {
                var title = SectionOf(page);
                var item = new MenuItem
                {
                    Header = new TextBlock { Text = title is null ? UiLanguage.Format(Strings.Trail_Page, page + 1) : $"{UiLanguage.Format(Strings.Trail_Page, page + 1)}  ·  {title}" },
                    InputGestureText = StepsText(steps),
                };
                var go = steps;
                item.Click += (_, _) => GoAlongTrail(go);
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        };
        TrailChips.Children.Add(more);
    }

    private void AddChevron()
    {
        var chevron = new IconPath
        {
            Width = 10,
            Height = 10,
            Margin = new Thickness(1, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Data = (System.Windows.Media.Geometry)FindResource("GeoChevronRight"),
            StrokeThickness = 2,
        };
        chevron.SetResourceReference(IconPath.ForegroundProperty, "TextMutedBrush");
        TrailChips.Children.Add(chevron);
    }

    private Button TrailButton(TextBlock content, bool here)
    {
        content.FontSize = (double)FindResource("FontSizeSmall");
        return new Button
        {
            Style = (Style)FindResource(here ? "ToolbarToggleStyle" : "ToolbarButtonStyle"),
            Tag = here ? "on" : null,
            Content = content,
            Focusable = false,
            MinHeight = 24,
            MinWidth = 28,
            Padding = new Thickness(7, 0, 7, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
    }

    /// <summary>What pointing at a place shows: the page, its number, the part of the document it is in, and how far away it is.</summary>
    private StackPanel TrailPreview(int page, int steps)
    {
        var panel = new StackPanel { MaxWidth = 260 };
        if (_thumbnails is { } thumbnails && _document is { } document && (uint)page < (uint)document.PageCount)
        {
            var size = document.PageSizes[page];
            var (w, h) = (View.Rotation & 1) == 1 ? (size.Height, size.Width) : (size.Width, size.Height);
            var width = 132.0;
            var height = Math.Round(width * h / w);
            if (height > 170)
            {
                height = 170;
                width = Math.Round(height * w / h);
            }
            panel.Children.Add(new ThumbnailImage
            {
                Source = thumbnails,
                PageIndex = page,
                Width = Math.Max(8, width),
                Height = Math.Max(8, height),
                Margin = new Thickness(1, 1, 1, 8),
                HorizontalAlignment = HorizontalAlignment.Left,
            });
        }
        panel.Children.Add(new TextBlock { Text = UiLanguage.Format(Strings.Trail_Page, page + 1), FontWeight = FontWeights.SemiBold });
        if (SectionOf(page) is { } title) panel.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        var hint = new TextBlock { Text = StepsText(steps), Margin = new Thickness(0, 2, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        panel.Children.Add(hint);
        return panel;
    }

    private static string StepsText(int steps) => steps switch
    {
        -1 => Strings.Trail_OneBack,
        1 => Strings.Trail_OneForward,
        < 0 => UiLanguage.Format(Strings.Trail_StepsBack, -steps),
        _ => UiLanguage.Format(Strings.Trail_StepsForward, steps),
    };

    private void GoAlongTrail(int steps)
    {
        View.GoBy(steps);
        View.Focus();
    }

    private void OnTrailBackClick(object sender, RoutedEventArgs e) => GoAlongTrail(-1);

    private void OnTrailForwardClick(object sender, RoutedEventArgs e) => GoAlongTrail(1);

    private void OnTrailCloseClick(object sender, RoutedEventArgs e)
    {
        _trailDismissed = true;
        RefreshTrail();
        View.Focus();
    }

    // ===================================================================== sections

    /// <summary>The outline, as the part of the document each page is in: the last entry at or before it, in page order.</summary>
    private void IndexSections(IReadOnlyList<OutlineItem> items)
    {
        var list = new List<(int Page, string Title)>();
        Walk(items, 0);
        // Stable, so of entries on one page the later (and deeper) wins.
        _sections = list.Select((s, i) => (s, i)).OrderBy(x => x.s.Page).ThenBy(x => x.i).Select(x => x.s).ToArray();
        if (TrailShown) RefreshTrail();

        void Walk(IReadOnlyList<OutlineItem> level, int depth)
        {
            foreach (var item in level)
            {
                if (list.Count >= 100_000) return;
                if (item.Target is GoToTarget go && item.Title.Length > 0) list.Add((go.Destination.PageIndex, item.Title.Trim()));
                if (depth < 64) Walk(item.Children, depth + 1);
            }
        }
    }

    internal string? SectionOf(int page)
    {
        var sections = _sections;
        int lo = 0, hi = sections.Length - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (sections[mid].Page <= page)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return found < 0 ? null : sections[found].Title;
    }
}
