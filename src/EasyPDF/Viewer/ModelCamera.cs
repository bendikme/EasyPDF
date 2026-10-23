namespace EasyPDF.Viewer;

/// <summary>A point or a direction in a 3D model's world.</summary>
internal readonly record struct Vec3(double X, double Y, double Z)
{
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);

    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public static double Dot(Vec3 a, Vec3 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

    public static Vec3 Cross(Vec3 a, Vec3 b) => new((a.Y * b.Z) - (a.Z * b.Y), (a.Z * b.X) - (a.X * b.Z), (a.X * b.Y) - (a.Y * b.X));

    public double Length => Math.Sqrt(Dot(this, this));

    /// <summary>The direction, at length one; <paramref name="fallback"/> for a zero vector.</summary>
    public Vec3 Unit(Vec3 fallback)
    {
        var length = Length;
        return length > 1e-12 && double.IsFinite(length) ? this * (1 / length) : fallback;
    }

    /// <summary>This turned by <paramref name="angle"/> radians about the unit axis <paramref name="axis"/>.</summary>
    public Vec3 Rotate(Vec3 axis, double angle)
    {
        var (sin, cos) = Math.SinCos(angle);
        return (this * cos) + (Cross(axis, this) * sin) + (axis * (Dot(axis, this) * (1 - cos)));
    }
}

/// <summary>
/// A camera looking at a 3D model: where it is, its axes in the model's world - right, up, and
/// the way it looks, at right angles to each other - and its field of view in degrees, across the
/// width of a frame <paramref name="Aspect"/> times as high as it is wide (a 3D annotation's), the
/// whole frame shown whatever the shape of the picture. It projects in perspective, as a 3D
/// annotation's views do.
/// </summary>
internal readonly record struct ModelCamera(Vec3 Eye, Vec3 Right, Vec3 Up, Vec3 Forward, double FieldOfView, double Aspect = 1)
{
    /// <summary>
    /// The camera of a PDF 3D view: its camera-to-world matrix, twelve numbers - the camera's X, Y
    /// and Z axes in world coordinates and its position - where X is right, Y up and Z the way it
    /// looks. Axes that are not quite at right angles are straightened; null for no usable axes.
    /// The field of view spans the width of a frame <paramref name="aspect"/> times as high as wide.
    /// </summary>
    public static ModelCamera? FromView(double[] c2w, double fieldOfView, double aspect = 1)
    {
        if (c2w.Length != 12 || !c2w.All(double.IsFinite)) return null;
        var forward = new Vec3(c2w[6], c2w[7], c2w[8]).Unit(default);
        var up = new Vec3(c2w[3], c2w[4], c2w[5]);
        if (forward == default) return null;
        up = (up - (forward * Vec3.Dot(up, forward))).Unit(default);
        if (up == default) return null;
        var right = Vec3.Cross(forward, up);
        var fov = fieldOfView is > 0 and < 180 ? fieldOfView : 30;
        return new ModelCamera(new Vec3(c2w[9], c2w[10], c2w[11]), right, up, forward, fov, aspect is > 0 and < 100 ? aspect : 1);
    }

    /// <summary>A camera looking at the middle of a box from the front, above and to the right, with Z up, and the whole box in sight.</summary>
    public static ModelCamera Overview(Vec3 min, Vec3 max)
    {
        var centre = (min + max) * 0.5;
        var radius = Math.Max(1e-6, (max - min).Length / 2);
        var forward = new Vec3(-1, 1, -0.8).Unit(default);
        var right = Vec3.Cross(forward, new Vec3(0, 0, 1)).Unit(new Vec3(1, 0, 0));
        var up = Vec3.Cross(right, forward);
        const double fov = 30;
        var distance = radius / Math.Sin(fov * Math.PI / 360) * 1.05;
        return new ModelCamera(centre - (forward * distance), right, up, forward, fov);
    }

    /// <summary>Pixels per unit of camera space at distance one, in a picture <paramref name="width"/> by <paramref name="height"/>: the frame fitted into it.</summary>
    public double Focal(double width, double height)
    {
        var spread = 2 * Math.Tan(FieldOfView * Math.PI / 360);
        return Math.Min(width / spread, height / (spread * Aspect));
    }

    /// <summary>Where a point lands in a picture of the given size, and how far ahead of the camera it is.</summary>
    public (double X, double Y, double Depth) Project(Vec3 point, double width, double height)
    {
        var d = point - Eye;
        var z = Vec3.Dot(d, Forward);
        var f = Focal(width, height) / z;
        return ((width / 2) + (Vec3.Dot(d, Right) * f), (height / 2) - (Vec3.Dot(d, Up) * f), z);
    }

    /// <summary>The direction from the eye through a point of a picture of the given size, at length one.</summary>
    public Vec3 Ray(double x, double y, double width, double height)
    {
        var f = Focal(width, height);
        var direction = Forward + (Right * ((x - (width / 2)) / f)) - (Up * ((y - (height / 2)) / f));
        return direction.Unit(Forward);
    }

    /// <summary>
    /// The camera turned about <paramref name="pivot"/>: by <paramref name="yaw"/> radians about the
    /// world's up axis, then by <paramref name="pitch"/> about its own right - a turntable, which
    /// keeps the horizon level. A pitch that would look straight up or down is not taken further.
    /// </summary>
    public ModelCamera Orbit(Vec3 pivot, Vec3 worldUp, double yaw, double pitch)
    {
        var turned = Turn(pivot, worldUp, yaw);
        var pitched = turned.Turn(pivot, turned.Right, pitch);
        // Past the pole the horizon would flip: stop just short of it.
        return Math.Abs(Vec3.Dot(pitched.Forward, worldUp)) < 0.998 || Math.Abs(Vec3.Dot(pitched.Forward, worldUp)) < Math.Abs(Vec3.Dot(turned.Forward, worldUp))
            ? pitched
            : turned;
    }

    private ModelCamera Turn(Vec3 pivot, Vec3 axis, double angle) =>
        angle == 0 ? this : new ModelCamera(pivot + (Eye - pivot).Rotate(axis, angle), Right.Rotate(axis, angle), Up.Rotate(axis, angle), Forward.Rotate(axis, angle), FieldOfView);

    /// <summary>
    /// The camera moved sideways so that what was <paramref name="dx"/>, <paramref name="dy"/>
    /// pixels away at <paramref name="depth"/> ahead comes under the same point of the picture.
    /// </summary>
    public ModelCamera Pan(double dx, double dy, double depth, double width, double height)
    {
        var units = depth / Focal(width, height);
        return this with { Eye = Eye - (Right * (dx * units)) + (Up * (dy * units)) };
    }

    /// <summary>The camera moved toward <paramref name="target"/>, to <paramref name="factor"/> of its distance (above one, away).</summary>
    public ModelCamera Dolly(Vec3 target, double factor) => this with { Eye = target + ((Eye - target) * factor) };

    /// <summary>The world axis nearest <paramref name="up"/>: what a turntable turns about.</summary>
    public static Vec3 NearestAxis(Vec3 up)
    {
        double ax = Math.Abs(up.X), ay = Math.Abs(up.Y), az = Math.Abs(up.Z);
        if (az >= ax && az >= ay) return new Vec3(0, 0, Math.Sign(up.Z) is 0 ? 1 : Math.Sign(up.Z));
        return ay >= ax ? new Vec3(0, Math.Sign(up.Y), 0) : new Vec3(Math.Sign(up.X), 0, 0);
    }

    /// <summary>A camera <paramref name="t"/> of the way from <paramref name="a"/> to <paramref name="b"/>: the turn the short way round, the field of view and position in proportion.</summary>
    public static ModelCamera Between(ModelCamera a, ModelCamera b, double t)
    {
        // Right, up and back make a right-handed frame, which a rotation can carry the world's onto.
        var qa = Quaternion.FromAxes(a.Right, a.Up, -a.Forward);
        var qb = Quaternion.FromAxes(b.Right, b.Up, -b.Forward);
        var q = Quaternion.Slerp(qa, qb, t);
        var (right, up, back) = q.Axes();
        return new ModelCamera(a.Eye + ((b.Eye - a.Eye) * t), right, up, -back, a.FieldOfView + ((b.FieldOfView - a.FieldOfView) * t), a.Aspect + ((b.Aspect - a.Aspect) * t));
    }

    /// <summary>A rotation, for turning one camera into another the short way round.</summary>
    private readonly record struct Quaternion(double W, double X, double Y, double Z)
    {
        /// <summary>The rotation that takes the world's X, Y and Z axes to <paramref name="x"/>, <paramref name="y"/> and <paramref name="z"/>.</summary>
        public static Quaternion FromAxes(Vec3 x, Vec3 y, Vec3 z)
        {
            // The rotation matrix has the axes as its columns.
            double m00 = x.X, m10 = x.Y, m20 = x.Z, m01 = y.X, m11 = y.Y, m21 = y.Z, m02 = z.X, m12 = z.Y, m22 = z.Z;
            var trace = m00 + m11 + m22;
            Quaternion q;
            if (trace > 0)
            {
                var s = Math.Sqrt(trace + 1) * 2;
                q = new Quaternion(s / 4, (m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s);
            }
            else if (m00 > m11 && m00 > m22)
            {
                var s = Math.Sqrt(1 + m00 - m11 - m22) * 2;
                q = new Quaternion((m21 - m12) / s, s / 4, (m01 + m10) / s, (m02 + m20) / s);
            }
            else if (m11 > m22)
            {
                var s = Math.Sqrt(1 + m11 - m00 - m22) * 2;
                q = new Quaternion((m02 - m20) / s, (m01 + m10) / s, s / 4, (m12 + m21) / s);
            }
            else
            {
                var s = Math.Sqrt(1 + m22 - m00 - m11) * 2;
                q = new Quaternion((m10 - m01) / s, (m02 + m20) / s, (m12 + m21) / s, s / 4);
            }
            return q.Normalized();
        }

        private Quaternion Normalized()
        {
            var n = Math.Sqrt((W * W) + (X * X) + (Y * Y) + (Z * Z));
            return n > 0 ? new Quaternion(W / n, X / n, Y / n, Z / n) : new Quaternion(1, 0, 0, 0);
        }

        public static Quaternion Slerp(Quaternion a, Quaternion b, double t)
        {
            var dot = (a.W * b.W) + (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);
            if (dot < 0)
            {
                b = new Quaternion(-b.W, -b.X, -b.Y, -b.Z);
                dot = -dot;
            }
            double wa, wb;
            if (dot > 0.9995)
            {
                wa = 1 - t;
                wb = t;
            }
            else
            {
                var angle = Math.Acos(dot);
                var sin = Math.Sin(angle);
                wa = Math.Sin((1 - t) * angle) / sin;
                wb = Math.Sin(t * angle) / sin;
            }
            return new Quaternion((a.W * wa) + (b.W * wb), (a.X * wa) + (b.X * wb), (a.Y * wa) + (b.Y * wb), (a.Z * wa) + (b.Z * wb)).Normalized();
        }

        /// <summary>Where the rotation takes the world's X, Y and Z axes.</summary>
        public (Vec3 X, Vec3 Y, Vec3 Z) Axes()
        {
            double xx = X * X, yy = Y * Y, zz = Z * Z, xy = X * Y, xz = X * Z, yz = Y * Z, wx = W * X, wy = W * Y, wz = W * Z;
            return (new Vec3(1 - (2 * (yy + zz)), 2 * (xy + wz), 2 * (xz - wy)),
                new Vec3(2 * (xy - wz), 1 - (2 * (xx + zz)), 2 * (yz + wx)),
                new Vec3(2 * (xz + wy), 2 * (yz - wx), 1 - (2 * (xx + yy))));
        }
    }
}
