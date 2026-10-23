using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using EasyPDF.Resources;
using EasyPDF.Viewer;

namespace EasyPDF.Views;

/// <summary>
/// The 3D view over the pages: the model, and above it a bar with the views stored with it, Home
/// and Close; below, how to turn it, until it has been. Built in code the first time a window shows
/// a model, so a window that never does pays nothing for it.
/// </summary>
internal sealed class ModelPanel : Grid
{
    private readonly ModelView _view = new();
    private readonly ComboBox _views;
    private readonly TextBlock _message;
    private readonly Border _hintPill;
    private readonly TextBlock _hint;
    private readonly TextBlock _note;
    private IReadOnlyList<(string Name, ModelCamera Camera)> _cameras = [];

    /// <summary>Set while the list of views is filled, when choosing one is not the user's doing.</summary>
    private bool _listing;

    public ModelPanel(FrameworkElement owner)
    {
        SetResourceReference(BackgroundProperty, "BackgroundBrush");
        Children.Add(_view);
        _view.CloseRequested += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

        _message = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 480,
            Margin = new Thickness(24),
            IsHitTestVisible = false,
        };
        _message.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeMedium");
        _message.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        Children.Add(_message);

        // The bar: views, Home, Close - where the trail of jumps sits over the pages.
        _views = new ComboBox
        {
            MinWidth = 220,
            MaxWidth = 380,
            MinHeight = 26,
            Padding = new Thickness(8, 2, 6, 2),
            Margin = new Thickness(2, 0, 2, 0),
            Focusable = false,
            ToolTip = Strings.Model_Views,
            IsTextSearchEnabled = true,
        };
        _views.SetResourceReference(StyleProperty, "AppComboBoxStyle");
        AutomationProperties.SetName(_views, Strings.Model_Views);
        _views.SelectionChanged += (_, _) =>
        {
            if (!_listing && _views.SelectedIndex >= 0 && _views.SelectedIndex < _cameras.Count)
                _view.GoTo(_cameras[_views.SelectedIndex].Camera, animate: true);
        };
        _views.DropDownClosed += (_, _) => _view.Focus();

        var home = ToolButton(owner, "GeoHome", Strings.Model_Home, 30);
        home.Click += (_, _) =>
        {
            _view.GoHome();
            _view.Focus();
        };
        var close = ToolButton(owner, "GeoClose", Strings.Model_Close, 26, size: 10);
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

        var separator = new Border { Width = 1, Margin = new Thickness(3, 6, 3, 6) };
        separator.SetResourceReference(Border.BackgroundProperty, "BorderBrushSoft");
        var bar = new Border
        {
            Margin = new Thickness(12, 10, 12, 0),
            Padding = new Thickness(3),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _views, home, separator, close } },
        };
        bar.SetResourceReference(StyleProperty, "StatusPillStyle");
        bar.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        bar.SetResourceReference(Border.BorderBrushProperty, "BorderBrushStrong");
        bar.SetResourceReference(Border.CornerRadiusProperty, "RadiusMedium");
        Children.Add(bar);

        // How to turn it, until it has been turned; and what of it could not be shown.
        _hint = new TextBlock { Text = Strings.Model_Hint };
        _note = new TextBlock { Visibility = Visibility.Collapsed };
        foreach (var text in (ReadOnlySpan<TextBlock>)[_hint, _note])
        {
            text.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeSmall");
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        }
        _hintPill = new Border
        {
            Margin = new Thickness(12, 0, 12, 12),
            Padding = new Thickness(10, 4, 10, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = new StackPanel { Children = { _hint, _note } },
        };
        _hintPill.SetResourceReference(StyleProperty, "StatusPillStyle");
        _hintPill.SetResourceReference(Border.CornerRadiusProperty, "RadiusMedium");
        Children.Add(_hintPill);
        _view.Interacted += (_, _) =>
        {
            _hint.Visibility = Visibility.Collapsed;
            UpdateHintPill();
        };
    }

    /// <summary>Raised by Close, or Esc in the model.</summary>
    public event EventHandler? CloseRequested;

    public ModelView View => _view;

    /// <summary>The message over the view, if one is up. For tests.</summary>
    internal string? Message => _message.Visibility == Visibility.Visible ? _message.Text : null;

    internal IReadOnlyList<string> ViewNames => [.. _cameras.Select(c => c.Name)];

    /// <summary>While the model is being read.</summary>
    public void ShowReading()
    {
        _view.Detach();
        Say(Strings.Model_Reading);
        FocusModel();
        _listing = true;
        _views.ItemsSource = null;
        _listing = false;
        _views.Visibility = Visibility.Collapsed;
        _hintPill.Visibility = Visibility.Collapsed;
    }

    /// <summary>Why there is no model to show.</summary>
    public void ShowFailure(string message)
    {
        _view.Detach();
        Say(message);
    }

    /// <summary>Shows a model from the first of its views.</summary>
    public void ShowModel(ModelRenderer renderer, IReadOnlyList<(string Name, ModelCamera Camera)> views, string? note)
    {
        _message.Visibility = Visibility.Collapsed;
        _cameras = views;
        _listing = true;
        _views.ItemsSource = views.Select(v => v.Name).ToList();
        _views.SelectedIndex = 0;
        _listing = false;
        _views.Visibility = views.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _hint.Visibility = Visibility.Visible;
        _note.Text = note ?? string.Empty;
        _note.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateHintPill();
        _view.Show(renderer, views[0].Camera);
        FocusModel();
    }

    /// <summary>Keys go to the model - Esc, Home - not to the pages under it; once it is in the layout, which a panel just shown is not yet.</summary>
    private void FocusModel() => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
    {
        if (IsVisible) _view.Focus();
    });

    /// <summary>Stops drawing and lets go of the model.</summary>
    public void Release()
    {
        _view.Detach();
        _cameras = [];
        _listing = true;
        _views.ItemsSource = null;
        _listing = false;
    }

    private void Say(string message)
    {
        _message.Text = message;
        _message.Visibility = Visibility.Visible;
    }

    private void UpdateHintPill() =>
        _hintPill.Visibility = _hint.Visibility == Visibility.Visible || _note.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;

    private static Button ToolButton(FrameworkElement owner, string geometry, string name, double width, double size = 14)
    {
        var button = new Button
        {
            Focusable = false,
            MinHeight = 26,
            Width = width,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            ToolTip = name,
            Content = new IconPath { Width = size, Height = size, Data = owner.TryFindResource(geometry) as Geometry, StrokeThickness = 2 },
        };
        button.SetResourceReference(StyleProperty, "ToolbarButtonStyle");
        AutomationProperties.SetName(button, name);
        return button;
    }
}
