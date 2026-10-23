using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EasyPDF.Pdf;
using EasyPDF.Prc;

/// <summary>
/// <c>--prc &lt;file.pdf&gt; [page] [--png out.png]</c>: reads the 3D models of a document's 3D
/// annotations and checks every compressed mesh decodes exactly (every stored value used, the
/// bounding box centred on the stored origin), optionally drawing the first model as its PDF view
/// shows it. For checking the PRC reader against real files, which cannot be fixtures here.
/// </summary>
internal static class PrcTool
{
    public static int Run(string[] args)
    {
        var file = args.First(a => !a.StartsWith("--", StringComparison.Ordinal));
        var png = args.SkipWhile(a => a != "--png").Skip(1).FirstOrDefault();
        int? onlyPage = args.Where(a => a != file && a != png && !a.StartsWith("--", StringComparison.Ordinal)).Select(a => int.TryParse(a, out var p) ? p - 1 : (int?)null).FirstOrDefault();

        using var source = FileSource.Open(file);
        var reader = PdfObjectReader.Open(source);
        if (reader is null)
        {
            Console.WriteLine("not a PDF this can read");
            return 1;
        }
        var sw = Stopwatch.StartNew();
        var found = new List<(int Page, Pdf3DModel Model)>();
        var pages = onlyPage is { } single ? [single] : Enumerable.Range(0, reader.PageCount);
        foreach (var page in pages)
            foreach (var model in reader.ThreeDModels(page, 256 << 20))
                found.Add((page, model));
        Console.WriteLine($"{found.Count} 3D model(s) found in {sw.ElapsedMilliseconds} ms");

        var failures = 0;
        foreach (var (page, model) in found)
        {
            Console.WriteLine($"page {page + 1}: {model.Kind}, {model.Data.Length:N0} bytes, view {(model.CameraToWorld is null ? "none" : "given")}");
            if (model.Kind != "PRC") continue;
            sw.Restart();
            PrcFile prc;
            try
            {
                prc = PrcFile.Read(model.Data);
            }
            catch (PrcFormatException e)
            {
                Console.WriteLine($"  not read: {e.Message}");
                failures++;
                continue;
            }
            Console.WriteLine($"  read in {sw.ElapsedMilliseconds} ms: {prc.FileStructures.Count} file structures, {prc.FileStructures.Sum(f => f.Occurrences.Count)} product occurrences");

            sw.Restart();
            var meshes = prc.FileStructures.SelectMany(f => f.Tessellations.OfType<PrcCompressedTessellation>()).ToList();
            var bad = new List<string>();
            long triangles = 0;
            foreach (var (t, i) in meshes.Select((t, i) => (t, i)))
            {
                var mesh = t.Decode(out var report);
                triangles += t.TriangleFaces.Length;
                if (!report.Exact) bad.Add($"#{i} stops at {report.Triangles}/{report.TrianglesExpected}: {report.Failure ?? "not all values used"}");
                else if (CentreError(t, mesh) > 0.01) bad.Add($"#{i} off its stored centre by {CentreError(t, mesh):P1} of its size");
            }
            Console.WriteLine($"  {meshes.Count} compressed meshes, {triangles:N0} triangles, decoded in {sw.ElapsedMilliseconds} ms: {meshes.Count - bad.Count} exact");
            foreach (var b in bad) Console.WriteLine($"    {b}");
            failures += bad.Count;

            if (png != null)
            {
                sw.Restart();
                var scene = PrcScene.Build(prc);
                Raster.Png(scene, png, 900, 1280, model.CameraToWorld);
                Console.WriteLine($"  drew {scene.Meshes.Count} mesh instances and {scene.Wires.Count} wire sets to {png} in {sw.ElapsedMilliseconds} ms");
                png = null;
            }
        }
        return failures == 0 ? 0 : 1;
    }

    /// <summary>How far the mesh's bounding-box centre is from the origin the file stores for it, against the box's diagonal.</summary>
    private static double CentreError(PrcCompressedTessellation t, PrcMesh m)
    {
        if (m.Positions.Length < 3) return 1;
        double[] lo = [double.MaxValue, double.MaxValue, double.MaxValue], hi = [double.MinValue, double.MinValue, double.MinValue];
        for (var i = 0; i < m.Positions.Length; i += 3)
            for (var k = 0; k < 3; k++)
            {
                lo[k] = Math.Min(lo[k], m.Positions[i + k]);
                hi[k] = Math.Max(hi[k], m.Positions[i + k]);
            }
        var size = Math.Max(1e-9, Math.Sqrt(Enumerable.Range(0, 3).Sum(k => (hi[k] - lo[k]) * (hi[k] - lo[k]))));
        var d = Math.Sqrt(Enumerable.Range(0, 3).Sum(k => Math.Pow((lo[k] + hi[k]) / 2 - t.Origin[k], 2)));
        return Math.Min(1, d / size);
    }

    /// <summary>A software rasteriser: depth buffer, flat two-sided shading, see-through parts after the rest, edge lines.</summary>
    private static class Raster
    {
        public static void Png(PrcScene scene, string path, int width, int height, double[]? c2w)
        {
            // The PDF view's camera: X right, Y up, Z the way it looks.
            c2w ??= [1, 0, 0, 0, 0, 1, 0, -1, 0, 0, 0, 0];
            (double, double, double) right = (c2w[0], c2w[1], c2w[2]), up = (c2w[3], c2w[4], c2w[5]), forward = (c2w[6], c2w[7], c2w[8]);
            static double Dot((double, double, double) a, (double X, double Y, double Z) b) => a.Item1 * b.X + a.Item2 * b.Y + a.Item3 * b.Z;

            var world = new List<(double X, double Y, double Z)[]>();
            double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
            void Extend((double X, double Y, double Z) p)
            {
                minU = Math.Min(minU, Dot(right, p));
                maxU = Math.Max(maxU, Dot(right, p));
                minV = Math.Min(minV, Dot(up, p));
                maxV = Math.Max(maxV, Dot(up, p));
            }
            foreach (var inst in scene.Meshes)
            {
                var p = inst.Mesh.Positions;
                var w = new (double X, double Y, double Z)[p.Length / 3];
                for (var i = 0; i < w.Length; i++) Extend(w[i] = inst.Transform.Apply(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]));
                world.Add(w);
            }
            foreach (var wire in scene.Wires)
                for (var i = 0; i + 2 < wire.Wire.Coordinates.Length; i += 3)
                    Extend(wire.Transform.Apply(wire.Wire.Coordinates[i], wire.Wire.Coordinates[i + 1], wire.Wire.Coordinates[i + 2]));
            if (minU > maxU) return;

            var scale = 0.92 * Math.Min(width / Math.Max(1e-9, maxU - minU), height / Math.Max(1e-9, maxV - minV));
            double cu = (minU + maxU) / 2, cv = (minV + maxV) / 2;
            (double X, double Y, double D) Project((double X, double Y, double Z) p) =>
                (width / 2.0 + (Dot(right, p) - cu) * scale, height / 2.0 - (Dot(up, p) - cv) * scale, Dot(forward, p));

            var color = new float[width * height * 3];
            var depth = new double[width * height];
            Array.Fill(depth, double.MaxValue);
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    color[(y * width + x) * 3] = color[(y * width + x) * 3 + 1] = color[(y * width + x) * 3 + 2] = 0.98f - 0.18f * y / height;

            var light = Unit((-forward.Item1 * 0.8 + up.Item1 * 0.4 + right.Item1 * 0.3, -forward.Item2 * 0.8 + up.Item2 * 0.4 + right.Item2 * 0.3, -forward.Item3 * 0.8 + up.Item3 * 0.4 + right.Item3 * 0.3));
            for (var pass = 0; pass < 2; pass++)
                for (var k = 0; k < scene.Meshes.Count; k++)
                {
                    var inst = scene.Meshes[k];
                    var w = world[k];
                    var tri = inst.Mesh.Triangles;
                    for (var i = 0; i < tri.Length / 3; i++)
                    {
                        var c = inst.TriangleColors[i];
                        var seeThrough = c.A < 0.99;
                        if (seeThrough != (pass == 1)) continue;
                        var a = w[tri[i * 3]];
                        var b = w[tri[i * 3 + 1]];
                        var d = w[tri[i * 3 + 2]];
                        var n = Unit(Cross((b.X - a.X, b.Y - a.Y, b.Z - a.Z), (d.X - a.X, d.Y - a.Y, d.Z - a.Z)));
                        var shade = 0.35 + 0.65 * Math.Abs(n.Item1 * light.Item1 + n.Item2 * light.Item2 + n.Item3 * light.Item3);
                        Fill(Project(a), Project(b), Project(d), (float)(c.R * shade), (float)(c.G * shade), (float)(c.B * shade), (float)c.A, !seeThrough);
                    }
                }

            foreach (var wire in scene.Wires)
            {
                var coords = wire.Wire.Coordinates;
                var idx = wire.Wire.Indices;
                (double X, double Y, double D) P(uint at) => Project(wire.Transform.Apply(coords[at], coords[at + 1], coords[at + 2]));
                for (var i = 0; i < idx.Length;)
                {
                    var count = (int)(idx[i] & 0x0FFFFFFF);
                    for (var j = 0; j + 1 < count && i + 2 + j < idx.Length; j++) Line(P(idx[i + 1 + j]), P(idx[i + 2 + j]));
                    if ((idx[i] & 0x10000000) != 0 && count > 1 && i + count < idx.Length) Line(P(idx[i + count]), P(idx[i + 1]));
                    i += count + 1;
                }
            }

            var bytes = new byte[width * height * 3];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)Math.Clamp(color[i] * 255, 0, 255);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, bytes, width * 3)));
            using var output = File.Create(path);
            encoder.Save(output);

            void Fill((double X, double Y, double D) p0, (double X, double Y, double D) p1, (double X, double Y, double D) p2, float r, float g, float bl, float alpha, bool writeDepth)
            {
                var area = (p1.X - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (p1.Y - p0.Y);
                if (Math.Abs(area) < 1e-12) return;
                var x0 = Math.Max(0, (int)Math.Floor(Math.Min(p0.X, Math.Min(p1.X, p2.X))));
                var x1 = Math.Min(width - 1, (int)Math.Ceiling(Math.Max(p0.X, Math.Max(p1.X, p2.X))));
                var y0 = Math.Max(0, (int)Math.Floor(Math.Min(p0.Y, Math.Min(p1.Y, p2.Y))));
                var y1 = Math.Min(height - 1, (int)Math.Ceiling(Math.Max(p0.Y, Math.Max(p1.Y, p2.Y))));
                for (var y = y0; y <= y1; y++)
                    for (var x = x0; x <= x1; x++)
                    {
                        double px = x + 0.5, py = y + 0.5;
                        var w0 = ((p1.X - px) * (p2.Y - py) - (p2.X - px) * (p1.Y - py)) / area;
                        var w1 = ((p2.X - px) * (p0.Y - py) - (p0.X - px) * (p2.Y - py)) / area;
                        var w2 = 1 - w0 - w1;
                        if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                        var z = w0 * p0.D + w1 * p1.D + w2 * p2.D;
                        var at = y * width + x;
                        if (z >= depth[at]) continue;
                        if (writeDepth) depth[at] = z;
                        color[at * 3] = color[at * 3] * (1 - alpha) + r * alpha;
                        color[at * 3 + 1] = color[at * 3 + 1] * (1 - alpha) + g * alpha;
                        color[at * 3 + 2] = color[at * 3 + 2] * (1 - alpha) + bl * alpha;
                    }
            }

            void Line((double X, double Y, double D) a, (double X, double Y, double D) b)
            {
                var steps = (int)Math.Ceiling(Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y))) + 1;
                for (var s = 0; s <= steps; s++)
                {
                    var f = s / (double)steps;
                    int x = (int)(a.X + (b.X - a.X) * f), y = (int)(a.Y + (b.Y - a.Y) * f);
                    if (x < 0 || y < 0 || x >= width || y >= height) continue;
                    var at = y * width + x;
                    if (a.D + (b.D - a.D) * f > depth[at] + 2) continue;
                    color[at * 3] *= 0.35f;
                    color[at * 3 + 1] *= 0.35f;
                    color[at * 3 + 2] *= 0.35f;
                }
            }
        }

        private static (double, double, double) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
            (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        private static (double, double, double) Unit((double, double, double) v)
        {
            var l = Math.Sqrt(v.Item1 * v.Item1 + v.Item2 * v.Item2 + v.Item3 * v.Item3);
            return l == 0 ? v : (v.Item1 / l, v.Item2 / l, v.Item3 / l);
        }
    }
}
