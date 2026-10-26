using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using EasyPDF.Services;
using EasyPDF.Views;

namespace EasyPDF;

/// <summary>
/// Threads started first thing in Main that get WPF ready while the UI thread is busy starting
/// the app, so that the first window is not the first to pay for it.
///
/// The render thread starts the composition engine: the render thread proper, the Direct3D
/// device, the connection to the desktop compositor. WPF starts the engine the first time a
/// thread touches a visual, and that thread waits the hundred-odd milliseconds it takes; at
/// start-up that would be the UI thread, in the middle of building the main window. The engine
/// is one per process, so this thread gets it going and the window finds it ready.
///
/// The other two do the one-off work behind what the main window is made of: the XAML loader
/// and the app's fonts on one; on the other the controls themselves - their static state (a
/// text box alone registers the whole editing command set), the system theme they are looked
/// up in - and the generic code the precompiled framework does not carry, which the JIT
/// compiles on first use. Each is done once per process by whichever thread comes first, and
/// these threads come first.
///
/// All are STA threads with dispatchers of their own, as a second UI thread would be. What they
/// make is thrown away, and the dispatchers are shut down once a window is rendering. None of
/// it may look a resource up: the app's resource dictionaries are not thread-safe, and the UI
/// thread is reading them all the while. A control is only ever constructed here - never
/// initialised, styled or measured, each of which looks its implicit style up in the app's
/// resources (and did corrupt them, once in a few starts).
/// </summary>
public static class StartupWarmup
{
    private static readonly ManualResetEventSlim Release = new(false);
    private static bool _started;

    public static void Start()
    {
        if (_started) return;
        _started = true;
        StartThread("EasyPDF.Warmup.Render", WarmRender);
        StartThread("EasyPDF.Warmup.Controls", WarmControls);
        StartThread("EasyPDF.Warmup.Xaml", WarmXamlAndFonts);
    }

    /// <summary>A window is rendering: the engine has a user of its own, and the threads can go.</summary>
    public static void Finish() => Release.Set();

    private static void StartThread(string name, Action work)
    {
        var thread = new Thread(() => Run(name, work), 512 * 1024) { IsBackground = true, Name = name };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static void Run(string name, Action work)
    {
        try
        {
            work();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"{name} failed; the first window does that work itself.", ex);
        }

        try
        {
            // Not before the first window has a media context of its own: the engine stays up.
            Release.Wait(TimeSpan.FromSeconds(30));
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"{name} could not shut its dispatcher down: {ex.Message}");
        }
    }

    private static void WarmRender()
    {
        // Asking for the render tier gives this thread a media context of its own, and the first
        // media context in the process starts the engine.
        _ = Dispatcher.CurrentDispatcher;
        StartupTrace.Mark("warm-dispatcher");
        _ = RenderCapability.Tier;
        StartupTrace.Mark("warm-render");
    }

    private static void WarmXamlAndFonts()
    {
        // The XAML loader and its table of known types, by loading the app's styles the way the
        // app is about to. (Application's static state comes with it: the pack scheme, MIME types.)
        var styles = (ResourceDictionary)Application.LoadComponent(new Uri("Themes/Controls.xaml", UriKind.Relative));
        StartupTrace.Mark("warm-xaml");

        // The app's font, in the weights the window uses: read from the font files, and shaped once.
        if (styles["AppFontFamily"] is FontFamily family)
        {
            foreach (var weight in new[] { FontWeights.Normal, FontWeights.SemiBold, FontWeights.Bold })
            {
                var typeface = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);
                var text = new FormattedText("EasyPDF 0123456789", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 13, Brushes.Black, 1.0);
                _ = text.WidthIncludingTrailingWhitespace;
            }
        }
        StartupTrace.Mark("warm-fonts");
    }

    private static void WarmControls()
    {
        // One of each control the window is made of, constructed and no more (see above).
        _ = new FrameworkElement[]
        {
            new TextBox(), new TextBlock(), new Button(), new RadioButton(), new RepeatButton(), new Thumb(),
            new ScrollBar(), new ScrollViewer(), new TreeView(), new TreeViewItem(), new ListBox(), new ListBoxItem(),
            new ItemsControl(), new ContextMenu(), new MenuItem(), new Separator(), new GridSplitter(),
            new ToggleButton(), new Popup(), new Border(), new Grid(), new StackPanel(), new ContentPresenter(),
            new ContentControl(), new Path(), new Ellipse(), new VirtualizingStackPanel(),
            new IconPath(), new AppIcon(), new ThumbnailImage(),
        };
        StartupTrace.Mark("warm-controls");

        // The system theme's dictionary, which WPF consults for every element's default style
        // and loads on the first lookup: a thousand keys to read, each naming a type to load.
        // A copy of our own, thrown away, leaves the lookup only the reading to do.
        try
        {
            _ = new ResourceDictionary { Source = new Uri("pack://application:,,,/PresentationFramework.Aero2;component/themes/aero2.normalcolor.xaml") };
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"The system theme could not be read ahead: {ex.Message}");
        }
        StartupTrace.Mark("warm-theme");
    }
}
