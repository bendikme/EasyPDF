using System.Collections.Concurrent;

namespace EasyPDF.Viewer;

/// <summary>
/// Draws a <see cref="ModelScene"/> on the processor, as a CAD viewer shows a model: flat-shaded
/// faces lit from the camera, see-through parts over the rest, and the edge lines darkened in.
///
/// The picture is drawn in bands of rows, all at once on the thread pool. Each band holds its own
/// colour and depth for the few rows it covers, so a frame needs little memory beyond the picture,
/// even drawn with four samples a pixel for smooth edges. The triangles and lines are sorted into
/// the bands they touch first, in their order, so each band only looks at its own.
/// </summary>
internal sealed class ModelRenderer
{
    /// <summary>The most picture rows a band, and the fewest; between them, as many bands as keep every processor busy.</summary>
    private const int MaxBandRows = 64;

    private const int MinBandRows = 8;

    /// <summary>Triangles or lines a piece of the sorting into bands.</summary>
    private const int Chunk = 16384;

    /// <summary>How much darker an edge line makes what it is drawn over.</summary>
    private const int LineShade = 90;

    /// <summary>How far behind a face, in proportion to its depth, a line on it may be and still be drawn.</summary>
    private const float LineBias = 1e-3f;

    private readonly ModelScene _scene;
    private readonly float _near;

    // Per frame: where each triangle corner and line end lands, in samples, and one over its
    // depth - which is what varies evenly across the picture; NaN behind the camera.
    private readonly float[] _px, _py, _pw;
    private readonly float[] _qx, _qy, _qw;

    // Per frame: each triangle's colour, lit; and the bands each triangle and line touches (first
    // << 16 | last), -1 for none.
    private readonly uint[] _lit;
    private readonly int[] _triangleBands;
    private readonly int[] _lineBands;

    private int[] _triangleBins = [];
    private int[] _lineBins = [];
    private readonly ConcurrentBag<Band> _bands = new();

    public ModelRenderer(ModelScene scene)
    {
        _scene = scene;
        _near = (float)(scene.Radius * 1e-4);
        var points = scene.Points.Length / 3;
        _px = new float[points];
        _py = new float[points];
        _pw = new float[points];
        var ends = scene.LinePoints.Length / 3;
        _qx = new float[ends];
        _qy = new float[ends];
        _qw = new float[ends];
        _lit = new uint[scene.TriangleCount];
        _triangleBands = new int[scene.TriangleCount];
        _lineBands = new int[scene.LineCount];
    }

    public ModelScene Scene => _scene;

    /// <summary>How long the last frame's steps took, in milliseconds: placing the points, sorting into bands, and drawing the bands.</summary>
    public (double Place, double Sort, double Draw) LastTimes { get; private set; }

    /// <summary>Lets go of the bands' buffers, until the next frame needs them again.</summary>
    public void Trim() => _bands.Clear();

    /// <summary>
    /// Draws the model as <paramref name="camera"/> sees it into <paramref name="output"/>,
    /// <paramref name="width"/> by <paramref name="height"/> pixels of 0xRRGGBB, over a gradient from
    /// <paramref name="top"/> to <paramref name="bottom"/>, with <paramref name="samples"/> by
    /// <paramref name="samples"/> samples a pixel. Returns false, the picture unfinished, when
    /// <paramref name="cancelled"/> says so between bands.
    /// </summary>
    public bool Render(ModelCamera camera, int width, int height, int samples, uint top, uint bottom, int[] output, Func<bool>? cancelled = null)
    {
        if (width <= 0 || height <= 0) return true;
        samples = Math.Clamp(samples, 1, 4);
        var rows = Math.Clamp(height / (Environment.ProcessorCount * 3), MinBandRows, MaxBandRows);
        var frame = new Frame(width * samples, height * samples, samples, rows * samples, camera);

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        Project(_scene.Points, _px, _py, _pw, frame);
        Project(_scene.LinePoints, _qx, _qy, _qw, frame);
        var placed = System.Diagnostics.Stopwatch.GetTimestamp();
        var triangleStarts = SortTriangles(frame, camera);
        var lineStarts = SortLines(frame);
        var sorted = System.Diagnostics.Stopwatch.GetTimestamp();
        if (cancelled?.Invoke() == true) return false;

        var stopped = false;
        Parallel.For(0, frame.BandCount, (band, state) =>
        {
            if (stopped || cancelled?.Invoke() == true)
            {
                stopped = true;
                state.Stop();
                return;
            }
            if (!_bands.TryTake(out var buffer) || buffer.Color.Length < frame.Width * frame.BandHeight) buffer = new Band(frame.Width * frame.BandHeight);
            try
            {
                DrawBand(band, buffer, frame, camera, triangleStarts, lineStarts, top, bottom, output, width);
            }
            finally
            {
                _bands.Add(buffer);
            }
        });
        LastTimes = (System.Diagnostics.Stopwatch.GetElapsedTime(started, placed).TotalMilliseconds,
            System.Diagnostics.Stopwatch.GetElapsedTime(placed, sorted).TotalMilliseconds,
            System.Diagnostics.Stopwatch.GetElapsedTime(sorted).TotalMilliseconds);
        return !stopped;
    }

    /// <summary>The nearest point of the model on a ray - of an opaque face before a see-through one - or null when the ray misses it.</summary>
    public Vec3? Pick(Vec3 origin, Vec3 direction)
    {
        var opaque = Nearest(0, _scene.OpaqueTriangles);
        var t = opaque < double.MaxValue ? opaque : Nearest(_scene.OpaqueTriangles, _scene.TriangleCount);
        return t < double.MaxValue ? origin + (direction * t) : null;

        double Nearest(int from, int to)
        {
            var best = double.MaxValue;
            var gate = new object();
            var points = _scene.Points;
            var tri = _scene.Triangles;
            Parallel.For(0, (to - from + Chunk - 1) / Chunk, chunk =>
            {
                var local = double.MaxValue;
                var end = Math.Min(to, from + ((chunk + 1) * Chunk));
                for (var i = from + (chunk * Chunk); i < end; i++)
                {
                    var a = Point(points, tri[i * 3]);
                    var e1 = Point(points, tri[(i * 3) + 1]) - a;
                    var e2 = Point(points, tri[(i * 3) + 2]) - a;
                    // Moeller and Trumbore: where the ray meets the triangle's plane, and whether inside it.
                    var p = Vec3.Cross(direction, e2);
                    var det = Vec3.Dot(e1, p);
                    if (Math.Abs(det) < 1e-12) continue;
                    var s = origin - a;
                    var u = Vec3.Dot(s, p) / det;
                    if (u is < 0 or > 1) continue;
                    var q = Vec3.Cross(s, e1);
                    var v = Vec3.Dot(direction, q) / det;
                    if (v < 0 || u + v > 1) continue;
                    var t = Vec3.Dot(e2, q) / det;
                    if (t > _near && t < local) local = t;
                }
                if (local < double.MaxValue)
                    lock (gate)
                        best = Math.Min(best, local);
            });
            return best;
        }
    }

    private static Vec3 Point(float[] points, int index) => new(points[index * 3], points[(index * 3) + 1], points[(index * 3) + 2]);

    // ===================================================================== per frame, before the bands

    /// <summary>The camera, as the frame draws with it: in samples, and in single precision.</summary>
    private readonly struct Frame
    {
        public Frame(int width, int height, int samples, int bandHeight, ModelCamera camera)
        {
            Width = width;
            Height = height;
            Samples = samples;
            BandHeight = bandHeight;
            BandCount = (height + bandHeight - 1) / bandHeight;
            Focal = (float)camera.Focal(width, height);
            Ex = (float)camera.Eye.X;
            Ey = (float)camera.Eye.Y;
            Ez = (float)camera.Eye.Z;
            Rx = (float)camera.Right.X;
            Ry = (float)camera.Right.Y;
            Rz = (float)camera.Right.Z;
            Ux = (float)camera.Up.X;
            Uy = (float)camera.Up.Y;
            Uz = (float)camera.Up.Z;
            Fx = (float)camera.Forward.X;
            Fy = (float)camera.Forward.Y;
            Fz = (float)camera.Forward.Z;
        }

        public readonly int Width, Height, Samples, BandHeight, BandCount;
        public readonly float Focal, Ex, Ey, Ez, Rx, Ry, Rz, Ux, Uy, Uz, Fx, Fy, Fz;
    }

    private void Project(float[] points, float[] xs, float[] ys, float[] ws, Frame f)
    {
        var count = points.Length / 3;
        var near = _near;
        float cx = f.Width / 2f, cy = f.Height / 2f;
        Parallel.For(0, (count + Chunk - 1) / Chunk, chunk =>
        {
            var end = Math.Min(count, (chunk + 1) * Chunk);
            for (var i = chunk * Chunk; i < end; i++)
            {
                float dx = points[i * 3] - f.Ex, dy = points[(i * 3) + 1] - f.Ey, dz = points[(i * 3) + 2] - f.Ez;
                var z = (dx * f.Fx) + (dy * f.Fy) + (dz * f.Fz);
                if (!(z >= near))
                {
                    ws[i] = float.NaN;
                    continue;
                }
                var w = 1 / z;
                xs[i] = cx + (f.Focal * ((dx * f.Rx) + (dy * f.Ry) + (dz * f.Rz)) * w);
                ys[i] = cy - (f.Focal * ((dx * f.Ux) + (dy * f.Uy) + (dz * f.Uz)) * w);
                ws[i] = w;
            }
        });
    }

    /// <summary>
    /// Lights each triangle, finds the bands it touches - none when it covers no sample - and sorts
    /// it into them. Returns where each band's list starts in <see cref="_triangleBins"/>.
    /// </summary>
    private int[] SortTriangles(Frame f, ModelCamera camera)
    {
        var tri = _scene.Triangles;
        var points = _scene.Points;
        var colors = _scene.Colors;
        var light = ((camera.Forward * -0.8) + (camera.Up * 0.4) + (camera.Right * 0.3)).Unit(-camera.Forward);
        float lx = (float)light.X, ly = (float)light.Y, lz = (float)light.Z;
        var lastBand = f.BandCount - 1;

        return Sort(_scene.TriangleCount, _triangleBands, ref _triangleBins, f.BandCount, i =>
        {
            int a = tri[i * 3], b = tri[(i * 3) + 1], c = tri[(i * 3) + 2];
            float wa = _pw[a], wb = _pw[b], wc = _pw[c];
            int bands;
            if (float.IsNaN(wa) || float.IsNaN(wb) || float.IsNaN(wc))
            {
                // Behind the camera, or partly: then clipped band by band.
                if (float.IsNaN(wa) && float.IsNaN(wb) && float.IsNaN(wc)) return -1;
                bands = lastBand;
            }
            else
            {
                float xa = _px[a], xb = _px[b], xc = _px[c], ya = _py[a], yb = _py[b], yc = _py[c];
                // The rows and columns of the samples whose centres it may cover.
                var rowFirst = (int)Math.Max(0, MathF.Ceiling(MathF.Min(ya, MathF.Min(yb, yc)) - 0.5f));
                var rowLast = (int)Math.Min(f.Height - 1, MathF.Floor(MathF.Max(ya, MathF.Max(yb, yc)) - 0.5f));
                var colFirst = (int)Math.Max(0, MathF.Ceiling(MathF.Min(xa, MathF.Min(xb, xc)) - 0.5f));
                var colLast = (int)Math.Min(f.Width - 1, MathF.Floor(MathF.Max(xa, MathF.Max(xb, xc)) - 0.5f));
                if (rowFirst > rowLast || colFirst > colLast) return -1;
                if (((xb - xa) * (yc - ya)) - ((xc - xa) * (yb - ya)) == 0) return -1;
                bands = ((rowFirst / f.BandHeight) << 16) | (rowLast / f.BandHeight);
            }

            // Lit from the camera's side, either face alike.
            float ax = points[a * 3], ay = points[(a * 3) + 1], az = points[(a * 3) + 2];
            float ux = points[b * 3] - ax, uy = points[(b * 3) + 1] - ay, uz = points[(b * 3) + 2] - az;
            float vx = points[c * 3] - ax, vy = points[(c * 3) + 1] - ay, vz = points[(c * 3) + 2] - az;
            float nx = (uy * vz) - (uz * vy), ny = (uz * vx) - (ux * vz), nz = (ux * vy) - (uy * vx);
            var length = MathF.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
            var facing = length > 0 ? MathF.Abs((nx * lx) + (ny * ly) + (nz * lz)) / length : 1;
            _lit[i] = Lit(colors[i], 0.35f + (0.65f * facing));
            return bands;
        });
    }

    private int[] SortLines(Frame f)
    {
        var lines = _scene.Lines;
        var lastBand = f.BandCount - 1;
        var thick = f.Samples - 1;
        return Sort(_scene.LineCount, _lineBands, ref _lineBins, f.BandCount, i =>
        {
            int a = lines[i * 2], b = lines[(i * 2) + 1];
            float wa = _qw[a], wb = _qw[b];
            if (float.IsNaN(wa) || float.IsNaN(wb)) return float.IsNaN(wa) && float.IsNaN(wb) ? -1 : lastBand;
            float xa = _qx[a], xb = _qx[b], ya = _qy[a], yb = _qy[b];
            if (MathF.Max(xa, xb) < 0 || MathF.Min(xa, xb) >= f.Width + thick) return -1;
            var rowFirst = (int)Math.Max(0, MathF.Floor(MathF.Min(ya, yb)));
            var rowLast = (int)Math.Min(f.Height - 1, MathF.Floor(MathF.Max(ya, yb)) + thick);
            if (rowFirst > rowLast) return -1;
            return ((rowFirst / f.BandHeight) << 16) | (rowLast / f.BandHeight);
        });
    }

    /// <summary>
    /// Sorts <paramref name="count"/> items into the bands <paramref name="bandsOf"/> gives each (first
    /// &lt;&lt; 16 | last, or -1), in their order: counted in pieces in parallel, then written out in
    /// parallel at each piece's place. Returns where each band's list starts, and its end.
    /// </summary>
    private static int[] Sort(int count, int[] bandsOf, ref int[] bins, int bandCount, Func<int, int> bands)
    {
        var chunks = (count + Chunk - 1) / Chunk;
        var counts = new int[chunks * bandCount];
        Parallel.For(0, chunks, chunk =>
        {
            var end = Math.Min(count, (chunk + 1) * Chunk);
            var row = chunk * bandCount;
            for (var i = chunk * Chunk; i < end; i++)
            {
                var b = bands(i);
                bandsOf[i] = b;
                if (b < 0) continue;
                for (var band = b >> 16; band <= (b & 0xFFFF); band++) counts[row + band]++;
            }
        });

        // Band by band, piece by piece: each piece's place in its band's list.
        var starts = new int[bandCount + 1];
        var total = 0;
        for (var band = 0; band < bandCount; band++)
        {
            starts[band] = total;
            for (var chunk = 0; chunk < chunks; chunk++)
            {
                var n = counts[(chunk * bandCount) + band];
                counts[(chunk * bandCount) + band] = total;
                total += n;
            }
        }
        starts[bandCount] = total;
        if (bins.Length < total) bins = new int[Math.Max(total, bins.Length + (bins.Length / 2))];

        var output = bins;
        Parallel.For(0, chunks, chunk =>
        {
            var end = Math.Min(count, (chunk + 1) * Chunk);
            var row = chunk * bandCount;
            for (var i = chunk * Chunk; i < end; i++)
            {
                var b = bandsOf[i];
                if (b < 0) continue;
                for (var band = b >> 16; band <= (b & 0xFFFF); band++) output[counts[row + band]++] = i;
            }
        });
        return starts;
    }

    private static uint Lit(uint color, float light)
    {
        var r = (uint)Math.Min(255, (((color >> 16) & 0xFF) * light) + 0.5f);
        var g = (uint)Math.Min(255, (((color >> 8) & 0xFF) * light) + 0.5f);
        var b = (uint)Math.Min(255, ((color & 0xFF) * light) + 0.5f);
        return (color & 0xFF000000) | (r << 16) | (g << 8) | b;
    }

    // ===================================================================== a band

    /// <summary>A band's samples: colour, one over depth (0 for nothing drawn), and whether an edge line darkened it yet.</summary>
    private sealed class Band(int size)
    {
        public readonly uint[] Color = new uint[size];
        public readonly float[] Depth = new float[size];
        public readonly byte[] Lined = new byte[size];
        public int Top;
        public int Bottom;
        public int Width;
    }

    private void DrawBand(int band, Band b, Frame f, ModelCamera camera, int[] triangleStarts, int[] lineStarts, uint top, uint bottom, int[] output, int outputWidth)
    {
        b.Top = band * f.BandHeight;
        b.Bottom = Math.Min(f.Height, b.Top + f.BandHeight);
        b.Width = f.Width;
        var rows = b.Bottom - b.Top;
        for (var r = 0; r < rows; r++)
        {
            var shade = Blend(top, bottom, f.Height > 1 ? (b.Top + r) * 255 / (f.Height - 1) : 0);
            b.Color.AsSpan(r * f.Width, f.Width).Fill(shade);
        }
        b.Depth.AsSpan(0, rows * f.Width).Clear();
        b.Lined.AsSpan(0, rows * f.Width).Clear();

        var opaque = _scene.OpaqueTriangles;
        var tri = _scene.Triangles;
        var colors = _scene.Colors;
        for (var k = triangleStarts[band]; k < triangleStarts[band + 1]; k++)
        {
            var i = _triangleBins[k];
            int a = tri[i * 3], c1 = tri[(i * 3) + 1], c2 = tri[(i * 3) + 2];
            var alpha = i < opaque ? 255 : (int)(colors[i] >> 24);
            if (float.IsNaN(_pw[a]) || float.IsNaN(_pw[c1]) || float.IsNaN(_pw[c2]))
            {
                FillClipped(b, f, camera, a, c1, c2, _lit[i], alpha);
                continue;
            }
            Fill(b, _px[a], _py[a], _pw[a], _px[c1], _py[c1], _pw[c1], _px[c2], _py[c2], _pw[c2], _lit[i], alpha);
        }

        var lines = _scene.Lines;
        for (var k = lineStarts[band]; k < lineStarts[band + 1]; k++)
        {
            var i = _lineBins[k];
            int a = lines[i * 2], e = lines[(i * 2) + 1];
            if (float.IsNaN(_qw[a]) || float.IsNaN(_qw[e]))
            {
                LineClipped(b, f, camera, a, e);
                continue;
            }
            Line(b, _qx[a], _qy[a], _qw[a], _qx[e], _qy[e], _qw[e], f.Samples);
        }

        // Into the picture: each pixel the mean of its samples.
        var s = f.Samples;
        for (var oy = b.Top / s; oy < (b.Bottom + s - 1) / s; oy++)
        {
            var row = output.AsSpan(oy * outputWidth, outputWidth);
            var first = ((oy * s) - b.Top) * f.Width;
            if (s == 1)
            {
                for (var x = 0; x < outputWidth; x++) row[x] = (int)(b.Color[first + x] | 0xFF000000);
                continue;
            }
            for (var x = 0; x < outputWidth; x++)
            {
                uint red = 0, green = 0, blue = 0;
                for (var sy = 0; sy < s; sy++)
                {
                    var at = first + (sy * f.Width) + (x * s);
                    for (var sx = 0; sx < s; sx++)
                    {
                        var c = b.Color[at + sx];
                        red += (c >> 16) & 0xFF;
                        green += (c >> 8) & 0xFF;
                        blue += c & 0xFF;
                    }
                }
                var n = (uint)(s * s);
                row[x] = (int)(0xFF000000 | ((red / n) << 16) | ((green / n) << 8) | (blue / n));
            }
        }
    }

    /// <summary>
    /// Fills a triangle's samples in the band, row by row: each row's run worked out from the three
    /// edges, a sample on an edge belonging to one side only (the top-left rule), so neighbouring
    /// triangles neither overlap nor leave gaps. Opaque, it is drawn where it is nearer than what is
    /// there; see-through, it is blended over that and leaves the depth as it was.
    /// </summary>
    private static void Fill(Band b, double x0, double y0, double w0, double x1, double y1, double w1, double x2, double y2, double w2, uint color, int alpha)
    {
        var area = ((x1 - x0) * (y2 - y0)) - ((x2 - x0) * (y1 - y0));
        if (!(Math.Abs(area) > 0) || !double.IsFinite(area)) return;
        if (area < 0)
        {
            (x1, x2) = (x2, x1);
            (y1, y2) = (y2, y1);
            (w1, w2) = (w2, w1);
            area = -area;
        }

        var rowFirst = (int)Math.Max(b.Top, Math.Ceiling(Math.Min(y0, Math.Min(y1, y2)) - 0.5));
        var rowLast = (int)Math.Min(b.Bottom - 1, Math.Floor(Math.Max(y0, Math.Max(y1, y2)) - 0.5));
        if (rowFirst > rowLast) return;
        var colFirst = (int)Math.Max(0, Math.Ceiling(Math.Min(x0, Math.Min(x1, x2)) - 0.5));
        var colLast = (int)Math.Min(b.Width - 1, Math.Floor(Math.Max(x0, Math.Max(x1, x2)) - 0.5));
        if (colFirst > colLast) return;

        // Edge a -> b is A x + B y + C, at least zero inside.
        var e0 = new Edge(x1, y1, x2, y2);
        var e1 = new Edge(x2, y2, x0, y0);
        var e2 = new Edge(x0, y0, x1, y1);

        // One over depth is a plane across the picture.
        var dx = (((w1 - w0) * (y2 - y0)) - ((w2 - w0) * (y1 - y0))) / area;
        var dy = (((w2 - w0) * (x1 - x0)) - ((w1 - w0) * (x2 - x0))) / area;

        var see = alpha < 255;
        uint cr = (color >> 16) & 0xFF, cg = (color >> 8) & 0xFF, cb = color & 0xFF;
        for (var r = rowFirst; r <= rowLast; r++)
        {
            var py = r + 0.5;
            int lo = colFirst, hi = colLast;
            if (!e0.Span(py, ref lo, ref hi) || !e1.Span(py, ref lo, ref hi) || !e2.Span(py, ref lo, ref hi) || lo > hi) continue;

            var d = w0 + (dx * (lo + 0.5 - x0)) + (dy * (py - y0));
            var at = ((r - b.Top) * b.Width) + lo;
            var depth = b.Depth.AsSpan(at, hi - lo + 1);
            var colors = b.Color.AsSpan(at, hi - lo + 1);
            if (!see)
            {
                for (var x = 0; x < depth.Length; x++, d += dx)
                {
                    var z = (float)d;
                    if (z <= depth[x]) continue;
                    depth[x] = z;
                    colors[x] = color;
                }
                continue;
            }
            for (var x = 0; x < depth.Length; x++, d += dx)
            {
                if ((float)d <= depth[x]) continue;
                var under = colors[x];
                uint ur = (under >> 16) & 0xFF, ug = (under >> 8) & 0xFF, ub = under & 0xFF;
                colors[x] = ((uint)(ur + ((((int)cr - (int)ur) * alpha) / 255)) << 16)
                    | ((uint)(ug + ((((int)cg - (int)ug) * alpha) / 255)) << 8)
                    | (uint)(ub + ((((int)cb - (int)ub) * alpha) / 255));
            }
        }
    }

    /// <summary>One edge of a triangle as a function across the picture, at least zero on the inside.</summary>
    private readonly struct Edge
    {
        private readonly double _a, _b, _c, _inverse;
        private readonly bool _inclusive;

        public Edge(double ax, double ay, double bx, double by)
        {
            _a = ay - by;
            _b = bx - ax;
            _c = (ax * by) - (bx * ay);
            _inverse = _a == 0 ? 0 : -1 / _a;
            // A left edge, or a flat top one, keeps the samples exactly on it.
            _inclusive = _a > 0 || (_a == 0 && _b > 0);
        }

        /// <summary>Narrows a row's run of columns (sample centres at x + 0.5) to this edge's inside; false when nothing is left.</summary>
        public bool Span(double py, ref int lo, ref int hi)
        {
            var k = (_b * py) + _c;
            if (_a == 0) return k > 0 || (k == 0 && _inclusive);
            var t = (k * _inverse) - 0.5;
            if (_a > 0)
            {
                var first = _inclusive ? Math.Ceiling(t) : Math.Floor(t) + 1;
                if (first > lo) lo = first > int.MaxValue ? int.MaxValue : (int)first;
            }
            else
            {
                var last = _inclusive ? Math.Floor(t) : Math.Ceiling(t) - 1;
                if (last < hi) hi = last < int.MinValue ? int.MinValue : (int)last;
            }
            return lo <= hi;
        }
    }

    /// <summary>
    /// An edge line through the band, one sample a step along its longer direction and as many
    /// thick as there are samples a pixel, so that it comes out a pixel wide. It darkens what is
    /// under it once, where it is not behind a face.
    /// </summary>
    private static void Line(Band b, double x0, double y0, double w0, double x1, double y1, double w1, int thickness)
    {
        // Most segments of a detailed model are this short: their ends are all there is to draw.
        double ax = Math.Floor(x0), ay = Math.Floor(y0), bx = Math.Floor(x1), by = Math.Floor(y1);
        if (Math.Abs(bx - ax) <= 1 && Math.Abs(by - ay) <= 1 && Math.Abs(ax) < b.Width + 2 && ay > b.Top - 2 && ay < b.Bottom + 2)
        {
            var across = Math.Abs(x1 - x0) >= Math.Abs(y1 - y0);
            Plot(b, (int)ax, (int)ay, (float)w0, thickness, across);
            if (bx != ax || by != ay) Plot(b, (int)bx, (int)by, (float)w1, thickness, across);
            return;
        }

        // Clipped to the band's rows (a thick line from just above may reach in) and the picture's columns.
        double t0 = 0, t1 = 1, dx = x1 - x0, dy = y1 - y0;
        if (!Clip(-dx, x0 + thickness, ref t0, ref t1) || !Clip(dx, b.Width - x0, ref t0, ref t1)
            || !Clip(-dy, y0 - (b.Top - thickness), ref t0, ref t1) || !Clip(dy, b.Bottom - y0, ref t0, ref t1)) return;

        var sx = x0 + (dx * t0);
        var sy = y0 + (dy * t0);
        var sw = w0 + ((w1 - w0) * t0);
        var span = t1 - t0;
        var steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy)) * span));
        var stepX = dx * span / steps;
        var stepY = dy * span / steps;
        var stepW = (w1 - w0) * span / steps;
        var along = Math.Abs(dx) >= Math.Abs(dy);
        for (var i = 0; i <= steps; i++, sx += stepX, sy += stepY, sw += stepW)
            Plot(b, (int)Math.Floor(sx), (int)Math.Floor(sy), (float)sw, thickness, along);

        static void Plot(Band b, int px, int py, float w, int thickness, bool across)
        {
            for (var k = 0; k < thickness; k++)
            {
                int x = across ? px : px + k, y = across ? py + k : py;
                if (x < 0 || x >= b.Width || y < b.Top || y >= b.Bottom) continue;
                var at = ((y - b.Top) * b.Width) + x;
                if (b.Lined[at] != 0 || w < b.Depth[at] * (1 - LineBias)) continue;
                b.Lined[at] = 1;
                var c = b.Color[at];
                b.Color[at] = ((((c >> 16) & 0xFF) * LineShade / 255) << 16) | ((((c >> 8) & 0xFF) * LineShade / 255) << 8) | ((c & 0xFF) * LineShade / 255);
            }
        }

        static bool Clip(double p, double q, ref double t0, ref double t1)
        {
            if (p == 0) return q > 0;
            var t = q / p;
            if (p < 0)
            {
                if (t > t1) return false;
                if (t > t0) t0 = t;
            }
            else
            {
                if (t < t0) return false;
                if (t < t1) t1 = t;
            }
            return true;
        }
    }

    // ===================================================================== through the camera's plane

    /// <summary>A point in the camera's frame: across, up, and ahead.</summary>
    private static (double X, double Y, double Z) InCamera(float[] points, int index, ModelCamera camera)
    {
        var d = Point(points, index) - camera.Eye;
        return (Vec3.Dot(d, camera.Right), Vec3.Dot(d, camera.Up), Vec3.Dot(d, camera.Forward));
    }

    private static (double X, double Y, double W) OnScreen((double X, double Y, double Z) p, Frame f)
    {
        var w = 1 / p.Z;
        return ((f.Width / 2.0) + (f.Focal * p.X * w), (f.Height / 2.0) - (f.Focal * p.Y * w), w);
    }

    /// <summary>A triangle partly behind the camera: the part ahead of its near plane, as one triangle or two.</summary>
    private void FillClipped(Band b, Frame f, ModelCamera camera, int a, int c1, int c2, uint color, int alpha)
    {
        Span<(double X, double Y, double Z)> corners = [InCamera(_scene.Points, a, camera), InCamera(_scene.Points, c1, camera), InCamera(_scene.Points, c2, camera)];
        Span<(double X, double Y, double W)> kept = stackalloc (double, double, double)[4];
        var n = 0;
        for (var i = 0; i < 3; i++)
        {
            var p = corners[i];
            var q = corners[(i + 1) % 3];
            var pIn = p.Z >= _near;
            if (pIn) kept[n++] = OnScreen(p, f);
            if (pIn != (q.Z >= _near))
            {
                var t = (_near - p.Z) / (q.Z - p.Z);
                kept[n++] = OnScreen((p.X + ((q.X - p.X) * t), p.Y + ((q.Y - p.Y) * t), _near), f);
            }
        }
        for (var i = 1; i + 1 < n; i++)
            Fill(b, kept[0].X, kept[0].Y, kept[0].W, kept[i].X, kept[i].Y, kept[i].W, kept[i + 1].X, kept[i + 1].Y, kept[i + 1].W, color, alpha);
    }

    /// <summary>A line partly behind the camera: the part ahead of its near plane.</summary>
    private void LineClipped(Band b, Frame f, ModelCamera camera, int a, int e)
    {
        var p = InCamera(_scene.LinePoints, a, camera);
        var q = InCamera(_scene.LinePoints, e, camera);
        if (p.Z < _near) (p, q) = (q, p);
        if (p.Z < _near) return;
        var t = (_near - p.Z) / (q.Z - p.Z);
        var cut = (p.X + ((q.X - p.X) * t), p.Y + ((q.Y - p.Y) * t), (double)_near);
        var s = OnScreen(p, f);
        var u = OnScreen(cut, f);
        Line(b, s.X, s.Y, s.W, u.X, u.Y, u.W, f.Samples);
    }

    /// <summary>Between two colours, <paramref name="t"/> of 255 of the way.</summary>
    private static uint Blend(uint a, uint b, int t)
    {
        uint Channel(int shift) => (uint)((((int)((a >> shift) & 0xFF) * (255 - t)) + ((int)((b >> shift) & 0xFF) * t)) / 255) << shift;
        return Channel(16) | Channel(8) | Channel(0);
    }
}
