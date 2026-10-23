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

/// <summary>What a decode consumed and produced, against what the entity holds.</summary>
internal sealed record PrcDecodeReport(
    int Triangles, int TrianglesExpected,
    int PointTriples, int PointTriplesStored,
    int ReferenceFlags, int ReferenceFlagsStored,
    int References, int ReferencesStored,
    int NormalBits, int NormalBitsStored,
    int NormalAngles, int NormalAnglesStored,
    string? Failure)
{
    /// <summary>Every stored value used, no more and no less, and every triangle made: a decode that went astray cannot do this.</summary>
    public bool Exact => Failure is null && Triangles == TrianglesExpected && PointTriples == PointTriplesStored
        && ReferenceFlags == ReferenceFlagsStored && References == ReferencesStored
        && NormalBits == NormalBitsStored && NormalAngles == NormalAnglesStored;
}

/// <summary>
/// PRC_TYPE_TESS_3D_Compressed, the "highly compressed tessellation" (ISO 14739-1 7.8.9): a mesh
/// stored as a traversal from triangle to neighbouring triangle, each new point given in the frame
/// of the edge it is reached through and quantised to a tolerance, each normal as two angles in a
/// frame of its triangle. The traversal follows the standard as corrected by the PDF Association's
/// errata (pdf-issues #436, #540, #727, #772, #773, #774, #775), and where those are silent or
/// disagree, what reproduces real writers' files exactly - every stored value consumed and every
/// point on the part's own edge lines - across the 415 meshes of an EPLAN export.
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

    /// <summary>
    /// Decodes the mesh. The report says what was consumed: a correct decode uses every stored
    /// point, flag, reference, normal bit and angle exactly and produces every triangle.
    /// </summary>
    public PrcMesh Decode(out PrcDecodeReport report)
    {
        var d = new Decoder(this);
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
            failure);
        return d.Result();
    }

    /// <summary>
    /// The traversal. Each triangle is a seed (three points, new or referred to) when the stack of
    /// open edges is empty, else is built on the edge popped from it; its corners' normals are then
    /// read, its orientation follows from its first corner's normal, and its edge-status flags say
    /// which of its two other edges have a neighbour still to come. Where the standard is silent or
    /// wrong, the rules here are the ones that reproduce real files exactly (see the notes on each).
    /// </summary>
    private sealed class Decoder(PrcCompressedTessellation t)
    {
        private readonly List<V> _points = new(t.PointArray.Length / 3 + 3);
        private readonly List<int> _triangles = new(t.TriangleFaces.Length * 3);
        private readonly List<float> _normals = new();
        private readonly List<int> _faces = new(t.TriangleFaces.Length);
        private readonly Stack<(int Lo, int Hi, int Far)> _stack = new();
        private readonly HashSet<long> _closed = new();
        private readonly Dictionary<int, StoredNormals> _vertexNormals = new();
        private readonly Dictionary<int, StoredNormals> _faceNormals = new();

        public int TriangleCount;
        public int PointCursor;
        public int FlagCursor;
        public int ReferenceCursor;
        public int NormalBitCursor;
        public int AngleCursor;

        /// <summary>The distinct normals stored so far for a vertex (or a planar face), oldest first.</summary>
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
                if (!NextGate(out var gate))
                {
                    Seed(tri);
                }
                else
                {
                    tri[0] = gate.Lo;
                    tri[1] = gate.Hi;
                    tri[2] = NextFlag() ? Reference() : AddPoint(Apex(_points[gate.Lo], _points[gate.Hi], _points[gate.Far], NextTriple()));
                    _closed.Add(EdgeKey(gate.Lo, gate.Hi));
                }

                bool reversed;
                if (t.MustRecalculateNormals)
                {
                    // Read in bulk before the traversal (pdf-issues #772).
                    reversed = t.NormalIsReversed[n];
                }
                else
                {
                    var planar = t.FaceIsPlanar[face];
                    var newFace = planar && !_faceNormals.ContainsKey(face);
                    for (var k = 0; k < 3; k++) corner[k] = RestoreNormal(tri, k, face);
                    if (planar)
                    {
                        corner[1] = corner[2] = corner[0];
                        if (newFace) ShareFaceNormal(tri[0], face, corner[0]);
                    }
                    // The triangle's normal against its first corner's (7.8.9.2): exactly zero, as on
                    // a triangle with its points in line, is not reversed.
                    reversed = V.Dot(DeriveNormal(_points[tri[0]], _points[tri[1]], _points[tri[2]]), corner[0]) < 0;
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
        /// with several; each is a style's index plus one.
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
                    styles[i] = cursor < a.Length ? a[cursor++] - 1 : -1;
                }
                else
                {
                    if (!faceStyle.TryGetValue(face, out var s))
                    {
                        s = cursor < a.Length ? a[cursor++] - 1 : -1;
                        faceStyle[face] = s;
                    }
                    styles[i] = s;
                }
            }
            return styles;
        }

        /// <summary>The next open edge; one already closed from its other side since it was pushed is passed over.</summary>
        private bool NextGate(out (int Lo, int Hi, int Far) gate)
        {
            while (_stack.Count > 0)
            {
                gate = _stack.Pop();
                if (!_closed.Contains(EdgeKey(gate.Lo, gate.Hi))) return true;
            }
            gate = default;
            return false;
        }

        /// <summary>
        /// A triangle not reached through an edge: the first of each connected piece of the mesh
        /// (pdf-issues #774). Its three points each are referred to or new: the first from the
        /// stored origin, the second from the first, the third from their middle.
        /// </summary>
        private void Seed(int[] tri)
        {
            var origin = new V(t.Origin[0], t.Origin[1], t.Origin[2]);
            bool r0 = NextFlag(), r1 = NextFlag(), r2 = NextFlag();
            tri[0] = r0 ? Reference() : AddPoint(origin + Offset(NextTriple()));
            tri[1] = r1 ? Reference() : AddPoint(_points[tri[0]] + Offset(NextTriple()));
            tri[2] = r2 ? Reference() : AddPoint((_points[tri[0]] + _points[tri[1]]) * 0.5 + Offset(NextTriple()));
            if (tri[0] > tri[1]) (tri[0], tri[1]) = (tri[1], tri[0]);
        }

        private V Offset(V triple) => new(triple.X * t.Tolerance, triple.Y * t.Tolerance, triple.Z * t.Tolerance);

        /// <summary>
        /// The pushes for the triangle's two forward edges: right is (1, 2) and left (0, 2), swapped
        /// when the triangle is reversed. Right is pushed first, so left is treated first
        /// (pdf-issues #727). An edge without a neighbour still to come is closed.
        /// </summary>
        private void EvaluateAdjacent(int[] tri, bool reversed, int status)
        {
            var right = (A: tri[1], B: tri[2], Far: tri[0]);
            var left = (A: tri[0], B: tri[2], Far: tri[1]);
            if (reversed) (right, left) = (left, right);
            Offer(right, (status & 1) != 0);
            Offer(left, (status & 2) != 0);
        }

        private void Offer((int A, int B, int Far) edge, bool hasNeighbour)
        {
            var (lo, hi) = edge.A < edge.B ? (edge.A, edge.B) : (edge.B, edge.A);
            if (hasNeighbour) _stack.Push((lo, hi, edge.Far));
            else _closed.Add(EdgeKey(lo, hi));
        }

        /// <summary>
        /// A new point in the frame of the edge it is reached through (7.8.9.3, formulas (1) to (3)):
        /// origin at the edge's middle, X from the lower-numbered end, Z across the plane of the
        /// triangle already on the other side, Y completing it - then turned half a turn about Z, the
        /// frame real files are encoded against (pdf-issues #773). When the far point is in line with
        /// the edge, MakeOrthoRep of the final X stands in, with Y turned towards the far point. The
        /// arithmetic is the standard's, to the order of the last sum: thin triangles magnify any
        /// difference in the last digits from one point to the next.
        /// </summary>
        private V Apex(V a, V b, V far, V triple)
        {
            var origin = (a + b) * 0.5;
            (b - a).TryUnitize(out var xs);
            V x = -xs, y, z;
            if (V.Cross(far - origin, xs).TryUnitize(out var zs) && V.Cross(zs, xs).TryUnitize(out var ys))
            {
                y = -ys;
                z = zs;
            }
            else
            {
                V.MakeOrthoRep(x, out y, out z);
                if (V.Dot(y, origin - far) > 0)
                {
                    y = -y;
                    z = -z;
                }
            }
            return origin + x * (triple.X * t.Tolerance) + y * (triple.Y * t.Tolerance) + z * (triple.Z * t.Tolerance);
        }

        /// <summary>Derive_Normal of the 2026 errata: the triangle's normal from the middle of its first edge, facing the winding (0, 2, 1).</summary>
        private static V DeriveNormal(V v1, V v2, V v3) => V.Cross(v3 - (v1 + v2) * 0.5, v2 - v1);

        /// <summary>
        /// The normal of corner <paramref name="k"/> (7.8.9.4, pdf-issues #436): read new the first
        /// time its vertex is met, with a bit saying whether it has more than one; after that, if it
        /// has, either a reference to one of its stored normals or a new one. A planar face stores
        /// one normal, read at the first corner of its first triangle.
        /// </summary>
        private V RestoreNormal(int[] tri, int k, int face)
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
            // A planar face's later triangles read nothing, whatever its has-multiple bit says.
            if (!stored.HasMultiple || planar) return stored.Normals[0];
            if (NextNormalBit())
            {
                // Counted back from the newest stored normal, least significant bit first.
                var max = stored.Normals.Count - 1;
                var bits = ReferenceWidth(max);
                var read = 0;
                for (var i = 0; i < bits; i++) if (NextNormalBit()) read |= 1 << i;
                var index = max - read;
                if (index < 0) throw new PrcFormatException("PRC normal reference out of range");
                return stored.Normals[index];
            }
            var next = NextNormal(tri);
            stored.Normals.Add(next);
            return next;
        }

        /// <summary>
        /// A planar face's normal, just read at the first corner of its first triangle, is also one of
        /// that corner's vertex's normals: added to them, or the first it is met with.
        /// </summary>
        private void ShareFaceNormal(int vertex, int face, V normal)
        {
            if (_vertexNormals.TryGetValue(vertex, out var stored))
            {
                if (stored.HasMultiple && !stored.Normals.Contains(normal)) stored.Normals.Add(normal);
                return;
            }
            var created = new StoredNormals { HasMultiple = _faceNormals[face].HasMultiple };
            created.Normals.Add(normal);
            _vertexNormals[vertex] = created;
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

        /// <summary>
        /// A normal given by two angles in a frame built from the triangle's sides around its most
        /// nearly right angle (7.8.9.4, as the standard words the choice), Z the triangle's normal
        /// flipped by the first bit, X and Y by the next two. Real writers take the angles' sine and
        /// cosine from the maths library, not from the four-term series the errata propose.
        /// </summary>
        private V NextNormal(int[] tri)
        {
            var reversedTriangle = NextNormalBit();
            var reversedX = NextNormalBit();
            var reversedY = NextNormalBit();
            V p0 = _points[tri[0]], p1 = _points[tri[1]], p2 = _points[tri[2]];
            (p1 - p0).TryUnitize(out var v1);
            (p2 - p0).TryUnitize(out var v2);
            (p2 - p1).TryUnitize(out var v3);
            var theta1 = Math.Abs(V.AngleBetween(v1, v2) - Math.PI / 2);
            var theta2 = Math.Abs(V.AngleBetween(v3, -v1) - Math.PI / 2);
            var theta3 = Math.Abs(V.AngleBetween(-v2, -v3) - Math.PI / 2);
            V x, z;
            if (theta1 < theta2 && theta1 < theta3)
            {
                x = v1;
                z = V.Cross(v1, v2);
            }
            else if (theta2 < theta3)
            {
                x = v3;
                z = V.Cross(-v3, v1);
            }
            else
            {
                x = -v2;
                z = V.Cross(v2, v3);
            }
            if (!z.TryUnitize(out z)) V.MakeOrthoRep(x, out _, out z);
            if (reversedTriangle) z = -z;
            if (!V.Cross(z, x).TryUnitize(out var y)) V.MakeOrthoRep(x, out y, out z);
            if (reversedX) x = -x;
            if (reversedY) y = -y;

            if (AngleCursor + 2 > t.NormalAngles.Length) throw new PrcFormatException("PRC normal angles run out");
            var scale = Math.PI / 2 / ((1 << t.NormalAngleBits) - 1);
            var theta = t.NormalAngles[AngleCursor++] * scale;
            var phi = t.NormalAngles[AngleCursor++] * scale;
            return x * (Math.Cos(theta) * Math.Cos(phi)) + y * (Math.Sin(theta) * Math.Cos(phi)) + z * Math.Sin(phi);
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

        /// <summary>A point met before, by its index.</summary>
        private int Reference()
        {
            if (ReferenceCursor >= t.PointReferences.Length) throw new PrcFormatException("PRC point references run out");
            var index = t.PointReferences[ReferenceCursor++];
            if (index < 0 || index >= _points.Count) throw new PrcFormatException("PRC point reference out of range");
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
            _points.Add(p);
            return _points.Count - 1;
        }

        private static long EdgeKey(int a, int b) => a < b ? (long)a << 32 | (uint)b : (long)b << 32 | (uint)a;
    }

    private readonly record struct V(double X, double Y, double Z)
    {
        private const double FltEpsilon = 1.1920928955078125e-07;

        public static V operator +(V a, V b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static V operator -(V a, V b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V operator -(V a) => new(-a.X, -a.Y, -a.Z);
        public static V operator *(V a, double s) => new(a.X * s, a.Y * s, a.Z * s);
        public static double Dot(V a, V b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static V Cross(V a, V b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        /// <summary>
        /// The standard's length (ISO 14739-1 12.3, PrcPt::Length): the square root, by Newton's
        /// method, of the squared length rounded to a single. Writers compute with it, so the points
        /// they encode against carry its rounding; -1 when the iteration does not settle.
        /// </summary>
        public double Length()
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
            var l = Length();
            if (l < FltEpsilon)
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
            var la = a.Length();
            var lb = b.Length();
            if (la < FltEpsilon || lb < FltEpsilon) return 0;
            var dot = Dot(new V(a.X / la, a.Y / la, a.Z / la), new V(b.X / lb, b.Y / lb, b.Z / lb));
            if (dot > 1.0 - 1e-12) dot = 1.0;
            else if (dot < -1.0 + 1e-12) dot = -1.0;
            return Math.Acos(dot);
        }
    }
}
