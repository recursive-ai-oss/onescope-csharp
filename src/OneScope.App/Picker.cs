using System.Numerics;
using OneScope.Sdk;

namespace OneScope.App;

/// <summary>
/// Finds the entity a draw list shows under a point (2D) or along a ray (3D). Only items that carry an entity count; labels and
/// items drawn as wireframes are hollow, so they are skipped. Both searches are linear in the item count and allocate nothing.
/// </summary>
internal static class Picker
{
    /// <summary>
    /// The entity drawn topmost at <paramref name="point"/> (world units, Z ignored), or none. <paramref name="order"/> is the draw order
    /// of the list's items (lowest layer first); the last item drawn wins. <paramref name="tolerance"/> widens thin shapes (lines, points).
    /// </summary>
    public static EntityId Pick2D(DrawList list, ReadOnlySpan<int> order, int count, Vector2 point, float tolerance)
    {
        ReadOnlySpan<DrawItem> items = list.Items;
        for (int k = Math.Min(count, items.Length) - 1; k >= 0; k--)
        {
            ref readonly DrawItem item = ref items[order[k]];
            if (item.Entity.IsSome && HitsPoint(list, item, point, tolerance))
            {
                return item.Entity;
            }
        }

        return EntityId.None;
    }

    /// <summary>
    /// The entity nearest along the ray, or none. <paramref name="hit"/> is the point where the ray meets it (for thin shapes, the closest point on the shape).
    /// <paramref name="tolerance"/> is the world-space radius that widens lines and points, which depends on distance, so the caller works it out for the view.
    /// </summary>
    public static EntityId Pick3D(DrawList list, Vector3 origin, Vector3 direction, float tolerance, out Vector3 hit)
    {
        EntityId best = EntityId.None;
        float bestT = float.MaxValue;
        hit = origin;
        foreach (ref readonly DrawItem item in list.Items)
        {
            if (!item.Entity.IsSome || !HitsRay(list, item, origin, direction, tolerance, out float t) || t >= bestT)
            {
                continue;
            }

            bestT = t;
            best = item.Entity;
            hit = origin + (direction * t);
        }

        return best;
    }

    private static bool HitsPoint(DrawList list, in DrawItem item, Vector2 p, float tol)
    {
        if ((item.Flags & DrawFlags.Wireframe) != 0 && item.Kind is not (DrawKind.Line or DrawKind.Arrow or DrawKind.Point))
        {
            return false;
        }

        Vector2 a = new(item.A.X, item.A.Y);
        Vector2 b = new(item.B.X, item.B.Y);
        switch (item.Kind)
        {
            case DrawKind.Point:
                return Vector2.Distance(a, p) <= Math.Max(tol, 0);
            case DrawKind.Line:
            case DrawKind.Arrow:
                return SegmentDistance(a, b, p) <= tol;
            case DrawKind.Triangle:
                return InTriangle(p, a, b, new Vector2(item.C.X, item.C.Y), tol);
            case DrawKind.Box:
                {
                    Quaternion q = item.Rotation;
                    float angle = MathF.Atan2(2 * ((q.W * q.Z) + (q.X * q.Y)), 1 - (2 * ((q.Y * q.Y) + (q.Z * q.Z))));
                    float c = MathF.Cos(angle);
                    float s = MathF.Sin(angle);
                    Vector2 d = p - a;
                    float lx = (d.X * c) + (d.Y * s);
                    float ly = (-d.X * s) + (d.Y * c);
                    return MathF.Abs(lx) <= item.Size.X + tol && MathF.Abs(ly) <= item.Size.Y + tol;
                }

            case DrawKind.Sphere:
                return Vector2.Distance(a, p) <= item.Size.X + tol;
            case DrawKind.Capsule:
            case DrawKind.Cylinder:
                return SegmentDistance(a, b, p) <= item.Size.X + tol;
            case DrawKind.Mesh:
                {
                    ReadOnlySpan<Vector3> v = list.Vertices;
                    ReadOnlySpan<int> idx = list.Indices;
                    for (int t = 0; t + 2 < item.Count; t += 3)
                    {
                        Vector3 v0 = v[idx[item.First + t]], v1 = v[idx[item.First + t + 1]], v2 = v[idx[item.First + t + 2]];
                        if (InTriangle(p, new Vector2(v0.X, v0.Y), new Vector2(v1.X, v1.Y), new Vector2(v2.X, v2.Y), 0))
                        {
                            return true;
                        }
                    }

                    return false;
                }

            default:
                return false;
        }
    }

    private static bool HitsRay(DrawList list, in DrawItem item, Vector3 o, Vector3 d, float tol, out float t)
    {
        t = 0;
        if ((item.Flags & DrawFlags.Wireframe) != 0 && item.Kind is not (DrawKind.Line or DrawKind.Arrow or DrawKind.Point))
        {
            return false;
        }

        switch (item.Kind)
        {
            case DrawKind.Point:
                return RaySegment(o, d, item.A, item.A, tol, out t);
            case DrawKind.Line:
            case DrawKind.Arrow:
                return RaySegment(o, d, item.A, item.B, tol, out t);
            case DrawKind.Sphere:
                return RaySphere(o, d, item.A, item.Size.X, out t);
            case DrawKind.Capsule:
            case DrawKind.Cylinder:
                return RaySegment(o, d, item.A, item.B, item.Size.X, out t);
            case DrawKind.Box:
                {
                    Quaternion inv = Quaternion.Conjugate(item.Rotation);
                    Vector3 lo = Vector3.Transform(o - item.A, inv);
                    Vector3 ld = Vector3.Transform(d, inv);
                    return RayBox(lo, ld, item.Size, out t);
                }

            case DrawKind.Triangle:
                return RayTriangle(o, d, item.A, item.B, item.C, out t);
            case DrawKind.Mesh:
                {
                    ReadOnlySpan<Vector3> v = list.Vertices;
                    ReadOnlySpan<int> idx = list.Indices;
                    bool any = false;
                    float best = float.MaxValue;
                    for (int k = 0; k + 2 < item.Count; k += 3)
                    {
                        if (RayTriangle(o, d, v[idx[item.First + k]], v[idx[item.First + k + 1]], v[idx[item.First + k + 2]], out float tt) && tt < best)
                        {
                            best = tt;
                            any = true;
                        }
                    }

                    t = best;
                    return any;
                }

            default:
                return false;
        }
    }

    private static float SegmentDistance(Vector2 a, Vector2 b, Vector2 p)
    {
        Vector2 ab = b - a;
        float len2 = ab.LengthSquared();
        float u = len2 <= 1e-20f ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0, 1);
        return Vector2.Distance(p, a + (ab * u));
    }

    private static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c, float tol)
    {
        float d1 = Edge(p, a, b);
        float d2 = Edge(p, b, c);
        float d3 = Edge(p, c, a);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0;
        bool pos = d1 > 0 || d2 > 0 || d3 > 0;
        if (!(neg && pos))
        {
            return true;
        }

        return tol > 0 && (SegmentDistance(a, b, p) <= tol || SegmentDistance(b, c, p) <= tol || SegmentDistance(c, a, p) <= tol);
    }

    private static float Edge(Vector2 p, Vector2 a, Vector2 b) => ((p.X - b.X) * (a.Y - b.Y)) - ((a.X - b.X) * (p.Y - b.Y));

    private static bool RaySphere(Vector3 o, Vector3 d, Vector3 c, float r, out float t)
    {
        Vector3 m = o - c;
        float b = Vector3.Dot(m, d);
        float cc = Vector3.Dot(m, m) - (r * r);
        float disc = (b * b) - cc;
        t = 0;
        if (disc < 0)
        {
            return false;
        }

        float s = MathF.Sqrt(disc);
        t = -b - s;
        if (t < 0)
        {
            t = -b + s;
        }

        return t >= 0;
    }

    private static bool RayBox(Vector3 o, Vector3 d, Vector3 half, out float t)
    {
        float tmin = 0;
        float tmax = float.MaxValue;
        t = 0;
        for (int axis = 0; axis < 3; axis++)
        {
            float oa = axis == 0 ? o.X : axis == 1 ? o.Y : o.Z;
            float da = axis == 0 ? d.X : axis == 1 ? d.Y : d.Z;
            float ha = axis == 0 ? half.X : axis == 1 ? half.Y : half.Z;
            if (MathF.Abs(da) < 1e-12f)
            {
                if (MathF.Abs(oa) > ha)
                {
                    return false;
                }

                continue;
            }

            float t1 = (-ha - oa) / da;
            float t2 = (ha - oa) / da;
            if (t1 > t2)
            {
                (t1, t2) = (t2, t1);
            }

            tmin = Math.Max(tmin, t1);
            tmax = Math.Min(tmax, t2);
            if (tmin > tmax)
            {
                return false;
            }
        }

        t = tmin;
        return true;
    }

    private static bool RayTriangle(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        t = 0;
        Vector3 e1 = b - a;
        Vector3 e2 = c - a;
        Vector3 p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-12f)
        {
            return false;
        }

        float inv = 1 / det;
        Vector3 s = o - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u is < 0 or > 1)
        {
            return false;
        }

        Vector3 q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(d, q) * inv;
        if (v < 0 || u + v > 1)
        {
            return false;
        }

        t = Vector3.Dot(e2, q) * inv;
        return t >= 0;
    }

    /// <summary>Closest approach of a ray to a segment; a hit when it is within <paramref name="radius"/>. <paramref name="t"/> is the ray parameter there.</summary>
    private static bool RaySegment(Vector3 o, Vector3 d, Vector3 a, Vector3 b, float radius, out float t)
    {
        Vector3 e = b - a;
        Vector3 w = o - a;
        float dd = Vector3.Dot(d, d);
        float de = Vector3.Dot(d, e);
        float ee = Vector3.Dot(e, e);
        float dw = Vector3.Dot(d, w);
        float ew = Vector3.Dot(e, w);
        float denom = (dd * ee) - (de * de);
        float u = ee <= 1e-20f ? 0 : denom > 1e-12f ? Math.Clamp(((dd * ew) - (de * dw)) / denom, 0, 1) : Math.Clamp(ew / ee, 0, 1);
        Vector3 onSeg = a + (e * u);
        t = Math.Max(Vector3.Dot(onSeg - o, d) / dd, 0);
        return Vector3.Distance(o + (d * t), onSeg) <= radius;
    }
}
