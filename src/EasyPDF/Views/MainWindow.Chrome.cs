using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using EasyPDF.Services;
using Point = System.Windows.Point;

namespace EasyPDF.Views;

/// <summary>
/// The window without a Windows title bar. WindowChrome turns the header strip into the caption;
/// this adds what it leaves out:
///
///   - the window buttons, which are ordinary buttons in the header;
///   - snap layouts on Windows 11, which appear only over something Windows believes is a
///     maximize button - so the header's button answers the hit test as one, and its hover,
///     press and click are handled here because Windows now owns its mouse input;
///   - a maximized window that fits the screen, and full screen, which covers it;
///   - the horizontal wheel (WM_MOUSEHWHEEL), which WPF does not route at all.
/// </summary>
public partial class MainWindow
{
    private const int WmNcHitTest = 0x0084;
    private const int WmNcMouseMove = 0x00A0;
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmNcLButtonUp = 0x00A2;
    private const int WmNcMouseLeave = 0x02A2;
    private const int WmMouseHWheel = 0x020E;
    private const int HtMaxButton = 9;

    private const int SmCxFrame = 32;
    private const int SmCyFrame = 33;
    private const int SmCxPaddedBorder = 92;

    /// <summary>The strip the header's controls are centred in, and the line under it.</summary>
    private const double HeaderStripHeight = 42;
    private const double HeaderDividerHeight = 1;

    private bool _maximizePressed;
    private bool _trackingNcLeave;

    private bool _fullScreen;
    private Rect _restoreBounds;
    private WindowState _restoreState;
    private bool _restoreSidebar;

    public bool IsFullScreen => _fullScreen;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        try
        {
            if (PresentationSource.FromVisual(this) is HwndSource source)
            {
                source.AddHook(ChromeHook);
                // WindowChrome takes the frame away only later, so the first layout would be done
                // for a client area with a frame and done again without it - rendering the first
                // screen twice. Asking Windows to recalculate the frame now gets the final client
                // area before anything is laid out.
                SetWindowPos(source.Handle, IntPtr.Zero, 0, 0, 0, 0,
                    SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoOwnerZOrder | SwpNoActivate | SwpFrameChanged);
            }
            FitFrame();
        }
        catch (Exception ex)
        {
            AppLog.Warn("The window caption could not be set up.", ex);
        }
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (_fullScreen && WindowState == WindowState.Minimized) return;
        if (_fullScreen && WindowState == WindowState.Maximized) ExitFullScreen();
        FitFrame();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        FitFrame();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    /// <summary>Reached from the keyboard or UI Automation; a mouse click arrives as WM_NCLBUTTONUP.</summary>
    private void OnMaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximized();

    private void OnCloseClick(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void ToggleMaximized()
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    /// <summary>
    /// Fits the content to what Windows draws round it: pulls it in by the frame Windows adds
    /// round a maximized window, and moves the header's controls clear of the border Windows 11
    /// draws over the top row of a window that is not - so they sit centred in the strip that is
    /// in sight, whichever it is.
    /// </summary>
    private void FitFrame()
    {
        try
        {
            if (Content is not FrameworkElement root) return;

            var scale = VisualTreeHelper.GetDpi(this);
            var inset = new Thickness(0);
            if (WindowState == WindowState.Maximized && !_fullScreen)
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    var dpi = GetDpiForWindow(hwnd);
                    var padded = GetSystemMetricsForDpi(SmCxPaddedBorder, dpi);
                    var x = (GetSystemMetricsForDpi(SmCxFrame, dpi) + padded) / scale.DpiScaleX;
                    var y = (GetSystemMetricsForDpi(SmCyFrame, dpi) + padded) / scale.DpiScaleY;
                    inset = new Thickness(x, y, x, y);
                }
            }

            // One device pixel, at any scale.
            var border = WindowTitleBar.BorderCoversEdge && WindowState == WindowState.Normal ? 1 / scale.DpiScaleY : 0;
            var header = _fullScreen ? 0 : border + HeaderStripHeight + HeaderDividerHeight;
            Header.Padding = new Thickness(0, border, 0, 0);
            HeaderRow.Height = new GridLength(header);

            root.Margin = inset;
            if (WindowChrome.GetWindowChrome(this) is { } chrome)
                chrome.CaptionHeight = _fullScreen ? 0 : header + inset.Top;
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"Fitting the window to its frame failed: {ex.Message}");
        }
    }

    // ===================================================================== full screen

    public void ToggleFullScreen()
    {
        if (_fullScreen) ExitFullScreen();
        else EnterFullScreen();
    }

    private void EnterFullScreen()
    {
        if (_fullScreen) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var monitor = MonitorFromWindow(hwnd, 2 /* nearest */);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        _restoreState = WindowState;
        _restoreBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _restoreSidebar = IsSidebarOpen;
        _fullScreen = true;

        Header.Visibility = Visibility.Collapsed;
        SetSidebar(false, remember: false);
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Normal;
        FitFrame();

        // The monitor in device pixels, placed in WPF's units for this window.
        var scale = VisualTreeHelper.GetDpi(this);
        Left = info.Monitor.Left / scale.DpiScaleX;
        Top = info.Monitor.Top / scale.DpiScaleY;
        Width = (info.Monitor.Right - info.Monitor.Left) / scale.DpiScaleX;
        Height = (info.Monitor.Bottom - info.Monitor.Top) / scale.DpiScaleY;
        View.Focus();
    }

    private void ExitFullScreen()
    {
        if (!_fullScreen) return;
        _fullScreen = false;

        Header.Visibility = Visibility.Visible;
        ResizeMode = ResizeMode.CanResize;
        if (_restoreBounds.Width > 0)
        {
            Left = _restoreBounds.Left;
            Top = _restoreBounds.Top;
            Width = _restoreBounds.Width;
            Height = _restoreBounds.Height;
        }
        if (_restoreState == WindowState.Maximized) WindowState = WindowState.Maximized;
        SetSidebar(_restoreSidebar && HasDocument, remember: false);
        FitFrame();
    }

    // ===================================================================== messages

    private IntPtr ChromeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmNcHitTest:
                if (!_fullScreen && IsOverMaximizeButton(lParam))
                {
                    handled = true;
                    return new IntPtr(HtMaxButton);
                }
                break;

            case WmNcMouseMove:
                if (wParam.ToInt32() == HtMaxButton)
                {
                    TrackNcLeave(hwnd);
                    SetMaximizeVisual(_maximizePressed ? "pressed" : "hover");
                }
                else
                {
                    SetMaximizeVisual(null);
                }
                break;

            case WmNcMouseLeave:
                _trackingNcLeave = false;
                _maximizePressed = false;
                SetMaximizeVisual(null);
                break;

            case WmNcLButtonDown when wParam.ToInt32() == HtMaxButton:
                handled = true;
                _maximizePressed = true;
                SetMaximizeVisual("pressed");
                return IntPtr.Zero;

            case WmNcLButtonUp when wParam.ToInt32() == HtMaxButton:
                handled = true;
                if (_maximizePressed)
                {
                    _maximizePressed = false;
                    SetMaximizeVisual(null);
                    ToggleMaximized();
                }
                return IntPtr.Zero;

            case WmMouseHWheel:
                if (View.IsMouseOver)
                {
                    var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                    View.OnHorizontalWheel(delta);
                    handled = true;
                    return new IntPtr(1);
                }
                break;
        }

        return IntPtr.Zero;
    }

    private bool IsOverMaximizeButton(IntPtr lParam)
    {
        if (MaximizeButton is not { IsVisible: true } button) return false;

        // Screen coordinates, signed: a second monitor to the left is negative.
        var raw = lParam.ToInt64();
        var screen = new Point((short)(raw & 0xFFFF), (short)((raw >> 16) & 0xFFFF));

        try
        {
            var local = button.PointFromScreen(screen);
            return local.X >= 0 && local.Y >= 0 && local.X < button.ActualWidth && local.Y < button.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void SetMaximizeVisual(string? state)
    {
        if (MaximizeButton is { } button && !Equals(button.Tag, state)) button.Tag = state;
    }

    /// <summary>WM_NCMOUSELEAVE only arrives when asked for, and it is what clears the hover.</summary>
    private void TrackNcLeave(IntPtr hwnd)
    {
        if (_trackingNcLeave) return;

        var track = new TrackMouseEventInfo
        {
            Size = (uint)Marshal.SizeOf<TrackMouseEventInfo>(),
            Flags = TmeLeave | TmeNonClient,
            Window = hwnd,
        };
        _trackingNcLeave = TrackMouseEvent(ref track);
    }

    private const uint TmeLeave = 0x00000002;
    private const uint TmeNonClient = 0x00000010;

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEventInfo
    {
        public uint Size;
        public uint Flags;
        public IntPtr Window;
        public uint HoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpNoOwnerZOrder = 0x0200;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool TrackMouseEvent(ref TrackMouseEventInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
