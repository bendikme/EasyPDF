namespace EasyPDF.Prc;

/// <summary>A colour with opacity, components 0 to 1.</summary>
internal readonly record struct PrcRgba(double R, double G, double B, double A);

/// <summary>One placed mesh of the model: its triangles, where it is, and the colour of each triangle.</summary>
internal sealed record PrcMeshInstance(PrcMesh Mesh, PrcMatrix Transform, PrcRgba[] TriangleColors, string Name);

/// <summary>One placed set of edge lines.</summary>
internal sealed record PrcWireInstance(PrcWire Wire, PrcMatrix Transform, PrcRgba Color);

/// <summary>
/// The model a PRC file draws: its product occurrences walked from the roots the model file names,
/// each placed by its own location and its parents', a prototype or external file structure
/// standing in for the occurrence's content, and each representation item's tessellation decoded
/// and coloured by its style.
/// </summary>
internal sealed class PrcScene
{
    public List<PrcMeshInstance> Meshes { get; } = [];
    public List<PrcWireInstance> Wires { get; } = [];

    /// <summary>The decode of each compressed tessellation, shared by every occurrence of it.</summary>
    private readonly Dictionary<PrcCompressedTessellation, PrcMesh?> _decoded = new(ReferenceEqualityComparer.Instance);
    private Func<PrcDecodeReport, bool>? _accept;

    public static readonly PrcRgba DefaultColor = new(0.75, 0.75, 0.75, 1);

    public static PrcScene Build(PrcFile file, PrcDecodeOptions options, Action<PrcCompressedTessellation, PrcDecodeReport>? report = null, Func<PrcDecodeReport, bool>? accept = null)
    {
        var scene = new PrcScene { _accept = accept };
        foreach (var (id, index, active) in file.Roots)
        {
            if (!active || file.Find(id) is not { } fs || index == 0 || index > fs.Occurrences.Count) continue;
            scene.Occurrence(file, fs, (int)index - 1, PrcMatrix.Identity, null, 0, options, report);
        }
        return scene;
    }

    private void Occurrence(PrcFile file, PrcFileStructure fs, int index, PrcMatrix parent, PrcStyleRef? inherited, int depth, PrcDecodeOptions options, Action<PrcCompressedTessellation, PrcDecodeReport>? report)
    {
        if (depth > 64 || index < 0 || index >= fs.Occurrences.Count) return;
        var po = fs.Occurrences[index];
        if (po.Graphics.Removed) return;
        var m = po.Location is { } location ? parent * location : parent;
        var style = Inherit(inherited, fs, po.Graphics);

        if (po.Part != 0 && po.Part <= fs.Parts.Count)
        {
            var part = fs.Parts[(int)po.Part - 1];
            var partStyle = Inherit(style, fs, part.Graphics);
            foreach (var item in part.Items) Item(fs, item, m, partStyle, po.Name, options, report);
        }
        if (po.Prototype != 0 && Target(file, fs, po.PrototypeFile) is { } prototype)
            Occurrence(file, prototype, (int)po.Prototype - 1, m, style, depth + 1, options, report);
        if (po.External != 0 && Target(file, fs, po.ExternalFile) is { } external)
            Occurrence(file, external, (int)po.External - 1, m, style, depth + 1, options, report);
        foreach (var child in po.Children)
            Occurrence(file, fs, (int)child, m, style, depth + 1, options, report);
    }

    private static PrcFileStructure? Target(PrcFile file, PrcFileStructure fs, PrcFileRef reference) => reference.Id is { } id ? file.Find(id) : fs;

    private void Item(PrcFileStructure fs, PrcItem item, PrcMatrix parent, PrcStyleRef? inherited, string name, PrcDecodeOptions options, Action<PrcCompressedTessellation, PrcDecodeReport>? report)
    {
        if (item.Graphics.Removed) return;
        var m = item.LocalCoordinateSystem != 0 && item.LocalCoordinateSystem <= fs.CoordinateSystems.Count
            ? parent * fs.CoordinateSystems[(int)item.LocalCoordinateSystem - 1]
            : parent;
        var style = Inherit(inherited, fs, item.Graphics);
        foreach (var child in item.Children) Item(fs, child, m, style, name, options, report);
        if (item.Tessellation == 0 || item.Tessellation > fs.Tessellations.Count) return;

        switch (fs.Tessellations[(int)item.Tessellation - 1])
        {
            case PrcCompressedTessellation compressed:
                if (!_decoded.TryGetValue(compressed, out var mesh))
                {
                    mesh = compressed.Decode(options, out var r);
                    report?.Invoke(compressed, r);
                    if (_accept?.Invoke(r) == false) mesh = null;
                    _decoded[compressed] = mesh;
                }
                if (mesh is { Triangles.Length: > 0 }) Meshes.Add(new PrcMeshInstance(mesh, m, Colors(fs, mesh, style), name));
                break;
            case PrcMesh plain:
                if (plain.Triangles.Length > 0) Meshes.Add(new PrcMeshInstance(plain, m, Colors(fs, plain, style), name));
                break;
            case PrcWire wire:
                Wires.Add(new PrcWireInstance(wire, m, Resolve(fs, style) ?? new PrcRgba(0, 0, 0, 1)));
                break;
        }
    }

    /// <summary>Each triangle's colour: its own line attribute's style, else the one it inherits.</summary>
    private static PrcRgba[] Colors(PrcFileStructure fs, PrcMesh mesh, PrcStyleRef? inherited)
    {
        var fallback = Resolve(fs, inherited) ?? DefaultColor;
        var colors = new PrcRgba[mesh.TriangleStyles.Length];
        for (var i = 0; i < colors.Length; i++)
        {
            var s = mesh.TriangleStyles[i];
            // A style the parent forces on its children wins over the triangle's own.
            colors[i] = inherited is { Forced: true } || s < 0 ? fallback : Style(fs, s) ?? fallback;
        }
        return colors;
    }

    /// <summary>A style an entity names (biased by one), and whether it imposes it on what it holds.</summary>
    private sealed record PrcStyleRef(PrcFileStructure Fs, uint Style, bool Forced);

    private static PrcStyleRef? Inherit(PrcStyleRef? parent, PrcFileStructure fs, PrcGraphics graphics)
    {
        if (parent is { Forced: true }) return parent;
        if (graphics.Style == 0) return parent;
        return new PrcStyleRef(fs, graphics.Style, graphics.ParentColorWins);
    }

    private static PrcRgba? Resolve(PrcFileStructure fs, PrcStyleRef? style) => style is null ? null : Style(style.Fs, (int)style.Style - 1);

    /// <summary>A style's colour: a colour index or a material's diffuse colour, both pointing at an RGB triple's first value.</summary>
    private static PrcRgba? Style(PrcFileStructure fs, int index)
    {
        if (index < 0 || index >= fs.Styles.Count) return null;
        var style = fs.Styles[index];
        double alpha = style.Transparency is { } t ? t / 255.0 : 1;
        uint colorIndex;
        if (style.IsMaterial)
        {
            if (style.ColorOrMaterial == 0 || style.ColorOrMaterial > fs.Materials.Count || fs.Materials[(int)style.ColorOrMaterial - 1] is not { } material) return null;
            colorIndex = material.Diffuse;
            if (style.Transparency is null) alpha = material.DiffuseAlpha;
        }
        else
        {
            colorIndex = style.ColorOrMaterial;
        }
        if (colorIndex == 0) return null;
        var c = (int)(colorIndex - 1) / 3;
        if (c >= fs.Colors.Count) return null;
        var rgb = fs.Colors[c];
        return new PrcRgba(rgb.R, rgb.G, rgb.B, Math.Clamp(alpha, 0, 1));
    }
}
