using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using EasyPDF.Pdf;
using EasyPDF.Resources;
using EasyPDF.Viewer;
using EasyPDF.Views;
using static Program;
using Point = System.Windows.Point;

/// <summary>3D models: found on their pages, read with their views, drawn, and shown over the pages.</summary>
internal static class ModelChecks
{
    public static IEnumerable<(string, Action)> All() =>
    [
        ("A 3D annotation is a link that shows its model; a link over it wins; other pages have none", ModelLinks),
        ("A 3D annotation's model and views are read from the file, the one it starts with first", ModelViewsRead),
        ("A model that cannot be read, or is U3D, says so; Esc and another page close the 3D view", ModelMessages),
        ("The renderer lights faces, darkens edges, blends see-through parts, with no seams", RendererDraws),
        ("The renderer clips what is behind the camera and finds the point under a ray", RendererClipsAndPicks),
        ("The camera turns with the horizon level, zooms toward a point and goes between views", CameraMoves),
        ("The 3D view draws a model, turns and zooms it, goes to a view and back home", ModelViewInteracts),
    ];

    private const uint Top = 0xF0F0F0, Bottom = 0x808080;

    private static MainWindow ShowWindow(string fixture)
    {
        var window = App.OpenEmptyWindow(null);
        Check(Await(window.OpenAsync(Fixture(fixture))), $"{fixture} opens");
        PumpUntil(() => window.PageView.IsScreenComplete() && !window.PageView.Tiles.IsBusy, "the screen to render", 20000);
        return window;
    }

    private static Point At(MainWindow window, int page, double x, double y) => window.PageView.ViewPointOf(page, x / 612, y / 792);

    private static void Click(MainWindow window, Point point)
    {
        var view = window.PageView;
        view.PointerMove(point);
        view.PointerDown(point, 1, shift: false);
        view.PointerUp(point);
        Pump(10);
    }

    private static PageInfo Info(MainWindow window, int page)
    {
        window.PageView.RequestPageInfo(page, JobPriority.Interactive);
        PumpUntil(() => window.PageView.InfoFor(page) is { MoreLinksPending: null }, $"page {page + 1}'s links");
        return window.PageView.InfoFor(page)!;
    }

    // ===================================================================== on the page

    private static void ModelLinks()
    {
        var window = ShowWindow("model.pdf");
        var info = Info(window, 0);
        var link = info.Links.Single(l => l.Target is ModelTarget);
        Equal(((ModelTarget)link.Target).PageIndex, 0, "the model's page");
        Near(link.Bounds.X0, 72 / 612.0, 0.002, "the model's left");
        Near(link.Bounds.Y0, 120 / 792.0, 0.002, "its top");
        Near(link.Bounds.X1, 540 / 612.0, 0.002, "its right");
        Near(link.Bounds.Y1, 600 / 792.0, 0.002, "its bottom");
        Check(info.LinkAt(175 / 612.0, 160 / 792.0)?.Target is UriTarget, "the web link over the model wins");
        Check(info.LinkAt(300 / 612.0, 400 / 792.0)?.Target is ModelTarget, "the model elsewhere in its area");
        Check(Info(window, 1).Links.Any(l => l.Target is ModelTarget), "a U3D model is a link too");
        Check(!Info(window, 2).Links.Any(l => l.Target is ModelTarget), "a page without a 3D annotation has none");

        var view = window.PageView;
        view.PointerMove(At(window, 0, 300, 400));
        Equal(window.StatusText.Text, Strings.View_LinkModel, "the status says what a click does");
        Equal(view.Cursor, Cursors.Hand, "a hand over the model");
        var menu = view.BuildContextMenu(new ModelTarget(0, 0));
        Check(menu.Items.OfType<System.Windows.Controls.MenuItem>().Any(i => (string)i.Header == Strings.View_ShowModel), "the context menu shows the model");
        Check(!menu.Items.OfType<System.Windows.Controls.MenuItem>().Any(i => (string)i.Header == Strings.View_CopyLink), "and has no address to copy");
    }

    private static void ModelViewsRead()
    {
        using var source = FileSource.Open(Fixture("model.pdf"));
        var reader = PdfObjectReader.Open(source)!;
        var annotations = reader.ThreeDAnnotations(0);
        Equal(annotations.Count, 1, "one 3D annotation on page 1");
        var model = reader.ThreeDModel(0, annotations[0], 1 << 20)!;
        Equal(model.Kind, "PRC", "its kind");
        Equal(Encoding.ASCII.GetString(model.Data), "not a PRC model", "its stream, decoded");
        Equal(string.Join("|", model.Views.Select(v => v.Name)), "=A1+B2-K1|Front", "its views, the one the annotation starts with first");
        Equal(model.Views[0].FieldOfView, 45.0, "a view's own field of view");
        Equal(model.Views[1].FieldOfView, 30.0, "a view without a projection: 30 degrees");
        Equal(string.Join(" ", model.Views[1].CameraToWorld), "1 0 0 0 0 1 0 1 0 0 -500 0", "a view's camera");
        Near(model.Aspect, 480 / 468.0, 1e-9, "the annotation's height over its width");
        Check(reader.ThreeDModel(0, annotations[0] + 1, 1 << 20) is null, "the link beside it is no model");

        var u3d = reader.ThreeDModel(1, reader.ThreeDAnnotations(1).Single(), 1 << 20)!;
        Equal(u3d.Kind, "U3D", "the second page's kind");
        Equal(u3d.Views.Single().Name, "Default", "with no view named to start with, the first");
        Equal(reader.ThreeDAnnotations(2).Count, 0, "none on the third page");
    }

    private static void ModelMessages()
    {
        var window = ShowWindow("model.pdf");
        Info(window, 0);
        Click(window, At(window, 0, 300, 400));
        PumpUntil(() => window.IsModelShown && window.ModelPanel?.Message is { } m && m != Strings.Model_Reading, "the model to be read");
        var panel = window.ModelPanel!;
        Equal(panel.Message, Strings.Model_Failed, "a stream that is no PRC model");
        Check(panel.Visibility == Visibility.Visible, "the 3D view is over the pages");
        Check(panel.View.HandleKey(Key.Escape, ModifierKeys.None), "Esc is the 3D view's");
        Check(!window.IsModelShown && panel.Visibility != Visibility.Visible, "Esc closes it");

        var view = window.PageView;
        view.GoToPage(1);
        PumpUntil(() => view.IsScreenComplete(), "page 2");
        Info(window, 1);
        Click(window, At(window, 1, 300, 400));
        PumpUntil(() => window.ModelPanel?.Message is { } m && m != Strings.Model_Reading, "the U3D model");
        Equal(panel.Message, Strings.Model_U3D, "U3D is not shown yet, and says so");
        view.GoToPage(2);
        PumpUntil(() => !window.IsModelShown, "going to another page to close the 3D view");

        // The link over the model is followed, not the model shown.
        string? opened = null;
        MainWindow.ShellOpen = target =>
        {
            opened = target;
            return true;
        };
        view.GoToPage(0);
        PumpUntil(() => view.IsScreenComplete(), "page 1");
        Click(window, At(window, 0, 175, 160));
        PumpUntil(() => opened is not null, "the web link");
        Equal(opened, "https://example.com/over-the-model", "the link followed");
        Pump(20);
        Check(!window.IsModelShown, "and the model not shown");
    }

    // ===================================================================== drawing

    /// <summary>A cube one unit across round the origin - its faces <paramref name="color"/>, its twelve edges as lines - and maybe a see-through square in front of it.</summary>
    private static ModelScene Cube(uint color = 0xFFCC2020, uint? glass = null)
    {
        float[] corners = [-0.5f, -0.5f, -0.5f, 0.5f, -0.5f, -0.5f, 0.5f, 0.5f, -0.5f, -0.5f, 0.5f, -0.5f,
                           -0.5f, -0.5f, 0.5f, 0.5f, -0.5f, 0.5f, 0.5f, 0.5f, 0.5f, -0.5f, 0.5f, 0.5f];
        int[] faces = [0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6, 0, 5, 1, 0, 4, 5, 3, 2, 6, 3, 6, 7, 0, 3, 7, 0, 7, 4, 1, 5, 6, 1, 6, 2];
        int[] edges = [0, 1, 1, 2, 2, 3, 3, 0, 4, 5, 5, 6, 6, 7, 7, 4, 0, 4, 1, 5, 2, 6, 3, 7];
        var points = corners.ToList();
        var triangles = faces.ToList();
        var colors = Enumerable.Repeat(color, 12).ToList();
        if (glass is { } g)
        {
            // A square two units across at y = -2, facing the camera.
            var first = points.Count / 3;
            points.AddRange([-1, -2, -1, 1, -2, -1, 1, -2, 1, -1, -2, 1]);
            triangles.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
            colors.AddRange([g, g]);
        }
        return new ModelScene
        {
            Points = [.. points],
            Triangles = [.. triangles],
            OpaqueTriangles = 12,
            Colors = [.. colors],
            LinePoints = corners,
            Lines = edges,
            Min = glass is null ? new Vec3(-0.5, -0.5, -0.5) : new Vec3(-1, -2, -1),
            Max = glass is null ? new Vec3(0.5, 0.5, 0.5) : new Vec3(1, 0.5, 1),
        };
    }

    /// <summary>From five units in front, looking at the cube's front face, Z up.</summary>
    private static ModelCamera Front => new(new Vec3(0, -5, 0), new Vec3(1, 0, 0), new Vec3(0, 0, 1), new Vec3(0, 1, 0), 30);

    private static int[] Draw(ModelRenderer renderer, ModelCamera camera, int samples = 1, int size = 200)
    {
        var pixels = new int[size * size];
        Check(renderer.Render(camera, size, size, samples, Top, Bottom, pixels), "the frame is drawn");
        return pixels;
    }

    private static (int R, int G, int B) Rgb(int pixel) => ((pixel >> 16) & 0xFF, (pixel >> 8) & 0xFF, pixel & 0xFF);

    private static void RendererDraws()
    {
        var renderer = new ModelRenderer(Cube());
        var pixels = Draw(renderer, Front);
        Equal(pixels[0] & 0xFFFFFF, (int)Top, "the gradient's top in the corner");
        Equal(pixels[199 * 200] & 0xFFFFFF, (int)Bottom, "its bottom at the foot");

        var middle = Rgb(pixels[(100 * 200) + 100]);
        Check(middle.R > 150 && middle.G < 60 && middle.B < 60, $"the face is red, lit: {middle}");
        Check(middle.R < 0xCC + 1, "no brighter than its colour");

        // The face is flat: one colour all over its middle, across the bands' joins (every 32 rows).
        for (var y = 60; y < 140; y++)
            for (var x = 70; x < 130; x++)
                if (pixels[(y * 200) + x] != pixels[(100 * 200) + 100]) throw new InvalidOperationException($"the face differs at {x},{y}");

        // Its outline is an edge line: darker than the face beside it. The face is 0.5 / 4.5 of the
        // focal length (100 / tan 15 degrees) from the middle.
        var row = Enumerable.Range(0, 100).Select(x => Rgb(pixels[(100 * 200) + x])).ToList();
        var edge = row.FindIndex(c => c != row[0]);
        Near(edge, 100 - (100 / Math.Tan(Math.PI / 12) / 9), 1.5, "the cube's left edge");
        Check(row[edge].R < middle.R / 2, $"the edge is dark: {row[edge]} beside {middle}");

        Check(Draw(renderer, Front).SequenceEqual(pixels), "the same frame twice is the same picture");
        var smooth = Draw(renderer, Front, samples: 2);
        Equal(smooth[(100 * 200) + 100], pixels[(100 * 200) + 100], "four samples a pixel: the face the same");

        // A see-through square in front: the face shows through, tinted, with no seam on the
        // square's diagonal where its two triangles meet.
        var glass = Draw(new ModelRenderer(Cube(glass: 0x802040E0)), Front);
        var tinted = Rgb(glass[(100 * 200) + 100]);
        Check(tinted.R > 60 && tinted.R < middle.R && tinted.B > 60, $"red through blue glass: {tinted}");
        for (var y = 70; y < 130; y++)
            for (var x = 70; x < 130; x++)
                if (glass[(y * 200) + x] != glass[(100 * 200) + 100]) throw new InvalidOperationException($"the glass differs at {x},{y}: a seam");
    }

    private static void RendererClipsAndPicks()
    {
        var renderer = new ModelRenderer(Cube());

        // From inside the cube, just under its top: its far face ahead, and its top - which runs
        // back past the camera - cut at the near plane.
        var inside = Draw(renderer, Front with { Eye = new Vec3(0, -0.2, 0.45) });
        var far = Rgb(inside[(100 * 200) + 100]);
        Check(far.R > 100 && far.G < 60, $"the far face ahead: {far}");
        var top = Rgb(inside[(5 * 200) + 100]);
        Check(top.R > 80 && top.G < 60 && top != far, $"the top face over it, less lit: {top}");

        // Turned away: nothing.
        var away = Draw(renderer, Front with { Forward = new Vec3(0, -1, 0), Right = new Vec3(-1, 0, 0) });
        Check(away.All(p => (p & 0xFF) == (p >> 16 & 0xFF)), "looking away, only the grey gradient");

        var hit = renderer.Pick(new Vec3(0, -5, 0), new Vec3(0, 1, 0));
        Check(hit is { } h && Math.Abs(h.Y + 0.5) < 1e-6 && Math.Abs(h.X) < 1e-6, $"the ray meets the front face: {hit}");
        Check(renderer.Pick(new Vec3(0, -5, 0), new Vec3(0, 0, 1)) is null, "a ray past it meets nothing");
        var glass = new ModelRenderer(Cube(glass: 0x802040E0));
        Check(glass.Pick(new Vec3(0, -5, 0), new Vec3(0, 1, 0)) is { } g && Math.Abs(g.Y + 0.5) < 1e-6, "an opaque face before glass in front of it");
        Check(glass.Pick(new Vec3(0.9, -5, 0.9), new Vec3(0, 1, 0)) is { } s && Math.Abs(s.Y + 2) < 1e-6, "glass where there is nothing else");
    }

    private static void CameraMoves()
    {
        var view = ModelCamera.FromView([.707107, .707107, 0, -.353553, .353553, .866025, -.612372, .612372, -.5, 2496.09, -2242.31, 2672.68], 30)!.Value;
        Near(view.Right.X, Math.Sqrt(0.5), 1e-5, "right is across");
        Near(view.Right.Z, 0, 1e-9, "and level");
        Near(Vec3.Dot(view.Up, view.Forward), 0, 1e-12, "up at right angles to the line of sight");
        Equal(ModelCamera.NearestAxis(view.Up), new Vec3(0, 0, 1), "Z is up");
        Equal(ModelCamera.NearestAxis(new Vec3(0.1, -1, 0.2)), new Vec3(0, -1, 0), "the nearest axis, either way");

        var (x, y, depth) = Front.Project(new Vec3(0, 2, 0), 300, 200);
        Near(x, 150, 1e-9, "a point ahead lands in the middle");
        Near(y, 100, 1e-9, "across and down");
        Near(depth, 7, 1e-9, "at its depth");
        var ray = Front.Ray(150, 100, 300, 200);
        Near(ray.Y, 1, 1e-12, "the middle's ray is the line of sight");
        var (x2, y2, _) = Front.Project(Front.Eye + (Front.Ray(40, 170, 300, 200) * 3), 300, 200);
        Near(x2, 40, 1e-9, "a ray through a point lands on it");
        Near(y2, 170, 1e-9, "up and down too");

        var up = new Vec3(0, 0, 1);
        // A view's field of view spans its frame's width; a frame taller than the picture is fitted by its height.
        var spread = 2 * Math.Tan(Math.PI / 12);
        Near(Front.Focal(300, 200), 200 / spread, 1e-9, "a square frame fits the picture's height");
        Near((Front with { Aspect = 0.5 }).Focal(300, 200), 300 / spread, 1e-9, "a wide one its width");
        Near((Front with { Aspect = 2 }).Focal(300, 200), 100 / spread, 1e-9, "a tall one its height, smaller");

        var turned = Front.Orbit(default, up, 0.7, 0.3);
        Near((turned.Eye - default(Vec3)).Length, 5, 1e-9, "turning keeps the distance to the point turned about");
        Near(turned.Right.Z, 0, 1e-12, "and the horizon level");
        Near(Vec3.Dot(turned.Forward, (default(Vec3) - turned.Eye).Unit(default)), 1, 1e-12, "and the point in the middle");
        var over = Front;
        for (var i = 0; i < 20; i++) over = over.Orbit(default, up, 0, 0.3);
        Check(Math.Abs(Vec3.Dot(over.Forward, up)) < 0.999, "pitching stops short of straight down");

        var closer = Front.Dolly(default, 0.5);
        Near(closer.Eye.Y, -2.5, 1e-12, "zooming halves the distance");
        var moved = Front.Pan(10, 0, 5, 300, 200);
        Check(moved.Eye.X < 0, "dragging right moves the camera left");

        var b = ModelCamera.FromView([0, 1, 0, 0, 0, 1, 1, 0, 0, -400, 0, 0], 45)!.Value;
        var start = ModelCamera.Between(Front, b, 0);
        var end = ModelCamera.Between(Front, b, 1);
        Near((start.Forward - Front.Forward).Length + (start.Up - Front.Up).Length, 0, 1e-9, "between at 0 is the first");
        Near((end.Forward - b.Forward).Length + (end.Right - b.Right).Length + (end.Eye - b.Eye).Length, 0, 1e-9, "between at 1 the second");
        var mid = ModelCamera.Between(Front, b, 0.5);
        Near(Vec3.Dot(mid.Right, mid.Up), 0, 1e-9, "halfway, the axes still square");
        Near(Vec3.Dot(Vec3.Cross(mid.Forward, mid.Up), mid.Right), 1, 1e-9, "and the same handedness");
        Near(mid.FieldOfView, 37.5, 1e-9, "the field of view halfway");
    }

    private static void ModelViewInteracts()
    {
        var window = ShowWindow("model.pdf");
        Info(window, 0);
        Click(window, At(window, 0, 300, 400));
        PumpUntil(() => window.ModelPanel?.Message == Strings.Model_Failed, "the 3D view");
        var panel = window.ModelPanel!;
        var side = ModelCamera.FromView([0, 1, 0, 0, 0, 1, 1, 0, 0, -6, 0, 0], 30)!.Value;
        panel.ShowModel(new ModelRenderer(Cube()), [("Front", Front), ("Side", side)], note: null);
        var view = panel.View;
        Equal(panel.Message, null, "no message over a model");
        Equal(string.Join(",", panel.ViewNames), "Front,Side", "its views listed");
        PumpUntil(() => view.Settled && view.Picture is not null, "the model to be drawn", 20000);

        var picture = view.Picture!;
        var pixels = new int[picture.PixelWidth * picture.PixelHeight];
        picture.CopyPixels(pixels, picture.PixelWidth * 4, 0);
        Equal((picture.PixelWidth, picture.PixelHeight), ((int)Math.Round(view.ActualWidth * window.PageView.Dpi), (int)Math.Round(view.ActualHeight * window.PageView.Dpi)), "drawn at the screen's resolution once still");
        var centre = Rgb(pixels[(picture.PixelHeight / 2 * picture.PixelWidth) + (picture.PixelWidth / 2)]);
        Check(centre.R > 150 && centre.G < 60, $"the cube in the middle: {centre}");

        view.Turn(100, 0);
        Check(view.Camera.Eye != Front.Eye, "dragging turns it");
        Near((view.Camera.Eye - default(Vec3)).Length, 5, 1e-6, "about the point of the cube under the pointer, or its middle");
        var before = (view.Camera.Eye - default(Vec3)).Length;
        view.Zoom(1, new Point(view.ActualWidth / 2, view.ActualHeight / 2));
        Check((view.Camera.Eye - default(Vec3)).Length < before, "the wheel zooms in");

        Check(view.HandleKey(Key.Home, ModifierKeys.None), "Home is the 3D view's");
        PumpUntil(() => (view.Camera.Eye - Front.Eye).Length < 1e-9, "Home to go back to the start");
        view.GoTo(side, animate: false);
        Equal(view.Camera, side, "a view at once");
        PumpUntil(() => view.Settled, "the side view to be drawn", 20000);

        window.CloseModel();
        Check(view.Picture is null, "closing lets go of the picture");
        Check(!window.IsModelShown, "and the 3D view is gone");
    }
}
