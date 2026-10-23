namespace EasyPDF.Prc;

/// <summary>A triangle mesh: positions, three vertex indices per triangle, and per triangle its face and style.</summary>
internal sealed class PrcMesh
{
    public required double[] Positions { get; init; }

    public required int[] Triangles { get; init; }

    /// <summary>Unit normals per triangle corner (nine values a triangle), or null when the file stores none.</summary>
    public float[]? CornerNormals { get; init; }

    /// <summary>The face (a group of triangles in the CAD model) of each triangle.</summary>
    public required int[] TriangleFaces { get; init; }

    /// <summary>Each triangle's index into the file structure's styles, or -1 when the tessellation names none.</summary>
    public required int[] TriangleStyles { get; init; }
}

/// <summary>Switches for the points the standard and its errata leave open, while the decoder is checked against files.</summary>
internal sealed record PrcDecodeOptions
{
    public bool SeedFromOrigin { get; init; }

    /// <summary>0: points in doubles; 1: each point rounded to singles; 2: frames computed in singles too.</summary>
    public int FloatMode { get; init; }

    /// <summary>0: a reference is a point index; 1: counted back from the newest point; 2: back from the next one.</summary>
    public int ReferenceMode { get; init; }

    /// <summary>A reference to an earlier normal of a vertex: false, in as many bits as the highest index needs; true, one bit per stored normal.</summary>
    public bool NormalReferenceBitPerNormal { get; init; }

    /// <summary>The second case of the normal frame: false as the 2026 pseudo-code (θ1 &lt; θ3), true as the standard (θ2 &lt; θ3).</summary>
    public bool NormalFrameSpec { get; init; }

    /// <summary>Whether a triangle's orientation is read as one more bit (else from its first vertex's normal).</summary>
    public bool OrientationBit { get; init; }

    public bool PushLeftFirst { get; init; }

    /// <summary>0: from the first corner's normal against the decoded triangle; 1: inherited across the gate edge;
    /// 2: from a new normal's reversed bit, else 0; 3: from a new normal's reversed bit, else 1.</summary>
    public int OrientationMode { get; init; }

    public Action<string>? Trace { get; init; }

    public Action<int>? ReferenceStats { get; init; }

    public int[]? ReferenceBits { get; init; }

    public Action<(int Corner, bool Rt, bool NewVertex)[]>? RtStats { get; init; }

    public HashSet<int>? FlipAt { get; init; }

    public bool NoSkip { get; init; }

    public bool ApexRotated { get; init; }

    /// <summary>A known exact point near a decoded one (from the part's edge lines), or null.</summary>
    public Func<double, double, double, (double X, double Y, double Z)?>? Snap { get; init; }

    public Action<int, double[]>? ApexTrace { get; init; }

    /// <summary>0: the frame's X from the edge's lower to higher index; 1: as the parent triangle runs the edge; 2: against it.</summary>
    public int XMode { get; init; }

    public Action<int, double>? Consistency { get; init; }

    public HashSet<int>? SwapAt { get; init; }

    /// <summary>Frames computed with the standard's own length, unitize and MakeOrthoRep (ISO 14739-1 12.3).</summary>
    public bool AnnexMath { get; init; }

    /// <summary>A triangle whose normal cannot be unitized: 0 as any other; 1 never reversed; 2 inherited.</summary>
    public int DegenerateMode { get; init; }

    public bool NoCloseUnflagged { get; init; }

    /// <summary>A reference adds the normal it refers to to the vertex's list again (the list is of uses, not of distinct normals).</summary>
    public bool ReferencesAppend { get; init; }

    /// <summary>A reference's width: the standard's bits-to-store function of the highest index (at least 1), else the plain bit count.</summary>
    public bool ReferenceWidthAtLeastOne { get; init; } = true;
}

/// <summary>What a decode consumed and produced, against what the entity holds: a correct decode uses everything exactly.</summary>
internal sealed record PrcDecodeReport(
    int Triangles, int TrianglesExpected,
    int PointTriples, int PointTriplesStored,
    int ReferenceFlags, int ReferenceFlagsStored,
    int References, int ReferencesStored,
    int NormalBits, int NormalBitsStored,
    int NormalAngles, int NormalAnglesStored,
    int Components, string? Failure);

/// <summary>
/// PRC_TYPE_TESS_3D_Compressed, the "highly compressed tessellation" (ISO 14739-1 7.8.9): a mesh
/// stored as a traversal from triangle to neighbouring triangle, each new point given in the frame
/// of the edge it is reached through and quantised to a tolerance. The traversal follows the
/// standard as corrected by the PDF Association's errata for it (pdf-issues #436, #540, #727,
/// #772, #773, #774, #775).
/// </summary>
internal sealed class PrcCompressedTessellation
{
    public bool HasFaces { get; private init; }
    public double Tolerance { get; private init; }
    public float[] Origin { get; private init; } = [0, 0, 0];
    public int[] PointArray { get; private init; } = [];
    public int[] EdgeStatus { get; private init; } = [];
    public int[] TriangleFaces { get; private init; } = [];
    public bool[] PointIsReference { get; private init; } = [];
    public int[] PointReferences { get; private init; } = [];
    public bool MustRecalculateNormals { get; private init; }
    public bool[] NormalIsReversed { get; private init; } = [];
    public double CreaseAngle { get; private init; }
    public int NormalAngleBits { get; private init; }
    public bool[] NormalBits { get; private init; } = [];
    public int[] NormalAngles { get; private init; } = [];
    public bool[] FaceIsPlanar { get; private init; } = [];
    public bool[]? MultipleLineAttributeOnFace { get; private init; }
    public int[] LineAttributes { get; private init; } = [];
    public int FaceCount { get; private init; }

    /// <summary>Reads the entity after its type, which the caller has read.</summary>
    public static PrcCompressedTessellation Read(PrcBitReader r, uint authoringVersion)
    {
        r.Boolean(); // is_calculated
        var hasFaces = r.Boolean();
        var tolerance = r.Double();
        float[] origin = authoringVersion >= 7031 ? [r.FloatAsBytes(), r.FloatAsBytes(), r.FloatAsBytes()] : [0, 0, 0];
        var points = r.CompressedIntegerArray();
        var edgeStatus = r.CharacterArray(2);
        var triangleFaces = r.CompressedIndiceArray();
        var referenceFlags = r.BoolArray(r.UnsignedInteger());
        var referenceCount = referenceFlags.Count(f => f);
        var references = r.CompressedIndiceArray(hasCompressedBit: false, compressed: referenceCount > 3);
        var faceCount = triangleFaces.Length == 0 ? 0 : triangleFaces.Max() + 1;

        var mustRecalculate = r.Boolean();
        bool[] normalIsReversed = [], normalBits = [], planar = [];
        int[] angles = [];
        double crease = 0;
        var angleBits = 0;
        if (mustRecalculate)
        {
            normalIsReversed = r.BoolArray((uint)triangleFaces.Length);
            crease = r.Double();
            r.Character(); // normal_recalculation_flags
        }
        else
        {
            angleBits = r.Character();
            normalBits = r.BoolArray(r.UnsignedInteger());
            angles = r.ShortArray(angleBits, signExtend: false);
            planar = r.BoolArray((uint)faceCount);
        }

        var perFace = hasFaces ? (uint)faceCount : 1u;
        if (r.Boolean()) // is_point_color
        {
            r.BoolArray(perFace);
            r.CharacterArray(8);
        }
        bool[]? multipleLineAttributes = r.Boolean() ? r.BoolArray(perFace) : null;
        var lineAttributes = r.ShortArray(16);
        if (!r.Boolean()) // no_texture
        {
            SkipTextureParameters(r);
            if (!r.Boolean()) r.BoolArray((uint)faceCount); // face_has_texture
        }
        if (r.Boolean()) r.CharacterArray(8); // behaviors

        return new PrcCompressedTessellation
        {
            HasFaces = hasFaces,
            Tolerance = tolerance,
            Origin = origin,
            PointArray = points,
            EdgeStatus = edgeStatus,
            TriangleFaces = triangleFaces,
            PointIsReference = referenceFlags,
            PointReferences = references,
            MustRecalculateNormals = mustRecalculate,
            NormalIsReversed = normalIsReversed,
            CreaseAngle = crease,
            NormalAngleBits = angleBits,
            NormalBits = normalBits,
            NormalAngles = angles,
            FaceIsPlanar = planar,
            MultipleLineAttributeOnFace = multipleLineAttributes,
            LineAttributes = lineAttributes,
            FaceCount = faceCount,
        };
    }

    private static void SkipTextureParameters(PrcBitReader r)
    {
        var words = r.UnsignedInteger();
        r.Skip(words * 32L);
        r.UnsignedInteger();
        var references = r.UnsignedInteger();
        for (var i = 0; i < references; i++) r.NumberOfBitsThenUnsignedInteger();
        r.Double();
        var parameters = r.UnsignedInteger();
        r.Skip(parameters * 32L);
    }

    public PrcMesh Decode(PrcDecodeOptions options, out PrcDecodeReport report)
    {
        var d = new Decoder(this, options);
        string? failure = null;
        try
        {
            d.Run();
        }
        catch (Exception e) when (e is PrcFormatException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            failure = e.Message;
        }
        report = new PrcDecodeReport(
            d.TriangleCount, TriangleFaces.Length,
            d.PointCursor / 3, PointArray.Length / 3,
            d.FlagCursor, PointIsReference.Length,
            d.ReferenceCursor, PointReferences.Length,
            d.NormalBitCursor, NormalBits.Length,
            d.AngleCursor, NormalAngles.Length,
            d.Components, failure);
        return d.Result();
    }

    private sealed class Decoder(PrcCompressedTessellation t, PrcDecodeOptions o)
    {
        private readonly List<V> _points = new(t.PointArray.Length / 3 + 3);
        private readonly List<int> _triangles = new(t.TriangleFaces.Length * 3);
        private readonly List<float> _normals = new();
        private readonly List<int> _faces = new(t.TriangleFaces.Length);
        private readonly Stack<(int Lo, int Hi, int Far, bool ParentLoToHi)> _stack = new();
        private readonly HashSet<long> _closed = new();
        private readonly Dictionary<int, StoredNormals> _vertexNormals = new();
        private readonly Dictionary<int, StoredNormals> _faceNormals = new();

        public int TriangleCount;
        private int _currentCorner, _pointsBeforeTriangle;
        private bool _currentNewVertex;
        public int PointCursor;
        public int FlagCursor;
        public int ReferenceCursor;
        public int NormalBitCursor;
        public int AngleCursor;
        public int Components;

        private sealed class StoredNormals
        {
            public bool HasMultiple;
            public readonly List<V> Normals = new();
        }

        public void Run()
        {
            var tri = new int[3];
            var corner = new V[3];
            for (var n = 0; n < t.TriangleFaces.Length; n++)
            {
                var face = t.TriangleFaces[n];
                bool? inherited = null;
                _newNormalReversed = null;
                _degenerate = false;
                _pointsBeforeTriangle = _points.Count;
                if (!NextGate(out var gate))
                {
                    Seed(tri);
                    Components++;
                }
                else
                {
                    tri[0] = gate.Lo;
                    tri[1] = gate.Hi;
                    // The edge's ends in the order the frame's X runs: by index, or as the parent ran the edge.
                    var (ea, eb) = o.XMode switch
                    {
                        1 => gate.ParentLoToHi ? (gate.Lo, gate.Hi) : (gate.Hi, gate.Lo),
                        2 => gate.ParentLoToHi ? (gate.Hi, gate.Lo) : (gate.Lo, gate.Hi),
                        _ => (gate.Lo, gate.Hi),
                    };
                    tri[2] = NextFlag() ? Reference() : AddPoint(Apex(_points[ea], _points[eb], _points[gate.Far], NextTriple()));
                    _closed.Add(EdgeKey(gate.Lo, gate.Hi));
                    // Wound (lo, hi, apex) the triangle runs lo to hi; its parent must run the edge the other way.
                    inherited = !gate.ParentLoToHi;
                }

                var reversed = false;
                if (t.MustRecalculateNormals)
                {
                    reversed = t.NormalIsReversed[n];
                }
                else
                {
                    TriangleRts.Clear();
                var swap = o.SwapAt?.Contains(n) == true;
                if (swap) (tri[0], tri[1]) = (tri[1], tri[0]);
                for (var k = 0; k < 3; k++)
                {
                    _currentCorner = k;
                    _currentNewVertex = tri[k] >= _pointsBeforeTriangle;
                    corner[k] = RestoreNormal(tri, k, face);
                }
                if (swap)
                {
                    (tri[0], tri[1]) = (tri[1], tri[0]);
                    (corner[0], corner[1]) = (corner[1], corner[0]);
                }
                if (TriangleRts.Count >= 2) o.RtStats?.Invoke([.. TriangleRts]);
                    if (t.FaceIsPlanar[face])
                    {
                        corner[1] = corner[2] = corner[0];
                    }
                    var geometric = V.Dot(DeriveNormal(_points[tri[0]], _points[tri[1]], _points[tri[2]]), corner[0]) < 0;
                    // Per corner: its new normal's reversed bit, or its normal against the decoded triangle.
                    bool CornerSays(int k)
                    {
                        foreach (var r in TriangleRts) if (r.Corner == k) return !r.Rt;
                        return V.Dot(DeriveNormal(_points[tri[0]], _points[tri[1]], _points[tri[2]]), corner[k]) < 0;
                    }
                    reversed = o.OrientationMode switch
                    {
                        1 => inherited ?? geometric,
                        2 => _newNormalReversed ?? geometric,
                        3 => _newNormalReversed ?? inherited ?? geometric,
                        4 => CornerSays(0),
                        5 => CornerSays(1),
                        6 => CornerSays(2),
                        7 => (CornerSays(0) ? 1 : 0) + (CornerSays(1) ? 1 : 0) + (CornerSays(2) ? 1 : 0) >= 2,
                        8 => TriangleRts.Count > 0 ? !TriangleRts[^1].Rt : inherited ?? geometric,
                        _ => geometric,
                    };
                    if (o.DegenerateMode > 0)
                    {
                        V p0 = _points[tri[0]], p1 = _points[tri[1]], p2 = _points[tri[2]];
                        (p1 - p0).TryUnitize(out var e1);
                        (p2 - p0).TryUnitize(out var e2);
                        if (_degenerate || !V.Cross(e1, e2).TryUnitize(out _))
                            reversed = o.DegenerateMode == 1 ? false : inherited ?? false;
                    }
                    if (o.FlipAt?.Contains(n) == true) reversed = !reversed;
                }

                o.Trace?.Invoke($"  tri {n}: v{tri[0]} v{tri[1]} v{tri[2]} reversed {reversed} status {t.EdgeStatus[n] & 3} geo n {V.Normalize(DeriveNormal(_points[tri[0]], _points[tri[1]], _points[tri[2]]))}");
                if (o.Consistency is { } consistency && !t.MustRecalculateNormals)
                {
                    var g = V.Normalize(DeriveNormal(_points[tri[0]], _points[tri[1]], _points[tri[2]]));
                    if (reversed) g = -g;
                    var worst = Math.Min(V.Dot(g, V.Normalize(corner[0])), Math.Min(V.Dot(g, V.Normalize(corner[1])), V.Dot(g, V.Normalize(corner[2]))));
                    consistency(n, worst);
                }
                EvaluateAdjacent(tri, reversed, t.EdgeStatus[n] & 3);

                // Derive_Normal faces the winding (0, 2, 1); a reversed triangle is wound (0, 1, 2).
                var (b, c) = reversed ? (1, 2) : (2, 1);
                _triangles.Add(tri[0]);
                _triangles.Add(tri[b]);
                _triangles.Add(tri[c]);
                if (!t.MustRecalculateNormals)
                {
                    foreach (var k in (ReadOnlySpan<int>)[0, b, c])
                    {
                        _normals.Add((float)corner[k].X);
                        _normals.Add((float)corner[k].Y);
                        _normals.Add((float)corner[k].Z);
                    }
                }
                _faces.Add(face);
                TriangleCount++;
            }
        }

        public PrcMesh Result()
        {
            var positions = new double[_points.Count * 3];
            for (var i = 0; i < _points.Count; i++)
            {
                positions[i * 3] = _points[i].X;
                positions[i * 3 + 1] = _points[i].Y;
                positions[i * 3 + 2] = _points[i].Z;
            }
            return new PrcMesh
            {
                Positions = positions,
                Triangles = [.. _triangles],
                CornerNormals = t.MustRecalculateNormals || _normals.Count != _triangles.Count * 3 ? null : [.. _normals],
                TriangleFaces = [.. _faces],
                TriangleStyles = Styles(),
            };
        }

        /// <summary>
        /// One line attribute per face, taken at its first triangle, or one per triangle of a face
        /// with several.
        /// </summary>
        private int[] Styles()
        {
            var styles = new int[_faces.Count];
            var faceStyle = new Dictionary<int, int>();
            var cursor = 0;
            var a = t.LineAttributes;
            for (var i = 0; i < styles.Length; i++)
            {
                var face = _faces[i];
                var multiple = t.MultipleLineAttributeOnFace is { } m && m[t.HasFaces ? face : 0];
                if (multiple)
                {
                    styles[i] = cursor < a.Length ? a[cursor++] : -1;
                }
                else
                {
                    if (!faceStyle.TryGetValue(face, out var s))
                    {
                        s = cursor < a.Length ? a[cursor++] : -1;
                        faceStyle[face] = s;
                    }
                    styles[i] = s;
                }
            }
            return styles;
        }

        private bool NextGate(out (int Lo, int Hi, int Far, bool ParentLoToHi) gate)
        {
            while (_stack.Count > 0)
            {
                gate = _stack.Pop();
                if (!_closed.Contains(EdgeKey(gate.Lo, gate.Hi)) || o.NoSkip) return true;
                o.Trace?.Invoke($"    skip closed edge v{gate.Lo} v{gate.Hi}");
            }
            gate = default;
            return false;
        }

        /// <summary>A triangle not reached through an edge: the first of each connected piece of the mesh.</summary>
        private void Seed(int[] tri)
        {
            var origin = o.SeedFromOrigin ? new V(t.Origin[0], t.Origin[1], t.Origin[2]) : default;
            var isReference = new[] { NextFlag(), NextFlag(), NextFlag() };
            tri[0] = isReference[0] ? Reference() : AddPoint(origin + NextTriple() * t.Tolerance);
            tri[1] = isReference[1] ? Reference() : AddPoint(_points[tri[0]] + NextTriple() * t.Tolerance);
            tri[2] = isReference[2] ? Reference() : AddPoint((_points[tri[0]] + _points[tri[1]]) * 0.5 + NextTriple() * t.Tolerance);
            if (tri[0] > tri[1]) (tri[0], tri[1]) = (tri[1], tri[0]);
        }

        /// <summary>
        /// The pushes for the triangle's two forward edges: right is (1, 2) and left (0, 2) as seen
        /// from the triangle's normal, swapped when it is reversed; right is pushed first.
        /// </summary>
        private void EvaluateAdjacent(int[] tri, bool reversed, int status)
        {
            var right = (A: tri[1], B: tri[2], Far: tri[0]);
            var left = (A: tri[0], B: tri[2], Far: tri[1]);
            if (reversed) (right, left) = (left, right);
            // The triangle is wound (0, 1, 2) when reversed, else (0, 2, 1).
            int[] winding = reversed ? [tri[0], tri[1], tri[2]] : [tri[0], tri[2], tri[1]];
            if (o.PushLeftFirst)
            {
                Offer(left, (status & 2) != 0, winding);
                Offer(right, (status & 1) != 0, winding);
            }
            else
            {
                Offer(right, (status & 1) != 0, winding);
                Offer(left, (status & 2) != 0, winding);
            }
        }

        private void Offer((int A, int B, int Far) edge, bool hasNeighbour, int[] winding)
        {
            var (lo, hi) = edge.A < edge.B ? (edge.A, edge.B) : (edge.B, edge.A);
            var loToHi = false;
            for (var i = 0; i < 3; i++)
                if (winding[i] == lo && winding[(i + 1) % 3] == hi) loToHi = true;
            if (hasNeighbour) _stack.Push((lo, hi, edge.Far, loToHi));
            else if (!o.NoCloseUnflagged) _closed.Add(EdgeKey(lo, hi));
        }

        /// <summary>
        /// A new point in the frame of the edge it is reached through: origin at the edge's middle, X
        /// along it from the higher-numbered end, Z across the plane of the triangle already on the
        /// other side, Y pointing towards that triangle (pdf-issues #773).
        /// </summary>
        private V Apex(V a, V b, V far, V offset)
        {
            if (o.AnnexMath) return ApexAnnex(a, b, far, offset);
            var origin = (a + b) * 0.5;
            var x = V.Normalize(a - b);
            var z = V.Cross(far - origin, x);
            V y;
            if (z.Length < 1e-7)
            {
                (y, z) = Orthonormal(x);
            }
            else
            {
                z = V.Normalize(z);
                y = V.Normalize(V.Cross(z, x));
            }
            if (V.Dot(y, origin - far) > 0)
            {
                y = -y;
                z = -z;
            }
            return origin + (x * offset.X + y * offset.Y + z * offset.Z) * t.Tolerance;
        }

        private V ApexAnnex(V a, V b, V far, V offset)
        {
            if (o.ApexRotated)
            {
                // Formulas (1) to (3) of 7.8.9.3, with V0 the lower-numbered end, then turned half a
                // turn about Z: the frame real files are encoded against (pdf-issues #773).
                var o0 = (a + b) * 0.5;
                (b - a).TryUnitize(out var xs);
                V ys, zs;
                if (!V.Cross(far - o0, xs).TryUnitize(out zs) || !V.Cross(zs, xs).TryUnitize(out ys))
                {
                    V.MakeOrthoRep(xs, out ys, out zs);
                    o.Trace?.Invoke("      (apex frame from MakeOrthoRep)");
                }
                o.ApexTrace?.Invoke(_points.Count, [o0.X, o0.Y, o0.Z, -xs.X, -xs.Y, -xs.Z, -ys.X, -ys.Y, -ys.Z, zs.X, zs.Y, zs.Z, offset.X, offset.Y, offset.Z, a.X, a.Y, a.Z, b.X, b.Y, b.Z, far.X, far.Y, far.Z]);
                return o0 + (-xs * offset.X + -ys * offset.Y + zs * offset.Z) * t.Tolerance;
            }
            var origin = (a + b) * 0.5;
            (a - b).TryUnitize(out var x);
            V y, z;
            if (!V.Cross(far - origin, x).TryUnitize(out z) || !V.Cross(z, x).TryUnitize(out y)) V.MakeOrthoRep(x, out y, out z);
            if (V.Dot(y, origin - far) > 0)
            {
                y = -y;
                z = -z;
            }
            return origin + (x * offset.X + y * offset.Y + z * offset.Z) * t.Tolerance;
        }

        private static (V Y, V Z) Orthonormal(V x)
        {
            var other = Math.Abs(x.X) < 0.9 ? new V(1, 0, 0) : new V(0, 1, 0);
            var y = V.Normalize(V.Cross(x, other));
            return (y, V.Normalize(V.Cross(x, y)));
        }

        private static V DeriveNormal(V v1, V v2, V v3) => V.Cross(v3 - (v1 + v2) * 0.5, v2 - v1);

        /// <summary>The normal of corner <paramref name="k"/>: read new, taken from an earlier corner of the same vertex or face, or referred to among them.</summary>
        private V RestoreNormal(int[] tri, int k, int face)
        {
            var before = NormalBitCursor;
            var stored0 = (t.FaceIsPlanar[face] ? _faceNormals : _vertexNormals).TryGetValue(t.FaceIsPlanar[face] ? face : tri[k], out var s0) ? s0.Normals.Count : -1;
            var result = RestoreNormal1(tri, k, face);
            o.Trace?.Invoke($"    corner {k} v{tri[k]} stored {stored0}: bits {string.Concat(t.NormalBits.Skip(before).Take(NormalBitCursor - before).Select(b => b ? '1' : '0'))} -> ({result.X:0.00},{result.Y:0.00},{result.Z:0.00})");
            return result;
        }

        private V RestoreNormal1(int[] tri, int k, int face)
        {
            var planar = t.FaceIsPlanar[face];
            if (planar && k > 0) return default;
            var map = planar ? _faceNormals : _vertexNormals;
            var key = planar ? face : tri[k];
            if (!map.TryGetValue(key, out var stored))
            {
                stored = new StoredNormals { HasMultiple = NextNormalBit() };
                map[key] = stored;
                var first = NextNormal(tri);
                stored.Normals.Add(first);
                return first;
            }
            if (!stored.HasMultiple) return stored.Normals[0];
            if (NextNormalBit())
            {
                o.ReferenceStats?.Invoke(stored.Normals.Count);
                var max = stored.Normals.Count - 1;
                var bits = o.ReferenceBits is { } table && stored.Normals.Count < table.Length ? table[stored.Normals.Count]
                    : o.NormalReferenceBitPerNormal ? stored.Normals.Count : o.ReferenceWidthAtLeastOne ? ReferenceWidth(max) : BitCount(max);
                var read = 0;
                for (var i = 0; i < bits; i++) if (NextNormalBit()) read |= 1 << i;
                var index = o.NormalReferenceBitPerNormal ? read : max - read;
                if (index < 0 || index > max) throw new PrcFormatException("PRC normal reference out of range");
                var referenced = stored.Normals[index];
                if (o.ReferencesAppend) stored.Normals.Add(referenced);
                return referenced;
            }
            var next = NextNormal(tri);
            stored.Normals.Add(next);
            return next;
        }

        /// <summary>
        /// The width of a reference to one of a vertex's earlier normals, 0 to <paramref name="max"/>:
        /// one bit more than the powers of two below <paramref name="max"/>, as real writers compute it.
        /// </summary>
        private static int ReferenceWidth(int max)
        {
            var bits = 1;
            while (1 << (bits - 1) < max) bits++;
            return bits;
        }

        private static int BitCount(int value)
        {
            var bits = 0;
            while (value > 0)
            {
                bits++;
                value >>= 1;
            }
            return bits;
        }

        /// <summary>A normal given by two angles in a frame built from the triangle's sides.</summary>
        private bool? _newNormalReversed;
        private bool _degenerate;
        public readonly List<(int Corner, bool Rt, bool NewVertex)> TriangleRts = new();

        private V NextNormal(int[] tri)
        {
            var reversedTriangle = NextNormalBit();
            // The writer set this bit from its own exact triangle: the triangle is reversed exactly when it is clear.
            _newNormalReversed ??= !reversedTriangle;
            TriangleRts.Add((_currentCorner, reversedTriangle, _currentNewVertex));
            var reversedX = NextNormalBit();
            var reversedY = NextNormalBit();
            V p0 = _points[tri[0]], p1 = _points[tri[1]], p2 = _points[tri[2]];
            V v1, v2, v3;
            double theta1, theta2, theta3;
            if (o.AnnexMath)
            {
                (p1 - p0).TryUnitize(out v1);
                (p2 - p0).TryUnitize(out v2);
                (p2 - p1).TryUnitize(out v3);
                theta1 = Math.Abs(V.AngleBetween(v1, v2) - Math.PI / 2);
                theta2 = Math.Abs(V.AngleBetween(v3, -v1) - Math.PI / 2);
                theta3 = Math.Abs(V.AngleBetween(-v2, -v3) - Math.PI / 2);
            }
            else
            {
                v1 = V.Normalize(p1 - p0);
                v2 = V.Normalize(p2 - p0);
                v3 = V.Normalize(p2 - p1);
                theta1 = Math.Abs(Math.Abs(Math.Acos(Math.Clamp(V.Dot(v1, v2), -1, 1))) - Math.PI / 2);
                theta2 = Math.Abs(Math.Abs(Math.Acos(Math.Clamp(V.Dot(v3, -v1), -1, 1))) - Math.PI / 2);
                theta3 = Math.Abs(Math.Abs(Math.Acos(Math.Clamp(V.Dot(-v2, -v3), -1, 1))) - Math.PI / 2);
            }
            V x, z;
            if (theta1 < theta2 && theta1 < theta3)
            {
                x = v1;
                z = V.Cross(v1, v2);
            }
            else if (o.NormalFrameSpec ? theta2 < theta3 : theta1 < theta3)
            {
                x = v3;
                z = V.Cross(-v3, v1);
            }
            else
            {
                x = -v2;
                z = V.Cross(v2, v3);
            }
            V y;
            if (o.AnnexMath)
            {
                if (!z.TryUnitize(out z))
                {
                    _degenerate = true;
                    V.MakeOrthoRep(x, out _, out z);
                }
                if (reversedTriangle) z = -z;
                if (!V.Cross(z, x).TryUnitize(out y)) V.MakeOrthoRep(x, out y, out z);
            }
            else
            {
                z = V.Normalize(z);
                if (reversedTriangle) z = -z;
                y = V.Normalize(V.Cross(z, x));
                x = V.Normalize(x);
            }
            if (reversedX) x = -x;
            if (reversedY) y = -y;

            var scale = Math.PI / 2 / ((1 << t.NormalAngleBits) - 1);
            if (AngleCursor + 2 > t.NormalAngles.Length) throw new PrcFormatException("PRC normal angles run out");
            o.Trace?.Invoke($"      new normal rt={reversedTriangle} rx={reversedX} ry={reversedY} angles {t.NormalAngles[AngleCursor]},{t.NormalAngles[AngleCursor + 1]} frame x=({x.X:0.00},{x.Y:0.00},{x.Z:0.00}) y=({y.X:0.00},{y.Y:0.00},{y.Z:0.00}) z=({z.X:0.00},{z.Y:0.00},{z.Z:0.00}) th=({theta1:0.000},{theta2:0.000},{theta3:0.000})");
            var theta = t.NormalAngles[AngleCursor++] * scale;
            var phi = t.NormalAngles[AngleCursor++] * scale;
            double cosTheta = Cos4(theta), sinTheta = Sin4(theta), cosPhi = Cos4(phi), sinPhi = Sin4(phi);
            return x * (cosTheta * cosPhi) + y * (sinTheta * cosPhi) + z * sinPhi;
        }

        // The standard computes the angles' sine and cosine with four terms of their Taylor series.
        private static double Sin4(double x)
        {
            var x2 = x * x;
            return x * (1.0 - x2 * (1.0 / 6.0 - x2 * (1.0 / 120.0 - x2 * (1.0 / 5040.0))));
        }

        private static double Cos4(double x)
        {
            var x2 = x * x;
            return 1.0 - x2 * (1.0 / 2.0 - x2 * (1.0 / 24.0 - x2 * (1.0 / 720.0)));
        }

        private bool NextNormalBit()
        {
            if (NormalBitCursor >= t.NormalBits.Length) throw new PrcFormatException("PRC normal bits run out");
            return t.NormalBits[NormalBitCursor++];
        }

        private bool NextFlag()
        {
            if (FlagCursor >= t.PointIsReference.Length) throw new PrcFormatException("PRC point flags run out");
            return t.PointIsReference[FlagCursor++];
        }

        private int Reference()
        {
            if (ReferenceCursor >= t.PointReferences.Length) throw new PrcFormatException("PRC point references run out");
            var r = t.PointReferences[ReferenceCursor++];
            var index = o.ReferenceMode switch
            {
                0 => r,
                1 => _points.Count - 1 - r,
                _ => _points.Count - r,
            };
            if (index < 0 || index >= _points.Count) throw new PrcFormatException($"PRC point reference {r} out of range ({_points.Count} points)");
            return index;
        }

        private V NextTriple()
        {
            if (PointCursor + 3 > t.PointArray.Length) throw new PrcFormatException("PRC points run out");
            var p = t.PointArray;
            var v = new V(p[PointCursor], p[PointCursor + 1], p[PointCursor + 2]);
            PointCursor += 3;
            return v;
        }

        private int AddPoint(V p)
        {
            o.Trace?.Invoke($"      point v{_points.Count} = ({p.X:0.####}, {p.Y:0.####}, {p.Z:0.####})");
            if (o.Snap?.Invoke(p.X, p.Y, p.Z) is { } snapped) p = new V(snapped.X, snapped.Y, snapped.Z);
            if (o.FloatMode == 1) p = new V((float)p.X, (float)p.Y, (float)p.Z);
            if (o.FloatMode == 2)
            {
                double gx = t.Origin[0], gy = t.Origin[1], gz = t.Origin[2];
                p = new V(gx + (float)(p.X - gx), gy + (float)(p.Y - gy), gz + (float)(p.Z - gz));
            }
            if (o.FloatMode == 3)
            {
                var g = new V(t.Origin[0], t.Origin[1], t.Origin[2]);
                p = new V(g.X + Math.Round((p.X - g.X) / t.Tolerance) * t.Tolerance, g.Y + Math.Round((p.Y - g.Y) / t.Tolerance) * t.Tolerance, g.Z + Math.Round((p.Z - g.Z) / t.Tolerance) * t.Tolerance);
            }
            if (o.FloatMode == 4) p = new V(Math.Round(p.X / t.Tolerance) * t.Tolerance, Math.Round(p.Y / t.Tolerance) * t.Tolerance, Math.Round(p.Z / t.Tolerance) * t.Tolerance);
            _points.Add(p);
            return _points.Count - 1;
        }

        private static long EdgeKey(int a, int b) => a < b ? (long)a << 32 | (uint)b : (long)b << 32 | (uint)a;
    }

    private readonly record struct V(double X, double Y, double Z)
    {
        public static V operator +(V a, V b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static V operator -(V a, V b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V operator -(V a) => new(-a.X, -a.Y, -a.Z);
        public static V operator *(V a, double s) => new(a.X * s, a.Y * s, a.Z * s);
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public static double Dot(V a, V b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static V Cross(V a, V b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        public static V Normalize(V a)
        {
            var l = a.Length;
            return l > 0 ? a * (1 / l) : a;
        }

        /// <summary>
        /// The standard's length (ISO 14739-1 12.3, PrcPt::Length): the square root, by Newton's
        /// method, of the squared length rounded to a single. Writers compute with it, so the points
        /// they encode against carry its rounding; -1 when the iteration does not settle.
        /// </summary>
        public double AnnexLength()
        {
            var f = (float)(X * X + Y * Y + Z * Z);
            double squared = f;
            var bits = BitConverter.SingleToUInt32Bits(f);
            var exponent = (int)((bits >> 23) & 0xFF);
            if (exponent > 127) bits = (bits & ~(0xFFu << 23)) | ((uint)((exponent - 127) / 2 + 127) << 23);
            double x0 = BitConverter.UInt32BitsToSingle(bits);
            for (var count = 0; count < 100; count++)
            {
                var xi = 0.5 * (x0 + squared / x0);
                if (xi == x0) return xi;
                x0 = xi;
            }
            return -1;
        }

        /// <summary>PrcPt::Unitize: divided by its length, unless that is below FLT_EPSILON.</summary>
        public bool TryUnitize(out V unit)
        {
            var l = AnnexLength();
            if (l < 1.1920928955078125e-07)
            {
                unit = this;
                return false;
            }
            unit = new V(X / l, Y / l, Z / l);
            return true;
        }

        /// <summary>PrcPt::MakeOrthoRep: Y and Z completing a unit X, from the world Y axis (or X when parallel).</summary>
        public static bool MakeOrthoRep(V x, out V y, out V z)
        {
            y = new V(0, 1, 0);
            z = default;
            if (!x.TryUnitize(out var sx)) return false;
            if (!Cross(sx, y).TryUnitize(out z))
            {
                y = new V(1, 0, 0);
                if (!Cross(sx, y).TryUnitize(out z)) return false;
            }
            return Cross(z, sx).TryUnitize(out y);
        }

        /// <summary>PrcPt::AngleBetween.</summary>
        public static double AngleBetween(V a, V b)
        {
            var la = a.AnnexLength();
            var lb = b.AnnexLength();
            if (la < 1.1920928955078125e-07 || lb < 1.1920928955078125e-07) return 0;
            var dot = Dot(new V(a.X / la, a.Y / la, a.Z / la), new V(b.X / lb, b.Y / lb, b.Z / lb));
            if (dot > 1.0 - 1e-12) dot = 1.0;
            else if (dot < -1.0 + 1e-12) dot = -1.0;
            return Math.Acos(dot);
        }
    }
}
