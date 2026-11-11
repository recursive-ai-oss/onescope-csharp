using System.Numerics;
using OneGame.Render3D;
using OneScope.Sdk;

namespace OneScope.App;

/// <summary>
/// Turns a draw list into 3D geometry: solid primitives become one triangle mesh with the light baked into vertex colours
/// (so the look doesn't depend on the renderer's lighting setup), and everything drawn as lines goes to a line list.
/// The builder keeps its buffers between frames so a steady scene allocates nothing.
/// </summary>
internal sealed class SolidBuilder
{
    private const int Segments = 20;
    private const int Rings = 10;
    private static readonly Vector3 LightDirection = Vector3.Normalize(new Vector3(0.45f, 1f, 0.7f));
    private static readonly float[] SrgbToLinear = BuildTable();

    private MeshVertex3D[] _vertices = new MeshVertex3D[1024];
    private uint[] _indices = new uint[4096];
    private int _vertexCount;
    private int _indexCount;

    public ReadOnlySpan<MeshVertex3D> Vertices => new(_vertices, 0, _vertexCount);

    public ReadOnlySpan<uint> Indices => new(_indices, 0, _indexCount);

    public void Clear()
    {
        _vertexCount = 0;
        _indexCount = 0;
    }

    public static Vector4 Linear(Color32 c) => new(SrgbToLinear[c.R], SrgbToLinear[c.G], SrgbToLinear[c.B], c.A / 255f);

    private static float[] BuildTable()
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++)
        {
            float s = i / 255f;
            t[i] = s <= 0.04045f ? s / 12.92f : MathF.Pow((s + 0.055f) / 1.055f, 2.4f);
        }

        return t;
    }

    private int AddVertex(Vector3 p, Vector3 n, Vector4 colour)
    {
        if (_vertexCount == _vertices.Length)
        {
            Array.Resize(ref _vertices, _vertices.Length * 2);
        }

        float lit = 0.4f + 0.6f * MathF.Max(0f, Vector3.Dot(n, LightDirection));
        _vertices[_vertexCount] = new MeshVertex3D(p, n, Vector2.Zero, new Vector4(colour.X * lit, colour.Y * lit, colour.Z * lit, colour.W));
        return _vertexCount++;
    }

    private void AddTriangleIndices(int a, int b, int c)
    {
        if (_indexCount + 3 > _indices.Length)
        {
            Array.Resize(ref _indices, _indices.Length * 2);
        }

        _indices[_indexCount++] = (uint)a;
        _indices[_indexCount++] = (uint)b;
        _indices[_indexCount++] = (uint)c;
    }

    public void Triangle(Vector3 a, Vector3 b, Vector3 c, Vector4 colour)
    {
        Vector3 n = Vector3.Cross(b - a, c - a);
        n = n.LengthSquared() > 1e-20f ? Vector3.Normalize(n) : Vector3.UnitY;
        int i = AddVertex(a, n, colour);
        AddVertex(b, n, colour);
        AddVertex(c, n, colour);
        AddTriangleIndices(i, i + 1, i + 2);
        // Drawn from both sides: the material is double sided, so no second winding is needed.
    }

    public void Mesh(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices, int first, int count, Vector4 colour)
    {
        for (int t = 0; t + 2 < count; t += 3)
        {
            Triangle(vertices[indices[first + t]], vertices[indices[first + t + 1]], vertices[indices[first + t + 2]], colour);
        }
    }

    /// <summary>A mesh painted by a scalar field: each corner takes the colour of its value, so a per-vertex field blends across a triangle.</summary>
    public void FieldMesh(DrawList list, in DrawItem item)
    {
        ReadOnlySpan<Vector3> v = list.Vertices;
        ReadOnlySpan<int> idx = list.Indices;
        for (int t = 0; t + 2 < item.Count; t += 3)
        {
            Vector3 a = v[idx[item.First + t]], b = v[idx[item.First + t + 1]], c = v[idx[item.First + t + 2]];
            Vector3 n = Vector3.Cross(b - a, c - a);
            n = n.LengthSquared() > 1e-20f ? Vector3.Normalize(n) : Vector3.UnitY;
            int i = AddVertex(a, n, Linear(list.FieldColorAt(item, item.First + t)));
            AddVertex(b, n, Linear(list.FieldColorAt(item, item.First + t + 1)));
            AddVertex(c, n, Linear(list.FieldColorAt(item, item.First + t + 2)));
            AddTriangleIndices(i, i + 1, i + 2);
        }
    }

    public void Box(Vector3 centre, Vector3 half, Quaternion rotation, Vector4 colour)
    {
        ReadOnlySpan<Vector3> normals = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
        foreach (Vector3 n in normals)
        {
            Vector3 u = MathF.Abs(n.Y) > 0.5f ? Vector3.UnitX : Vector3.UnitY;
            Vector3 t1 = Vector3.Normalize(Vector3.Cross(n, u));
            Vector3 t2 = Vector3.Cross(n, t1);
            Vector3 c = n * Abs(n, half);
            Vector3 e1 = t1 * Abs(t1, half);
            Vector3 e2 = t2 * Abs(t2, half);
            Vector3 wn = Vector3.Transform(n, rotation);
            int i = AddVertex(centre + Vector3.Transform(c - e1 - e2, rotation), wn, colour);
            AddVertex(centre + Vector3.Transform(c + e1 - e2, rotation), wn, colour);
            AddVertex(centre + Vector3.Transform(c + e1 + e2, rotation), wn, colour);
            AddVertex(centre + Vector3.Transform(c - e1 + e2, rotation), wn, colour);
            AddTriangleIndices(i, i + 1, i + 2);
            AddTriangleIndices(i, i + 2, i + 3);
        }
    }

    private static float Abs(Vector3 axis, Vector3 half) => MathF.Abs(axis.X) * half.X + MathF.Abs(axis.Y) * half.Y + MathF.Abs(axis.Z) * half.Z;

    public void Sphere(Vector3 centre, float radius, Vector4 colour)
    {
        int first = _vertexCount;
        for (int r = 0; r <= Rings; r++)
        {
            float phi = MathF.PI * r / Rings;
            float y = MathF.Cos(phi);
            float ring = MathF.Sin(phi);
            for (int s = 0; s <= Segments; s++)
            {
                float theta = 2 * MathF.PI * s / Segments;
                var n = new Vector3(ring * MathF.Cos(theta), y, ring * MathF.Sin(theta));
                AddVertex(centre + n * radius, n, colour);
            }
        }

        int stride = Segments + 1;
        for (int r = 0; r < Rings; r++)
        {
            for (int s = 0; s < Segments; s++)
            {
                int a = first + r * stride + s;
                AddTriangleIndices(a, a + stride, a + 1);
                AddTriangleIndices(a + 1, a + stride, a + stride + 1);
            }
        }
    }

    public void Cylinder(Vector3 a, Vector3 b, float radius, Vector4 colour, bool caps = true)
    {
        Axis(a, b, out Vector3 axis, out Vector3 u, out Vector3 v);
        int first = _vertexCount;
        for (int s = 0; s <= Segments; s++)
        {
            float theta = 2 * MathF.PI * s / Segments;
            Vector3 n = u * MathF.Cos(theta) + v * MathF.Sin(theta);
            AddVertex(a + n * radius, n, colour);
            AddVertex(b + n * radius, n, colour);
        }

        for (int s = 0; s < Segments; s++)
        {
            int i = first + s * 2;
            AddTriangleIndices(i, i + 1, i + 2);
            AddTriangleIndices(i + 2, i + 1, i + 3);
        }

        if (!caps)
        {
            return;
        }

        foreach ((Vector3 centre, Vector3 n) in new[] { (a, -axis), (b, axis) })
        {
            int c = AddVertex(centre, n, colour);
            int ringStart = _vertexCount;
            for (int s = 0; s <= Segments; s++)
            {
                float theta = 2 * MathF.PI * s / Segments;
                AddVertex(centre + (u * MathF.Cos(theta) + v * MathF.Sin(theta)) * radius, n, colour);
            }

            for (int s = 0; s < Segments; s++)
            {
                AddTriangleIndices(c, ringStart + s, ringStart + s + 1);
            }
        }
    }

    public void Capsule(Vector3 a, Vector3 b, float radius, Vector4 colour)
    {
        Cylinder(a, b, radius, colour, caps: false);
        Sphere(a, radius, colour);
        Sphere(b, radius, colour);
    }

    /// <summary>An orthonormal frame with <paramref name="axis"/> pointing from a to b (or up when they coincide).</summary>
    public static void Axis(Vector3 a, Vector3 b, out Vector3 axis, out Vector3 u, out Vector3 v)
    {
        Vector3 d = b - a;
        axis = d.LengthSquared() > 1e-20f ? Vector3.Normalize(d) : Vector3.UnitY;
        Vector3 helper = MathF.Abs(axis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        u = Vector3.Normalize(Vector3.Cross(axis, helper));
        v = Vector3.Cross(axis, u);
    }
}
