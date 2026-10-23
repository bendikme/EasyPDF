using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace EasyPDF.Prc;

/// <summary>A 4 x 4 transformation, row-major, applied to column vectors: p' = M p.</summary>
internal readonly record struct PrcMatrix(double[] M)
{
    public static PrcMatrix Identity => new([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]);

    public static PrcMatrix operator *(PrcMatrix a, PrcMatrix b)
    {
        var m = new double[16];
        for (var r = 0; r < 4; r++)
        for (var c = 0; c < 4; c++)
        {
            double s = 0;
            for (var k = 0; k < 4; k++) s += a.M[r * 4 + k] * b.M[k * 4 + c];
            m[r * 4 + c] = s;
        }
        return new PrcMatrix(m);
    }

    public (double X, double Y, double Z) Apply(double x, double y, double z)
    {
        var m = M;
        var w = m[12] * x + m[13] * y + m[14] * z + m[15];
        if (w == 0) w = 1;
        return ((m[0] * x + m[1] * y + m[2] * z + m[3]) / w,
                (m[4] * x + m[5] * y + m[6] * z + m[7]) / w,
                (m[8] * x + m[9] * y + m[10] * z + m[11]) / w);
    }
}

internal readonly record struct PrcColor(double R, double G, double B);

/// <summary>Polylines (a PRC_TYPE_TESS_3D_Wire): coordinates, and each line's count with flags followed by its points.</summary>
internal sealed record PrcWire(double[] Coordinates, uint[] Indices);

/// <summary>An entity's graphics: its layer and style (both biased: 0 is none) and how it inherits them.</summary>
internal sealed record PrcGraphics(uint Layer, uint Style, ushort Behavior)
{
    public static readonly PrcGraphics None = new(0, 0, 1);

    public bool Shown => (Behavior & 0x0001) != 0;
    public bool Removed => (Behavior & 0x2000) != 0;
    public bool ParentColorWins => (Behavior & 0x0010) != 0;
}

/// <summary>A style: a colour (or a material's diffuse colour) and an opacity.</summary>
internal sealed record PrcStyle(bool IsMaterial, uint ColorOrMaterial, int? Transparency);

internal sealed record PrcMaterial(uint Diffuse, double DiffuseAlpha);

internal sealed class PrcItem
{
    public required uint Type { get; init; }
    public required string Name { get; init; }
    public required PrcGraphics Graphics { get; init; }
    public required uint LocalCoordinateSystem { get; init; }
    public required uint Tessellation { get; init; }
    public List<PrcItem> Children { get; } = [];
}

internal sealed class PrcPart
{
    public required string Name { get; init; }
    public required PrcGraphics Graphics { get; init; }
    public List<PrcItem> Items { get; } = [];
}

/// <summary>A file structure named by its unique id, or the one the reference is in (null).</summary>
internal readonly record struct PrcFileRef(uint[]? Id);

internal sealed class PrcOccurrence
{
    public required string Name { get; init; }
    public required PrcGraphics Graphics { get; init; }
    public required uint Part { get; init; }
    public required uint Prototype { get; init; }
    public PrcFileRef PrototypeFile { get; init; }
    public required uint External { get; init; }
    public PrcFileRef ExternalFile { get; init; }
    public required uint[] Children { get; init; }
    public PrcMatrix? Location { get; init; }
}

internal sealed class PrcFileStructure
{
    public required uint[] Id { get; init; }
    public uint AuthoringVersion { get; set; }
    public List<PrcColor> Colors { get; } = [];
    public List<PrcMaterial?> Materials { get; } = [];
    public List<PrcStyle> Styles { get; } = [];
    public List<PrcMatrix> CoordinateSystems { get; } = [];
    public List<PrcPart> Parts { get; } = [];
    public List<PrcOccurrence> Occurrences { get; } = [];
    public List<object?> Tessellations { get; } = [];
}

/// <summary>
/// A PRC file (ISO 14739-1): a header naming its file structures and where each one's sections
/// start, then the sections, each compressed with zlib - the globals (colours, styles, coordinate
/// systems), the tree (parts and product occurrences), the tessellations and the exact geometry -
/// and a model file naming the root product occurrences. Only what is needed to draw the model's
/// tessellations is kept; the exact geometry is not read.
/// </summary>
internal sealed class PrcFile
{
    public uint AuthoringVersion { get; private init; }
    public List<PrcFileStructure> FileStructures { get; } = [];
    public List<(uint[] Id, uint Index, bool Active)> Roots { get; } = [];
    public double UnitsInMm { get; private set; } = 1;

    public static PrcFile Read(byte[] data)
    {
        if (data.Length < 47 || data[0] != 'P' || data[1] != 'R' || data[2] != 'C') throw new PrcFormatException("Not a PRC file");
        var at = 3;
        uint U32()
        {
            if (at + 4 > data.Length) throw new PrcFormatException("PRC header ends early");
            var v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
            at += 4;
            return v;
        }
        uint[] Id() => [U32(), U32(), U32(), U32()];

        U32(); // minimal version for read
        var file = new PrcFile { AuthoringVersion = U32() };
        Id();
        Id();
        var structureCount = U32();
        if (structureCount > data.Length / 24) throw new PrcFormatException("PRC header damaged");
        var sections = new List<uint[]>();
        for (var i = 0; i < structureCount; i++)
        {
            var id = Id();
            U32(); // reserved
            var count = U32();
            if (count is < 6 or > 64) throw new PrcFormatException("PRC header damaged");
            var offsets = new uint[count];
            for (var k = 0; k < count; k++) offsets[k] = U32();
            sections.Add(offsets);
            file.FileStructures.Add(new PrcFileStructure { Id = id, AuthoringVersion = file.AuthoringVersion });
        }
        var modelStart = U32();
        var modelEnd = U32();

        // Each section runs to the next one's start, whichever structure it belongs to.
        var starts = sections.SelectMany(s => s).Append(modelStart).Append(modelEnd).Append((uint)data.Length).Distinct().Order().ToArray();
        byte[] Inflate(uint start)
        {
            if (start >= data.Length) throw new PrcFormatException("PRC section outside the file");
            var end = starts.First(s => s > start);
            using var input = new MemoryStream(data, (int)start, (int)(end - start));
            using var z = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            z.CopyTo(output);
            return output.ToArray();
        }

        for (var i = 0; i < file.FileStructures.Count; i++)
        {
            var fs = file.FileStructures[i];
            var offsets = sections[i];
            // The structure's own header is not compressed; it carries the version it was written for.
            var h = (int)offsets[0];
            if (h + 11 <= data.Length && data[h] == 'P' && data[h + 1] == 'R' && data[h + 2] == 'C')
                fs.AuthoringVersion = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(h + 7));
            var reader = new PrcSectionReader(fs);
            try
            {
                reader.ReadGlobals(new PrcBitReader(Inflate(offsets[1])));
                reader.ReadTree(new PrcBitReader(Inflate(offsets[2])));
                reader.ReadTessellations(new PrcBitReader(Inflate(offsets[3])));
            }
            catch (PrcFormatException e)
            {
                throw new PrcFormatException($"File structure {i}: {e.Message}");
            }
        }

        var model = new PrcBitReader(Inflate(modelStart));
        PrcSectionReader.Schema(model);
        PrcSectionReader.Expect(model, PrcType.ModelFile);
        new PrcSectionReader(file.FileStructures.FirstOrDefault() ?? new PrcFileStructure { Id = [0, 0, 0, 0] }).ContentBase(model);
        model.Boolean(); // units from the CAD file
        file.UnitsInMm = model.Double();
        var roots = model.UnsignedInteger();
        for (var i = 0; i < roots; i++)
        {
            uint[] id = [model.UnsignedInteger(), model.UnsignedInteger(), model.UnsignedInteger(), model.UnsignedInteger()];
            file.Roots.Add((id, model.UnsignedInteger(), model.Boolean()));
        }
        return file;
    }

    public PrcFileStructure? Find(uint[] id) => FileStructures.FirstOrDefault(f => f.Id.AsSpan().SequenceEqual(id));
}

internal static class PrcType
{
    public const uint TessBase = 170;
    public const uint Tess3D = 172;
    public const uint Tess3DCompressed = 173;
    public const uint TessFace = 174;
    public const uint Tess3DWire = 175;
    public const uint TessMarkup = 176;
    public const uint SurfacePlane = 86;
    public const uint Attribute = 201;
    public const uint CartesianTransformation = 202;
    public const uint EntityReference = 203;
    public const uint MarkupLinkedItem = 204;
    public const uint ReferenceOnPRCBase = 205;
    public const uint ReferenceOnTopology = 206;
    public const uint GeneralTransformation = 207;
    public const uint BrepModel = 232;
    public const uint Curve = 233;
    public const uint Direction = 234;
    public const uint Plane = 235;
    public const uint PointSet = 236;
    public const uint PolyBrepModel = 237;
    public const uint PolyWire = 238;
    public const uint Set = 239;
    public const uint CoordinateSystem = 240;
    public const uint ModelFile = 301;
    public const uint FileStructure = 302;
    public const uint Globals = 303;
    public const uint Tree = 304;
    public const uint Tessellation = 305;
    public const uint ProductOccurrence = 310;
    public const uint PartDefinition = 311;
    public const uint Filter = 320;
    public const uint View = 501;
    public const uint Markup = 502;
    public const uint Leader = 503;
    public const uint AnnotationItem = 504;
    public const uint AnnotationSet = 505;
    public const uint AnnotationReference = 506;
    public const uint Style = 701;
    public const uint Material = 702;
    public const uint Picture = 703;
    public const uint TextureApplication = 711;
    public const uint LinePattern = 721;
    public const uint AmbientLight = 731;
    public const uint PointLight = 732;
    public const uint DirectionalLight = 733;
    public const uint SpotLight = 734;
    public const uint SceneDisplayParameters = 741;
    public const uint Camera = 742;
}
