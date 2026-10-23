namespace EasyPDF.Prc;

/// <summary>
/// Reads one file structure's globals, tree and tessellation sections. PRC entities carry no
/// length, so each must be read field by field to reach the next; what is not needed to draw the
/// model is read and dropped. Field order follows ISO 14739-1 with its known errata, as prc-rs's
/// schema (MIT, Kristóf Ralovich) records them.
/// </summary>
internal sealed class PrcSectionReader(PrcFileStructure fs)
{
    private PrcGraphics _graphics = PrcGraphics.None;

    public static void Expect(PrcBitReader r, uint type)
    {
        var t = r.UnsignedInteger();
        if (t != type) throw new PrcFormatException($"PRC entity {t} where {type} was expected");
    }

    /// <summary>
    /// The schema that opens the globals and the model file: fields that later versions of the
    /// format add to entity types, which a file written for an older reader describes so it can
    /// skip them. A file that uses it is not read.
    /// </summary>
    public static void Schema(PrcBitReader r)
    {
        if (r.UnsignedInteger() != 0) throw new PrcFormatException("PRC schema extensions are not supported");
    }

    public void ReadGlobals(PrcBitReader r)
    {
        Schema(r);
        Expect(r, PrcType.Globals);
        ContentBase(r);
        var referenced = r.UnsignedInteger();
        for (var i = 0; i < referenced; i++) UniqueId(r);

        r.Double(); // tessellation chord height
        r.Double(); // tessellation angle
        r.String(); // default font family
        var fonts = r.UnsignedInteger();
        for (var i = 0; i < fonts; i++)
        {
            r.String();
            r.UnsignedInteger();
            var keys = r.UnsignedInteger();
            for (var k = 0; k < keys; k++)
            {
                r.UnsignedInteger();
                r.Character();
            }
        }
        var colors = r.UnsignedInteger();
        for (var i = 0; i < colors; i++) fs.Colors.Add(new PrcColor(r.Double(), r.Double(), r.Double()));
        var pictures = r.UnsignedInteger();
        for (var i = 0; i < pictures; i++)
        {
            Expect(r, PrcType.Picture);
            ContentBase(r);
            for (var k = 0; k < 4; k++) r.UnsignedInteger();
        }
        if (r.UnsignedInteger() != 0) throw new PrcFormatException("PRC texture definitions are not supported");
        var materials = r.UnsignedInteger();
        for (var i = 0; i < materials; i++)
        {
            var type = r.UnsignedInteger();
            RefBase(r);
            if (type == PrcType.Material)
            {
                r.UnsignedInteger(); // ambient
                var diffuse = r.UnsignedInteger();
                r.UnsignedInteger(); // emissive
                r.UnsignedInteger(); // specular
                r.Double(); // shininess
                r.Double(); // ambient alpha
                var alpha = r.Double();
                r.Double();
                r.Double();
                fs.Materials.Add(new PrcMaterial(diffuse, alpha));
            }
            else if (type == PrcType.TextureApplication)
            {
                for (var k = 0; k < 4; k++) r.UnsignedInteger();
                fs.Materials.Add(null);
            }
            else throw new PrcFormatException($"PRC material type {type} not known");
        }
        var patterns = r.UnsignedInteger();
        for (var i = 0; i < patterns; i++)
        {
            Expect(r, PrcType.LinePattern);
            RefBase(r);
            var n = r.UnsignedInteger();
            for (var k = 0; k < n; k++) r.Double();
            r.Double();
            r.Boolean();
        }
        var styles = r.UnsignedInteger();
        for (var i = 0; i < styles; i++)
        {
            Expect(r, PrcType.Style);
            RefBase(r);
            r.Double(); // line width
            r.Boolean(); // is a vpicture
            r.UnsignedInteger(); // pattern
            var isMaterial = r.Boolean();
            var color = r.UnsignedInteger();
            int? transparency = r.Boolean() ? (byte)r.Character() : null;
            if (r.Boolean()) r.Character();
            if (r.Boolean()) r.Character();
            if (r.Boolean()) r.Character();
            fs.Styles.Add(new PrcStyle(isMaterial, color, transparency));
        }
        if (r.UnsignedInteger() != 0) throw new PrcFormatException("PRC fill patterns are not supported");
        var systems = r.UnsignedInteger();
        for (var i = 0; i < systems; i++)
        {
            Expect(r, PrcType.CoordinateSystem);
            ItemContent(r);
            fs.CoordinateSystems.Add(Transformation(r));
            r.UserData();
        }
        r.UserData();
    }

    public void ReadTree(PrcBitReader r)
    {
        Expect(r, PrcType.Tree);
        ContentBase(r);
        var parts = r.UnsignedInteger();
        for (var i = 0; i < parts; i++)
        {
            Expect(r, PrcType.PartDefinition);
            var (name, graphics) = BaseWithGraphics(r);
            var part = new PrcPart { Name = name, Graphics = graphics };
            for (var k = 0; k < 6; k++) r.Double(); // bounding box
            var items = r.UnsignedInteger();
            for (var k = 0; k < items; k++) part.Items.Add(Item(r));
            Markups(r);
            Views(r);
            r.UserData();
            fs.Parts.Add(part);
        }
        var occurrences = r.UnsignedInteger();
        for (var i = 0; i < occurrences; i++) fs.Occurrences.Add(Occurrence(r));

        Expect(r, PrcType.FileStructure);
        ContentBase(r);
        r.UnsignedInteger(); // next available index
        r.UnsignedInteger(); // the structure's own product occurrence
        r.UserData();
    }

    public void ReadTessellations(PrcBitReader r)
    {
        Expect(r, PrcType.Tessellation);
        ContentBase(r);
        var count = r.UnsignedInteger();
        for (var i = 0; i < count; i++)
        {
            var type = r.UnsignedInteger();
            switch (type)
            {
                case PrcType.Tess3DCompressed:
                    fs.Tessellations.Add(PrcCompressedTessellation.Read(r, fs.AuthoringVersion));
                    break;
                case PrcType.Tess3DWire:
                    fs.Tessellations.Add(Wire(r));
                    break;
                case PrcType.Tess3D:
                    fs.Tessellations.Add(PrcTessellation3D.Read(r, fs.AuthoringVersion));
                    break;
                case PrcType.TessMarkup:
                    // The drawing of a markup (text and lines), which this reader does not show.
                    r.Boolean();
                    var coordinates = r.UnsignedInteger();
                    for (var k = 0; k < coordinates; k++) r.Double();
                    var codes = r.UnsignedInteger();
                    for (var k = 0; k < codes; k++) r.UnsignedInteger();
                    var texts = r.UnsignedInteger();
                    for (var k = 0; k < texts; k++) r.String();
                    r.String();
                    r.Character();
                    fs.Tessellations.Add(null);
                    break;
                default:
                    // Without lengths, an entity this reader cannot read hides all that follow it.
                    throw new PrcFormatException($"PRC tessellation type {type} is not supported yet");
            }
        }
        r.UserData();
    }

    /// <summary>
    /// Polylines: coordinates, then for each line its point count (with flags in the top bits)
    /// and the points' positions in the coordinates.
    /// </summary>
    private static PrcWire Wire(PrcBitReader r)
    {
        r.Boolean(); // is_calculated
        var coordinates = new double[r.UnsignedInteger()];
        for (var i = 0; i < coordinates.Length; i++) coordinates[i] = r.Double();
        var indices = new uint[r.UnsignedInteger()];
        for (var i = 0; i < indices.Length; i++) indices[i] = r.UnsignedInteger();
        if (r.Boolean())
        {
            var rgba = r.Boolean();
            var perSegment = r.Boolean();
            if (!r.Boolean()) // not "optimized"
            {
                var colors = 0;
                for (var i = 0; i < indices.Length; i++)
                {
                    var n = (int)(indices[i] & 0x0FFFFFFF);
                    colors += perSegment ? n - 1 : n;
                    i += n;
                }
                var size = rgba ? 4 : 3;
                r.Skip(size * 8);
                for (var i = 1; i < colors; i++)
                    if (!r.Boolean()) r.Skip(size * 8);
            }
        }
        return new PrcWire(coordinates, indices);
    }

    private PrcOccurrence Occurrence(PrcBitReader r)
    {
        Expect(r, PrcType.ProductOccurrence);
        var (name, graphics) = BaseWithGraphics(r);
        var part = r.UnsignedInteger();
        var prototype = r.UnsignedInteger();
        var prototypeFile = prototype != 0 ? FileIdentifier(r) : default;
        var external = r.UnsignedInteger();
        var externalFile = external != 0 ? FileIdentifier(r) : default;
        var children = new uint[r.UnsignedInteger()];
        for (var k = 0; k < children.Length; k++) children[k] = r.UnsignedInteger();
        r.Character(); // product behaviour
        r.Boolean(); // unit from the CAD file
        r.Double(); // unit
        r.Character(); // flags
        r.Integer(); // load status
        PrcMatrix? location = r.Boolean() ? TypedTransformation(r) : null;
        var references = r.UnsignedInteger();
        for (var k = 0; k < references; k++) EntityReference(r);
        Markups(r);
        Views(r);
        if (r.Boolean()) Filter(r);
        var filters = r.UnsignedInteger();
        for (var k = 0; k < filters; k++) Filter(r);
        var scenes = r.UnsignedInteger();
        for (var k = 0; k < scenes; k++) SceneDisplayParameters(r);
        r.UserData();
        return new PrcOccurrence
        {
            Name = name,
            Graphics = graphics,
            Part = part,
            Prototype = prototype,
            PrototypeFile = prototypeFile,
            External = external,
            ExternalFile = externalFile,
            Children = children,
            Location = location,
        };
    }

    private PrcItem Item(PrcBitReader r)
    {
        var type = r.UnsignedInteger();
        var (name, graphics, system, tessellation) = ItemContent(r);
        var item = new PrcItem { Type = type, Name = name, Graphics = graphics, LocalCoordinateSystem = system, Tessellation = tessellation };
        switch (type)
        {
            case PrcType.PolyBrepModel:
                r.Boolean(); // is closed
                break;
            case PrcType.BrepModel:
            case PrcType.Curve:
            case PrcType.Plane:
                if (r.Boolean())
                {
                    r.UnsignedInteger();
                    r.UnsignedInteger();
                }
                if (type == PrcType.BrepModel) r.Boolean();
                break;
            case PrcType.PolyWire:
                break;
            case PrcType.Direction:
                if (r.Boolean()) Vector(r);
                Vector(r);
                break;
            case PrcType.PointSet:
                var points = r.UnsignedInteger();
                for (var k = 0; k < points; k++) Vector(r);
                break;
            case PrcType.Set:
                var n = r.UnsignedInteger();
                for (var k = 0; k < n; k++) item.Children.Add(Item(r));
                break;
            case PrcType.CoordinateSystem:
                TypedTransformation(r);
                break;
            default:
                throw new PrcFormatException($"PRC representation item {type} not known");
        }
        r.UserData();
        return item;
    }

    private (string Name, PrcGraphics Graphics, uint System, uint Tessellation) ItemContent(PrcBitReader r)
    {
        var (name, graphics) = BaseWithGraphics(r);
        return (name, graphics, r.UnsignedInteger(), r.UnsignedInteger());
    }

    /// <summary>Product manufacturing information (dimensions, notes and what they point at): read and dropped.</summary>
    private void Markups(PrcBitReader r)
    {
        var linked = r.UnsignedInteger();
        for (var k = 0; k < linked; k++)
        {
            Expect(r, PrcType.MarkupLinkedItem);
            ContentEntityReference(r);
            if (r.Boolean()) ReferenceData(r);
            for (var b = 0; b < 4; b++) r.Boolean();
            r.UserData();
        }
        var leaders = r.UnsignedInteger();
        for (var k = 0; k < leaders; k++)
        {
            Expect(r, PrcType.Leader);
            BaseWithGraphics(r);
            if (r.Boolean()) ReferenceOnBase(r);
            if (r.Boolean()) ReferenceOnBase(r);
            r.UnsignedInteger(); // tessellation
            r.UserData();
        }
        var markups = r.UnsignedInteger();
        for (var k = 0; k < markups; k++)
        {
            Expect(r, PrcType.Markup);
            BaseWithGraphics(r);
            r.UnsignedInteger(); // type
            r.UnsignedInteger(); // subtype
            for (var list = 0; list < 2; list++)
            {
                var n = r.UnsignedInteger();
                for (var i = 0; i < n; i++) ReferenceOnBase(r);
            }
            r.UnsignedInteger(); // tessellation
            r.UserData();
        }
        var annotations = r.UnsignedInteger();
        for (var k = 0; k < annotations; k++) Annotation(r);
    }

    private void Annotation(PrcBitReader r)
    {
        var type = r.UnsignedInteger();
        BaseWithGraphics(r);
        switch (type)
        {
            case PrcType.AnnotationItem:
                ReferenceOnBase(r);
                break;
            case PrcType.AnnotationSet:
                var n = r.UnsignedInteger();
                for (var i = 0; i < n; i++) Annotation(r);
                break;
            case PrcType.AnnotationReference:
                var m = r.UnsignedInteger();
                for (var i = 0; i < m; i++) ReferenceOnBase(r);
                break;
            default:
                throw new PrcFormatException($"PRC annotation type {type} not known");
        }
        r.UserData();
    }

    private void Views(PrcBitReader r)
    {
        var views = r.UnsignedInteger();
        for (var k = 0; k < views; k++)
        {
            Expect(r, PrcType.View);
            BaseWithGraphics(r);
            var annotations = r.UnsignedInteger();
            for (var i = 0; i < annotations; i++) ReferenceOnBase(r);
            Plane(r);
            if (r.Boolean()) SceneDisplayParameters(r);
            if (fs.AuthoringVersion >= 8137)
            {
                r.Boolean(); // is an annotation view
                r.Boolean(); // is the default view
                r.Boolean(); // is a direction
                var linked = r.UnsignedInteger();
                for (var i = 0; i < linked; i++) ReferenceOnBase(r);
                var filters = r.UnsignedInteger();
                for (var i = 0; i < filters; i++) Filter(r);
            }
            r.UserData();
        }
    }

    private void Plane(PrcBitReader r)
    {
        Expect(r, PrcType.SurfacePlane);
        if (r.Boolean())
        {
            Attributes(r);
            if (!r.Boolean()) _name = r.String();
            r.UnsignedInteger();
        }
        r.UnsignedInteger(); // extend type
        Transformation(r);
        for (var i = 0; i < 8; i++) r.Double(); // domain, parameterisation
    }

    private void ReferenceOnBase(PrcBitReader r)
    {
        Expect(r, PrcType.ReferenceOnPRCBase);
        ReferenceOnBaseContent(r);
    }

    private static void ReferenceOnBaseContent(PrcBitReader r)
    {
        r.UnsignedInteger(); // type of entity
        if (!r.Boolean()) UniqueId(r);
        r.UnsignedInteger(); // unique id
    }

    private void ReferenceData(PrcBitReader r)
    {
        var type = r.UnsignedInteger();
        if (type == PrcType.ReferenceOnPRCBase)
        {
            ReferenceOnBaseContent(r);
        }
        else if (type == PrcType.ReferenceOnTopology)
        {
            r.UnsignedInteger(); // type of entity
            if (r.Boolean())
            {
                if (!r.Boolean()) UniqueId(r);
                r.UnsignedInteger();
                r.UnsignedInteger();
                var n = r.UnsignedInteger();
                for (var i = 0; i < n; i++) r.UnsignedInteger();
            }
        }
        else throw new PrcFormatException($"PRC reference type {type} not known");
    }

    private void ContentEntityReference(PrcBitReader r)
    {
        BaseWithGraphics(r);
        r.UnsignedInteger(); // local coordinate system
        if (r.Boolean()) ReferenceData(r);
    }

    private void Filter(PrcBitReader r)
    {
        Expect(r, PrcType.Filter);
        RefBase(r);
        r.Boolean();
        r.Boolean();
        var layers = r.UnsignedInteger();
        for (var k = 0; k < layers; k++) r.UnsignedInteger();
        r.Boolean();
        var entities = r.UnsignedInteger();
        for (var k = 0; k < entities; k++) EntityReference(r);
        r.UserData();
    }

    private void SceneDisplayParameters(PrcBitReader r)
    {
        Expect(r, PrcType.SceneDisplayParameters);
        RefBase(r);
        r.Boolean(); // is active
        var lights = r.UnsignedInteger();
        for (var i = 0; i < lights; i++)
        {
            var type = r.UnsignedInteger();
            RefBase(r);
            for (var k = 0; k < 3; k++) r.UnsignedInteger(); // ambient, diffuse, specular
            switch (type)
            {
                case PrcType.AmbientLight:
                    break;
                case PrcType.PointLight:
                    Vector(r);
                    for (var k = 0; k < 3; k++) r.Double();
                    break;
                case PrcType.DirectionalLight:
                    Vector(r);
                    if (fs.AuthoringVersion >= 8030) r.Double();
                    break;
                case PrcType.SpotLight:
                    Vector(r);
                    for (var k = 0; k < 3; k++) r.Double();
                    Vector(r);
                    r.Double();
                    r.Double();
                    break;
                default:
                    throw new PrcFormatException($"PRC light type {type} not known");
            }
        }
        if (r.Boolean())
        {
            Expect(r, PrcType.Camera);
            RefBase(r);
            r.Boolean(); // orthographic
            for (var k = 0; k < 3; k++) Vector(r); // position, look, up
            for (var k = 0; k < 6; k++) r.Double(); // x, y, ratio, near, far, zoom
        }
        if (r.Boolean()) Vector(r); // rotation centre
        var planes = r.UnsignedInteger();
        for (var i = 0; i < planes; i++) Plane(r);
        r.UnsignedInteger(); // background style
        r.UnsignedInteger(); // default style
        var styles = r.UnsignedInteger();
        for (var i = 0; i < 2 * styles; i++) r.UnsignedInteger();
        if (fs.AuthoringVersion >= 8137) r.Boolean(); // is absolute
    }

    private void EntityReference(PrcBitReader r)
    {
        Expect(r, PrcType.EntityReference);
        ContentEntityReference(r);
        r.UserData();
    }

    private PrcFileRef FileIdentifier(PrcBitReader r) => r.Boolean() ? new PrcFileRef(null) : new PrcFileRef(UniqueId(r));

    private static uint[] UniqueId(PrcBitReader r) => [r.UnsignedInteger(), r.UnsignedInteger(), r.UnsignedInteger(), r.UnsignedInteger()];

    private static (double X, double Y, double Z) Vector(PrcBitReader r) => (r.Double(), r.Double(), r.Double());

    private static PrcMatrix TypedTransformation(PrcBitReader r)
    {
        var type = r.UnsignedInteger();
        if (type == PrcType.CartesianTransformation) return Transformation(r);
        if (type != PrcType.GeneralTransformation) throw new PrcFormatException($"PRC transformation type {type} not known");
        // Sixteen values, column after column.
        var m = new double[16];
        for (var i = 0; i < 16; i++) m[(i % 4) * 4 + i / 4] = r.Double();
        return new PrcMatrix(m);
    }

    /// <summary>A transformation whose first byte says which of its parts follow.</summary>
    private static PrcMatrix Transformation(PrcBitReader r)
    {
        var behavior = (byte)r.Character();
        var m = PrcMatrix.Identity.M;
        if ((behavior & 0x01) != 0)
        {
            var (x, y, z) = Vector(r);
            m[3] = x;
            m[7] = y;
            m[11] = z;
        }
        if ((behavior & 0x20) != 0)
        {
            for (var c = 0; c < 3; c++)
            {
                var (x, y, z) = Vector(r);
                m[c] = x;
                m[4 + c] = y;
                m[8 + c] = z;
            }
        }
        else if ((behavior & 0x02) != 0)
        {
            var a = Vector(r);
            var b = Vector(r);
            // The third axis: X x Y, or Y x X when mirrored.
            var c = (X: a.Y * b.Z - a.Z * b.Y, Y: a.Z * b.X - a.X * b.Z, Z: a.X * b.Y - a.Y * b.X);
            if ((behavior & 0x04) != 0) c = (-c.X, -c.Y, -c.Z);
            (m[0], m[4], m[8]) = a;
            (m[1], m[5], m[9]) = b;
            (m[2], m[6], m[10]) = c;
        }
        if ((behavior & 0x10) != 0)
        {
            var (x, y, z) = Vector(r);
            for (var row = 0; row < 3; row++)
            {
                m[row * 4] *= x;
                m[row * 4 + 1] *= y;
                m[row * 4 + 2] *= z;
            }
        }
        if ((behavior & 0x08) != 0)
        {
            var s = r.Double();
            for (var row = 0; row < 3; row++)
            for (var c = 0; c < 3; c++)
                m[row * 4 + c] *= s;
        }
        if ((behavior & 0x40) != 0)
        {
            for (var c = 0; c < 4; c++) m[12 + c] = r.Double();
        }
        return new PrcMatrix(m);
    }

    private (string Name, PrcGraphics Graphics) BaseWithGraphics(PrcBitReader r)
    {
        var name = RefBase(r);
        if (!r.Boolean()) _graphics = new PrcGraphics(r.UnsignedInteger(), r.UnsignedInteger(), r.UnsignedShort());
        return (name, _graphics);
    }

    private string RefBase(PrcBitReader r)
    {
        var name = ContentBase(r);
        r.UnsignedInteger(); // CAD identifier
        r.UnsignedInteger(); // CAD persistent identifier
        r.UnsignedInteger(); // PRC unique identifier
        return name;
    }

    private string _name = "";

    /// <summary>Attributes, which are skipped, and a name, which may be "the same as the last one".</summary>
    public string ContentBase(PrcBitReader r)
    {
        Attributes(r);
        if (!r.Boolean()) _name = r.String();
        return _name;
    }

    private static void Attributes(PrcBitReader r)
    {
        var attributes = r.UnsignedInteger();
        for (var i = 0; i < attributes; i++)
        {
            Expect(r, PrcType.Attribute);
            AttributeTitle(r);
            var entries = r.UnsignedInteger();
            for (var k = 0; k < entries; k++)
            {
                AttributeTitle(r);
                switch (r.UnsignedInteger())
                {
                    case 1: r.Integer(); break;
                    case 2: r.Double(); break;
                    case 3: r.Integer(); break;
                    case 4: r.String(); break;
                    case 5:
                        r.Integer();
                        r.UnsignedInteger();
                        break;
                }
            }
        }
    }

    private static void AttributeTitle(PrcBitReader r)
    {
        if (r.Boolean()) r.UnsignedInteger();
        else r.String();
    }
}
