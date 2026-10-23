namespace EasyPDF.Prc;

/// <summary>
/// PRC_TYPE_TESS_3D (ISO 14739-1 7.8.4 to 7.8.6): coordinates and normals as plain arrays, and per
/// face a list of triangles, fans and strips whose corners index them (a multiple of 3 each).
/// </summary>
internal static class PrcTessellation3D
{
    private const uint Triangle = 0x0002, Fan = 0x0004, Strip = 0x0008;
    private const uint TriangleOneNormal = 0x0020, FanOneNormal = 0x0040, StripOneNormal = 0x0080;
    private const uint Textured = 0x0200 | 0x0400 | 0x0800 | 0x2000 | 0x4000 | 0x8000;
    private const uint SingleNormal = 0x40000000;

    /// <summary>Reads the entity after its type, which the caller has read.</summary>
    public static PrcMesh Read(PrcBitReader r, uint authoringVersion)
    {
        r.Boolean(); // is_calculated
        var coordinates = Doubles(r);
        r.Boolean(); // has_faces
        if (authoringVersion >= 7039) r.Boolean(); // has_loops
        if (authoringVersion >= 7047 && r.Boolean())
        {
            r.Character();
            r.Double();
        }
        var normals = Doubles(r);
        var wires = r.UnsignedInteger();
        for (var i = 0; i < wires; i++) r.UnsignedInteger();
        var indices = new uint[r.UnsignedInteger()];
        for (var i = 0; i < indices.Length; i++) indices[i] = r.UnsignedInteger();

        var triangles = new List<int>();
        var cornerNormals = new List<float>();
        var faces = new List<int>();
        var styles = new List<int>();
        var faceCount = r.UnsignedInteger();
        for (var f = 0; f < faceCount; f++)
        {
            PrcSectionReader.Expect(r, PrcType.TessFace);
            var lineAttributes = new uint[r.UnsignedInteger()];
            for (var i = 0; i < lineAttributes.Length; i++) lineAttributes[i] = r.UnsignedInteger();
            r.UnsignedInteger(); // start of wire data
            var sizesWire = r.UnsignedInteger();
            for (var i = 0; i < sizesWire; i++) r.UnsignedInteger();
            var used = r.UnsignedInteger();
            var start = r.UnsignedInteger();
            var data = new uint[r.UnsignedInteger()];
            for (var i = 0; i < data.Length; i++) data[i] = r.UnsignedInteger();
            var textureIndices = (int)r.UnsignedInteger();
            if (r.Boolean()) throw new PrcFormatException("PRC vertex colours on faces are not supported yet");
            if (lineAttributes.Length > 0) r.UnsignedInteger(); // behaviour

            var face = new FaceBuilder(coordinates, normals, indices, (int)start, textureIndices, triangles, cornerNormals);
            var d = 0;
            var entity = 0;
            for (var bit = 0; bit < 16; bit++)
            {
                var type = 1u << bit;
                if ((used & type) == 0) continue;
                var textured = (type & Textured) != 0;
                var basic = textured ? type >> 8 : type;
                if (basic is Triangle or TriangleOneNormal)
                {
                    var count = data[d++];
                    for (var i = 0; i < count; i++)
                    {
                        face.Triangle(basic == TriangleOneNormal, textured);
                        Style(entity++);
                    }
                }
                else if (basic is Fan or Strip or FanOneNormal or StripOneNormal)
                {
                    var groups = data[d++];
                    for (var g = 0; g < groups; g++)
                    {
                        var n = data[d++];
                        var oneNormal = basic is FanOneNormal or StripOneNormal;
                        var made = face.Group(basic is Fan or FanOneNormal, oneNormal, oneNormal && (n & SingleNormal) != 0, (int)(n & 0x3FFFFFFF), textured);
                        for (var i = 0; i < made; i++) Style(entity);
                        entity++;
                    }
                }
                else throw new PrcFormatException($"PRC face tessellation kind {type:X} not supported");
            }

            void Style(int e)
            {
                faces.Add(f);
                styles.Add(lineAttributes.Length switch
                {
                    0 => -1,
                    1 => (int)lineAttributes[0] - 1,
                    _ => e < lineAttributes.Length ? (int)lineAttributes[e] - 1 : -1,
                });
            }
        }
        var textures = r.UnsignedInteger();
        for (var i = 0; i < textures; i++) r.Double();

        return new PrcMesh
        {
            Positions = coordinates,
            Triangles = [.. triangles],
            CornerNormals = cornerNormals.Count == triangles.Count * 3 ? [.. cornerNormals] : null,
            TriangleFaces = [.. faces],
            TriangleStyles = [.. styles],
        };
    }

    private static double[] Doubles(PrcBitReader r)
    {
        var count = r.UnsignedInteger();
        if (count > r.Remaining) throw new PrcFormatException("PRC array longer than its section");
        var a = new double[count];
        for (var i = 0; i < a.Length; i++) a[i] = r.Double();
        return a;
    }

    /// <summary>Walks a face's corner indices, from its start in the tessellation's index array.</summary>
    private sealed class FaceBuilder(double[] coordinates, double[] normals, uint[] indices, int cursor, int textureIndices, List<int> triangles, List<float> cornerNormals)
    {
        private readonly List<(int Point, int Normal)> _corners = new();

        public void Triangle(bool oneNormal, bool textured)
        {
            _corners.Clear();
            var normal = oneNormal ? Next() : -1;
            for (var k = 0; k < 3; k++)
            {
                if (!oneNormal) normal = Next();
                if (textured) cursor += textureIndices;
                _corners.Add((Point(Next()), normal));
            }
            Emit(0, 1, 2);
        }

        /// <summary>A fan or strip of <paramref name="n"/> corners; returns its triangle count.</summary>
        public int Group(bool fan, bool oneNormal, bool single, int n, bool textured)
        {
            _corners.Clear();
            var normal = single ? Next() : -1;
            for (var k = 0; k < n; k++)
            {
                if (!single) normal = Next();
                if (textured) cursor += textureIndices;
                _corners.Add((Point(Next()), normal));
            }
            for (var k = 2; k < n; k++)
            {
                if (fan) Emit(0, k - 1, k);
                else if (k % 2 == 0) Emit(k - 2, k - 1, k);
                else Emit(k - 1, k - 2, k);
            }
            // A one-normal fan or strip without the single flag gives each triangle the normal of its last corner.
            if (oneNormal && !single)
                for (var t = 0; t < n - 2; t++)
                {
                    var last = cornerNormals.Count - (n - 2 - t) * 9;
                    var normalIndex = _corners[t + 2].Normal;
                    for (var c = 0; c < 3; c++) SetNormal(last + c * 3, normalIndex);
                }
            return Math.Max(0, n - 2);
        }

        private int Next()
        {
            if (cursor >= indices.Length) throw new PrcFormatException("PRC face tessellation runs past its indices");
            return (int)indices[cursor++];
        }

        private int Point(int index)
        {
            if (index % 3 != 0 || index + 2 >= coordinates.Length) throw new PrcFormatException("PRC point index out of range");
            return index / 3;
        }

        private void Emit(int a, int b, int c)
        {
            foreach (var k in (ReadOnlySpan<int>)[a, b, c])
            {
                triangles.Add(_corners[k].Point);
                var n = _corners[k].Normal;
                cornerNormals.Add(0);
                cornerNormals.Add(0);
                cornerNormals.Add(0);
                SetNormal(cornerNormals.Count - 3, n);
            }
        }

        private void SetNormal(int at, int index)
        {
            if (index < 0 || index + 2 >= normals.Length) return;
            cornerNormals[at] = (float)normals[index];
            cornerNormals[at + 1] = (float)normals[index + 1];
            cornerNormals[at + 2] = (float)normals[index + 2];
        }
    }
}
