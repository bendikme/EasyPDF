using EasyPDF.Prc;

namespace EasyPDF.Viewer;

/// <summary>
/// A 3D model ready to draw: the triangles of every placed mesh and every edge line, in world
/// coordinates, with each triangle's colour - the opaque triangles before the see-through ones,
/// which are drawn over them.
/// </summary>
internal sealed class ModelScene
{
    /// <summary>The most triangles, and line segments, a model is drawn with; past them, parts are left out.</summary>
    internal const int MaxTriangles = 16_000_000;

    internal const int MaxLines = 24_000_000;

    /// <summary>The triangles' corners, three numbers each.</summary>
    public required float[] Points { get; init; }

    /// <summary>Three corners a triangle; the first <see cref="OpaqueTriangles"/> are opaque.</summary>
    public required int[] Triangles { get; init; }

    public required int OpaqueTriangles { get; init; }

    /// <summary>Each triangle's colour, 0xAARRGGBB.</summary>
    public required uint[] Colors { get; init; }

    /// <summary>The edge lines' ends, three numbers each.</summary>
    public required float[] LinePoints { get; init; }

    /// <summary>Two line points a segment.</summary>
    public required int[] Lines { get; init; }

    /// <summary>Opposite corners of the box round everything drawn.</summary>
    public required Vec3 Min { get; init; }

    public required Vec3 Max { get; init; }

    /// <summary>Meshes not drawn: their decode was not exact, or the model has more than can be drawn.</summary>
    public int PartsLeftOut { get; init; }

    public int TriangleCount => Triangles.Length / 3;

    public int LineCount => Lines.Length / 2;

    public Vec3 Centre => (Min + Max) * 0.5;

    /// <summary>Half the box's diagonal: everything is within this of the centre.</summary>
    public double Radius => Math.Max(1e-6, (Max - Min).Length / 2);

    /// <summary>The scene of a PRC file.</summary>
    public static ModelScene Read(PrcFile file)
    {
        var inexact = 0;
        var scene = PrcScene.Build(file, (_, report) =>
        {
            if (!report.Exact) inexact++;
        });
        return From(scene, inexact);
    }

    /// <summary>The scene placed: every instance's points in world coordinates, the colours packed, the see-through triangles last.</summary>
    public static ModelScene From(PrcScene scene, int leftOut = 0)
    {
        // What fits, in order: an instance past the limits is left out whole.
        var meshes = new List<PrcMeshInstance>();
        long points = 0, triangles = 0, seeThrough = 0;
        foreach (var instance in scene.Meshes)
        {
            var count = instance.Mesh.Triangles.Length / 3;
            if (triangles + count > MaxTriangles)
            {
                leftOut++;
                continue;
            }
            meshes.Add(instance);
            points += instance.Mesh.Positions.Length / 3;
            triangles += count;
            for (var t = 0; t < count; t++)
                if (Pack(instance.TriangleColors[t]) >> 24 < 0xFC) seeThrough++;
        }

        var xyz = new float[points * 3];
        var corners = new int[triangles * 3];
        var colors = new uint[triangles];
        var min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
        int nextOpaque = 0, nextClear = (int)(triangles - seeThrough), at = 0;
        foreach (var instance in meshes)
        {
            var first = at / 3;
            var p = instance.Mesh.Positions;
            for (var i = 0; i + 2 < p.Length; i += 3)
            {
                var (x, y, z) = instance.Transform.Apply(p[i], p[i + 1], p[i + 2]);
                xyz[at++] = (float)x;
                xyz[at++] = (float)y;
                xyz[at++] = (float)z;
                Grow(ref min, ref max, x, y, z);
            }
            var tri = instance.Mesh.Triangles;
            for (var t = 0; t < tri.Length / 3; t++)
            {
                var color = Pack(instance.TriangleColors[t]);
                var slot = color >> 24 < 0xFC ? nextClear++ : nextOpaque++;
                colors[slot] = color;
                corners[slot * 3] = first + tri[t * 3];
                corners[(slot * 3) + 1] = first + tri[(t * 3) + 1];
                corners[(slot * 3) + 2] = first + tri[(t * 3) + 2];
            }
        }

        // Edge lines: each polyline's points, then the segments between them - and back to the
        // first point when it is closed. A polyline's count is its points, flags above.
        var wires = new List<PrcWireInstance>();
        long wirePoints = 0, segments = 0;
        foreach (var wire in scene.Wires)
        {
            var idx = wire.Wire.Indices;
            long count = 0;
            for (var i = 0; i < idx.Length; i += (int)(idx[i] & 0x0FFFFFFF) + 1)
                count += Math.Max(0, (int)(idx[i] & 0x0FFFFFFF) - 1) + ((idx[i] & 0x10000000) != 0 ? 1 : 0);
            if (segments + count > MaxLines) continue;
            wires.Add(wire);
            wirePoints += wire.Wire.Coordinates.Length / 3;
            segments += count;
        }

        var linePoints = new float[wirePoints * 3];
        var lines = new int[segments * 2];
        int pointAt = 0, lineAt = 0;
        foreach (var wire in wires)
        {
            var c = wire.Wire.Coordinates;
            var idx = wire.Wire.Indices;
            var first = pointAt / 3;
            var pointCount = c.Length / 3;
            for (var i = 0; i + 2 < c.Length; i += 3)
            {
                var (x, y, z) = wire.Transform.Apply(c[i], c[i + 1], c[i + 2]);
                linePoints[pointAt++] = (float)x;
                linePoints[pointAt++] = (float)y;
                linePoints[pointAt++] = (float)z;
                Grow(ref min, ref max, x, y, z);
            }
            for (var i = 0; i < idx.Length;)
            {
                var n = (int)(idx[i] & 0x0FFFFFFF);
                var closed = (idx[i] & 0x10000000) != 0;
                for (var j = 0; j + 1 < n && i + 2 + j < idx.Length; j++) Segment(idx[i + 1 + j], idx[i + 2 + j]);
                if (closed && n > 1 && i + n < idx.Length) Segment(idx[i + n], idx[i + 1]);
                i += n + 1;
            }

            void Segment(uint a, uint b)
            {
                // Indices count coordinates: a point's is a multiple of three.
                if (a % 3 != 0 || b % 3 != 0 || a / 3 >= pointCount || b / 3 >= pointCount) return;
                lines[lineAt++] = first + (int)(a / 3);
                lines[lineAt++] = first + (int)(b / 3);
            }
        }

        if (min.X > max.X) min = max = default;
        return new ModelScene
        {
            Points = xyz,
            Triangles = corners,
            OpaqueTriangles = (int)(triangles - seeThrough),
            Colors = colors,
            LinePoints = linePoints,
            Lines = lineAt == lines.Length ? lines : lines[..lineAt],
            Min = min,
            Max = max,
            PartsLeftOut = leftOut,
        };
    }

    private static void Grow(ref Vec3 min, ref Vec3 max, double x, double y, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) return;
        min = new Vec3(Math.Min(min.X, x), Math.Min(min.Y, y), Math.Min(min.Z, z));
        max = new Vec3(Math.Max(max.X, x), Math.Max(max.Y, y), Math.Max(max.Z, z));
    }

    private static uint Pack(PrcRgba c) =>
        ((uint)Math.Round(Math.Clamp(c.A, 0, 1) * 255) << 24) | ((uint)Math.Round(Math.Clamp(c.R, 0, 1) * 255) << 16)
        | ((uint)Math.Round(Math.Clamp(c.G, 0, 1) * 255) << 8) | (uint)Math.Round(Math.Clamp(c.B, 0, 1) * 255);
}
