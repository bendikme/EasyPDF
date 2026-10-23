using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EasyPDF.Services;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace EasyPDF.Viewer;

/// <summary>
/// Shows a 3D model, turned by dragging (about the point under the pointer), moved by dragging
/// with the right button or Shift, and zoomed toward the pointer by the wheel; the arrow keys turn
/// it, + and - zoom, Home goes back to the start, Esc asks to close.
///
/// Frames are drawn by <see cref="ModelRenderer"/> on a thread of their own, always for the latest
/// camera. While the model moves they are drawn at whatever resolution keeps up; once it is still,
/// at the screen's, and then with smoothed edges when that takes no more than a moment.
/// </summary>
internal sealed class ModelView : FrameworkElement
{
    /// <summary>How long after the last move a frame counts as still.</summary>
    private const double SettleMilliseconds = 140;

    /// <summary>The longest a moving frame should take; resolution is lowered to keep under it.</summary>
    private const double MovingBudget = 22;

    /// <summary>The resolutions moving frames are drawn at, against the screen's: a few, so that their bitmaps are reused.</summary>
    private static readonly double[] MovingScales = [1, 0.85, 0.7, 0.6, 0.5, 0.42, 0.35, 0.3];

    /// <summary>Radians of turn a device-independent pixel of drag.</summary>
    private const double TurnPerPixel = Math.PI / 420;

    private ModelRenderer? _renderer;
    private ModelCamera _home;
    private Vec3 _pivot;
    private Vec3 _worldUp = new(0, 0, 1);
    private WriteableBitmap? _bitmap;

    /// <summary>The bitmap of the other size - moving frames' or still ones' - kept for the next switch.</summary>
    private WriteableBitmap? _spare;

    // Shared with the drawing thread, under _gate.
    private readonly object _gate = new();
    private ModelCamera _camera;
    private int _version;
    private long _changedAt;
    private int _pixelWidth;
    private int _pixelHeight;
    private uint _top = 0xF4F5F7;
    private uint _bottom = 0xCDD0D4;
    private Session? _session;

    /// <summary>The drawing of one model: its thread's wake-up call, and its stop.</summary>
    private sealed class Session
    {
        public readonly AutoResetEvent Wake = new(true);
        public volatile bool Stopped;
    }

    private enum Drag
    {
        None,
        Turn,
        Move,
    }

    private Drag _drag;
    private Point _last;

    public ModelView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        IsManipulationEnabled = true;
        Cursor = Cursors.SizeAll;
        Loaded += (_, _) =>
        {
            if (ThemeService.Current is { } theme) theme.PaletteChanged += OnPaletteChanged;
            UpdateBackground();
        };
        Unloaded += (_, _) =>
        {
            if (ThemeService.Current is { } theme) theme.PaletteChanged -= OnPaletteChanged;
        };
    }

    /// <summary>Raised when Esc is pressed.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised the first time the model is turned, moved or zoomed.</summary>
    public event EventHandler? Interacted;

    /// <summary>Raised on the UI thread after each frame is shown. For tests.</summary>
    internal event EventHandler? FramePresented;

    internal ModelCamera Camera
    {
        get
        {
            lock (_gate) return _camera;
        }
    }

    /// <summary>The picture shown, as drawn last. For tests.</summary>
    internal WriteableBitmap? Picture => _bitmap;

    /// <summary>Whether the last frame shown is the best there will be for this camera. For tests.</summary>
    internal bool Settled { get; private set; }

    /// <summary>Shows a model from <paramref name="home"/>, which Home goes back to; the turntable's axis is the world axis nearest its up.</summary>
    public void Show(ModelRenderer renderer, ModelCamera home)
    {
        Detach();
        _renderer = renderer;
        _home = home;
        _worldUp = ModelCamera.NearestAxis(home.Up);
        _pivot = DefaultPivot(home);
        var session = new Session();
        _session = session;
        SetCamera(home);
        lock (_gate) _changedAt = 0;
        new Thread(() => DrawLoop(renderer, session)) { IsBackground = true, Name = "3D model" }.Start();
    }

    /// <summary>Stops drawing and lets go of the model and the picture.</summary>
    public void Detach()
    {
        StopAnimation();
        if (_session is { } session)
        {
            session.Stopped = true;
            session.Wake.Set();
        }
        _session = null;
        _renderer = null;
        _bitmap = null;
        _spare = null;
        InvalidateVisual();
    }

    /// <summary>Goes to a camera: at once, or turning there over a moment.</summary>
    public void GoTo(ModelCamera camera, bool animate)
    {
        _pivot = DefaultPivot(camera);
        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            StopAnimation();
            SetCamera(camera);
            return;
        }
        _from = Camera;
        _to = camera;
        _animationStart = Stopwatch.GetTimestamp();
        if (_animating) return;
        _animating = true;
        CompositionTarget.Rendering += OnAnimationFrame;
    }

    public void GoHome() => GoTo(_home, animate: true);

    /// <summary>Where turning goes round when nothing is under the pointer: the point on the line of sight level with the model's middle.</summary>
    private Vec3 DefaultPivot(ModelCamera camera)
    {
        if (_renderer is not { } renderer) return default;
        var centre = renderer.Scene.Centre;
        var ahead = Vec3.Dot(centre - camera.Eye, camera.Forward);
        return ahead > renderer.Scene.Radius * 1e-3 ? camera.Eye + (camera.Forward * ahead) : centre;
    }

    private void SetCamera(ModelCamera camera)
    {
        lock (_gate)
        {
            _camera = camera;
            _version++;
            _changedAt = Stopwatch.GetTimestamp();
        }
        Settled = false;
        _session?.Wake.Set();
    }

    // ===================================================================== animation

    private bool _animating;
    private ModelCamera _from;
    private ModelCamera _to;
    private long _animationStart;

    private void OnAnimationFrame(object? sender, EventArgs e)
    {
        var t = Math.Min(1, Stopwatch.GetElapsedTime(_animationStart).TotalMilliseconds / 360);
        var eased = t * t * (3 - (2 * t));
        SetCamera(ModelCamera.Between(_from, _to, eased));
        if (t >= 1) StopAnimation();
    }

    private void StopAnimation()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnAnimationFrame;
    }

    // ===================================================================== drawing

    private enum Quality
    {
        None,
        Moving,
        Still,
        Smooth,
    }

    /// <summary>
    /// Draws frames as long as the view shows a model: the latest camera, at the quality the moment
    /// allows, each handed to the UI thread when done. A still frame that the camera moves away from
    /// before it is finished is dropped.
    /// </summary>
    private void DrawLoop(ModelRenderer renderer, Session session)
    {
        var wake = session.Wake;
        var buffer = Array.Empty<int>();
        var drawnVersion = -1;
        var drawn = Quality.None;
        double stillTime = 0, smoothTime = 0;

        // Moving frames' resolution, a step of MovingScales. A frame's time is partly the model's -
        // placing its points, sorting and setting up each triangle and line - and partly the
        // pixels'; a still frame at one sample a pixel and a smooth one at four tell the two
        // apart. Moving frames get the most pixels that keep them under budget - or all of them
        // when the model's part alone is most of the budget, as fewer would then blur the picture
        // and hardly speed it up.
        var step = 0;
        int Step()
        {
            if (stillTime <= 0) return 0;
            var pixels = smoothTime > stillTime ? (smoothTime - stillTime) / 3 : stillTime / 2;
            var model = Math.Max(0, stillTime - pixels);
            if (model + pixels <= MovingBudget || model >= MovingBudget * 0.8) return 0;
            for (var i = 0; i < MovingScales.Length; i++)
                if (model + (pixels * MovingScales[i] * MovingScales[i]) <= MovingBudget) return i;
            return MovingScales.Length - 1;
        }
        while (!session.Stopped)
        {
            ModelCamera camera;
            int version, width, height;
            uint top, bottom;
            double sinceChange;
            lock (_gate)
            {
                camera = _camera;
                version = _version;
                width = _pixelWidth;
                height = _pixelHeight;
                top = _top;
                bottom = _bottom;
                sinceChange = Stopwatch.GetElapsedTime(_changedAt).TotalMilliseconds;
            }

            Quality quality;
            if (width <= 0 || height <= 0) quality = Quality.None;
            else if (version != drawnVersion) quality = sinceChange < SettleMilliseconds ? Quality.Moving : Quality.Still;
            else if (drawn == Quality.Moving) quality = sinceChange < SettleMilliseconds ? Quality.None : Quality.Still;
            // Four samples a pixel cost about four times a still frame: only when that is quick.
            else if (drawn == Quality.Still && stillTime * 4 < 400) quality = Quality.Smooth;
            else quality = Quality.None;

            if (quality == Quality.None)
            {
                // Nothing new: wait for a change - or, after moving frames, for the camera to settle.
                if (drawn == Quality.Moving && version == drawnVersion) wake.WaitOne(TimeSpan.FromMilliseconds(Math.Max(1, SettleMilliseconds - sinceChange)));
                else
                {
                    if (drawnVersion == version && drawn != Quality.Moving)
                    {
                        renderer.Trim();
                        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Settled = !session.Stopped && version == CurrentVersion);
                    }
                    wake.WaitOne();
                }
                continue;
            }

            var drawnStep = quality == Quality.Moving ? step : -1;
            var k = drawnStep >= 0 ? MovingScales[drawnStep] : 1;
            var w = Math.Max(1, (int)Math.Round(width * k));
            var h = Math.Max(1, (int)Math.Round(height * k));
            if (buffer.Length < width * height) buffer = new int[width * height];
            var started = Stopwatch.GetTimestamp();
            bool done;
            try
            {
                done = renderer.Render(camera, w, h, quality == Quality.Smooth ? 2 : 1, top, bottom, buffer,
                    quality == Quality.Moving ? null : () => session.Stopped || CurrentVersion != version);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Drawing the 3D model failed.", ex);
                return;
            }
            var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (!done || session.Stopped) continue;

            if (quality == Quality.Still)
            {
                stillTime = milliseconds;
                smoothTime = 0;
                step = Step();
            }
            else if (quality == Quality.Smooth)
            {
                smoothTime = milliseconds;
                step = Step();
            }

            var frame = buffer;
            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (!session.Stopped) Present(frame, w, h);
                }, DispatcherPriority.Render);
            }
            catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException or InvalidOperationException)
            {
                // The application is shutting down.
                return;
            }
            drawnVersion = version;
            drawn = quality;
        }
    }

    private int CurrentVersion
    {
        get
        {
            lock (_gate) return _version;
        }
    }

    private void Present(int[] frame, int width, int height)
    {
        if (_renderer is null) return;
        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            var other = _spare is { } spare && spare.PixelWidth == width && spare.PixelHeight == height
                ? spare
                : new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
            _spare = _bitmap;
            _bitmap = other;
            InvalidateVisual();
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), frame, width * 4, 0);
        FramePresented?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var area = new Rect(RenderSize);
        if (_bitmap is { } bitmap) dc.DrawImage(bitmap, area);
        else dc.DrawRectangle(new SolidColorBrush(ToColor(_top)), null, area);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);

    protected override Size ArrangeOverride(Size finalSize)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Round(finalSize.Width * dpi.DpiScaleX);
        var height = (int)Math.Round(finalSize.Height * dpi.DpiScaleY);
        lock (_gate)
        {
            if (width != _pixelWidth || height != _pixelHeight)
            {
                _pixelWidth = width;
                _pixelHeight = height;
                _version++;
            }
        }
        _session?.Wake.Set();
        return finalSize;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateArrange();
    }

    private void OnPaletteChanged(object? sender, EventArgs e) => UpdateBackground();

    /// <summary>A soft gradient behind the model, light or dark with the theme.</summary>
    private void UpdateBackground()
    {
        var dark = ThemeService.Current?.Theme == AppTheme.Dark;
        lock (_gate)
        {
            _top = dark ? 0x4B4E54u : 0xF4F5F7u;
            _bottom = dark ? 0x232528u : 0xCDD0D4u;
            _version++;
        }
        _session?.Wake.Set();
        InvalidateVisual();
    }

    private static Color ToColor(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    // ===================================================================== pointer

    private (double Width, double Height) Pixels
    {
        get
        {
            lock (_gate) return (_pixelWidth, _pixelHeight);
        }
    }

    private double Dpi => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>The point of the model under a point of the view, if any.</summary>
    private Vec3? ModelPointAt(Point point)
    {
        if (_renderer is not { } renderer) return null;
        var (width, height) = Pixels;
        var camera = Camera;
        return renderer.Pick(camera.Eye, camera.Ray(point.X * Dpi, point.Y * Dpi, width, height));
    }

    private void Begin(Drag drag, Point point)
    {
        if (_renderer is null) return;
        Focus();
        StopAnimation();
        _drag = drag;
        _last = point;
        if (drag == Drag.Turn && ModelPointAt(point) is { } hit) _pivot = hit;
        CaptureMouse();
        Interacted?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        e.Handled = true;
        var point = e.GetPosition(this);
        if (e.ChangedButton == MouseButton.Left)
            Begin(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? Drag.Move : Drag.Turn, point);
        else if (e.ChangedButton is MouseButton.Right or MouseButton.Middle)
            Begin(Drag.Move, point);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag == Drag.None) return;
        var point = e.GetPosition(this);
        var delta = point - _last;
        _last = point;
        if (_drag == Drag.Turn) Turn(delta.X, delta.Y);
        else Move(delta.X, delta.Y);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag == Drag.None) return;
        e.Handled = true;
        _drag = Drag.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _drag = Drag.None;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        e.Handled = true;
        Zoom(e.Delta / 120.0, e.GetPosition(this));
    }

    /// <summary>Turns the model by a drag of <paramref name="dx"/>, <paramref name="dy"/> device-independent pixels.</summary>
    internal void Turn(double dx, double dy)
    {
        if (_renderer is null) return;
        SetCamera(Camera.Orbit(_pivot, _worldUp, -dx * TurnPerPixel, -dy * TurnPerPixel));
    }

    /// <summary>Moves the model with a drag, keeping the turning point where it is under the pointer.</summary>
    internal void Move(double dx, double dy)
    {
        if (_renderer is not { } renderer) return;
        var camera = Camera;
        var (width, height) = Pixels;
        var depth = Math.Max(renderer.Scene.Radius * 1e-3, Vec3.Dot(_pivot - camera.Eye, camera.Forward));
        var moved = camera.Pan(dx * Dpi, dy * Dpi, depth, width, height);
        _pivot += moved.Eye - camera.Eye;
        SetCamera(moved);
    }

    /// <summary>Zooms toward the point of the model under <paramref name="at"/> - or toward the turning point's depth there - by <paramref name="notches"/> wheel notches.</summary>
    internal void Zoom(double notches, Point at)
    {
        if (_renderer is not { } renderer || notches == 0) return;
        StopAnimation();
        Interacted?.Invoke(this, EventArgs.Empty);
        var camera = Camera;
        var (width, height) = Pixels;
        var target = ModelPointAt(at);
        if (target is null)
        {
            var ray = camera.Ray(at.X * Dpi, at.Y * Dpi, width, height);
            var depth = Math.Max(renderer.Scene.Radius * 0.05, Vec3.Dot(_pivot - camera.Eye, camera.Forward));
            target = camera.Eye + (ray * (depth / Math.Max(0.05, Vec3.Dot(ray, camera.Forward))));
        }
        SetCamera(camera.Dolly(target.Value, Math.Pow(0.8, notches)));
    }

    // ===================================================================== keys

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && HandleKey(e.Key, Keyboard.Modifiers)) e.Handled = true;
    }

    /// <summary>The model's keys. Returns whether the key was one.</summary>
    internal bool HandleKey(Key key, ModifierKeys modifiers)
    {
        if (_renderer is null && key != Key.Escape) return false;
        var middle = new Point(RenderSize.Width / 2, RenderSize.Height / 2);
        var ctrl = modifiers.HasFlag(ModifierKeys.Control);
        const double step = Math.PI / 12 / TurnPerPixel;
        switch (key)
        {
            case Key.Escape:
                CloseRequested?.Invoke(this, EventArgs.Empty);
                break;
            case Key.Home:
                GoHome();
                break;
            case Key.Add or Key.OemPlus when !ctrl:
                Zoom(1, middle);
                break;
            case Key.Subtract or Key.OemMinus when !ctrl:
                Zoom(-1, middle);
                break;
            case Key.Left:
                Turn(-step, 0);
                break;
            case Key.Right:
                Turn(step, 0);
                break;
            case Key.Up:
                Turn(0, -step);
                break;
            case Key.Down:
                Turn(0, step);
                break;
            default:
                return false;
        }
        return true;
    }

    // ===================================================================== touch

    protected override void OnManipulationStarting(ManipulationStartingEventArgs e)
    {
        base.OnManipulationStarting(e);
        e.ManipulationContainer = this;
        e.Mode = ManipulationModes.All;
        StopAnimation();
        if (ModelPointAt(e.Manipulators.FirstOrDefault()?.GetPosition(this) ?? default) is { } hit) _pivot = hit;
        e.Handled = true;
    }

    protected override void OnManipulationDelta(ManipulationDeltaEventArgs e)
    {
        base.OnManipulationDelta(e);
        e.Handled = true;
        var delta = e.DeltaManipulation;
        // One finger turns; two move and pinch.
        if (e.Manipulators.Count() >= 2)
        {
            Move(delta.Translation.X, delta.Translation.Y);
            if (Math.Abs(delta.Scale.X - 1) > 0.001) Zoom(Math.Log(delta.Scale.X) / Math.Log(1 / 0.8), e.ManipulationOrigin);
        }
        else
        {
            Turn(delta.Translation.X, delta.Translation.Y);
        }
    }

    protected override void OnManipulationInertiaStarting(ManipulationInertiaStartingEventArgs e)
    {
        base.OnManipulationInertiaStarting(e);
        e.TranslationBehavior.DesiredDeceleration = 10.0 * 96.0 / (1000.0 * 1000.0);
        e.ExpansionBehavior.DesiredDeceleration = 0.1 * 96 / (1000.0 * 1000.0);
        e.Handled = true;
    }
}
