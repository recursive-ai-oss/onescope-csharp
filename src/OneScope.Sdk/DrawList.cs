using System.Numerics;

namespace OneScope.Sdk;

/// <summary>The primitive a <see cref="DrawItem"/> describes.</summary>
public enum DrawKind : byte
{
    /// <summary>A line segment from A to B.</summary>
    Line,
    /// <summary>A point at A; <see cref="DrawItem.Size"/>.X is its diameter in screen pixels.</summary>
    Point,
    /// <summary>A triangle A, B, C.</summary>
    Triangle,
    /// <summary>An indexed triangle mesh: <see cref="DrawItem.Count"/> indices starting at <see cref="DrawItem.First"/> in <see cref="DrawList.Indices"/>, into <see cref="DrawList.Vertices"/>.</summary>
    Mesh,
    /// <summary>A box centred on A, rotated by <see cref="DrawItem.Rotation"/>, with half extents <see cref="DrawItem.Size"/>.</summary>
    Box,
    /// <summary>A sphere centred on A with radius <see cref="DrawItem.Size"/>.X.</summary>
    Sphere,
    /// <summary>A capsule whose axis runs A to B, radius <see cref="DrawItem.Size"/>.X.</summary>
    Capsule,
    /// <summary>A cylinder whose axis runs A to B, radius <see cref="DrawItem.Size"/>.X.</summary>
    Cylinder,
    /// <summary>An arrow from A to B; the head size is <see cref="DrawItem.Size"/>.X in world units (zero lets the renderer choose).</summary>
    Arrow,
    /// <summary>Text anchored at A; <see cref="DrawItem.Count"/> chars starting at <see cref="DrawItem.First"/> in <see cref="DrawList.TextChars"/>.</summary>
    Label,
}

/// <summary>Modifiers for a draw item.</summary>
[Flags]
public enum DrawFlags : byte
{
    /// <summary>Solid, depth tested.</summary>
    None = 0,
    /// <summary>Draw edges only.</summary>
    Wireframe = 1,
    /// <summary>Draw on top of the scene, ignoring depth.</summary>
    OnTop = 2,
}

/// <summary>Colour, grouping and modifiers shared by every primitive.</summary>
/// <param name="Color">The colour.</param>
/// <param name="Overlay">The overlay this item belongs to, 0 to 63 (see <see cref="OverlayInfo"/>). The app hides it when that overlay is off.</param>
/// <param name="Layer">Draw order within a view; higher layers draw later. Not a depth value.</param>
/// <param name="Flags">Drawing modifiers.</param>
/// <param name="Entity">The entity this item shows, or <see cref="EntityId.None"/>. The app picks it under the pointer and highlights it when selected.</param>
public readonly record struct DrawStyle(Color32 Color, int Overlay = 0, int Layer = 0, DrawFlags Flags = DrawFlags.None, EntityId Entity = default)
{
    /// <summary>
    /// A style for an item that belongs to no entity. It stays in 1.0 because the navigation module is built on a release that
    /// only had this four-argument form; a module that draws entities passes <paramref name="color"/> and the entity in the
    /// full constructor.
    /// </summary>
    public DrawStyle(Color32 color, int overlay, int layer, DrawFlags flags)
        : this(color, overlay, layer, flags, default)
    {
    }
}

/// <summary>
/// One primitive in a <see cref="DrawList"/>. Which fields are used depends on <see cref="Kind"/>; unused fields are zero.
/// All positions and sizes are in the source's <see cref="LengthUnit"/>. 2D sources keep Z at zero.
/// </summary>
public struct DrawItem
{
    /// <summary>The primitive.</summary>
    public DrawKind Kind;

    /// <summary>Drawing modifiers.</summary>
    public DrawFlags Flags;

    /// <summary>Overlay id, 0 to 63.</summary>
    public byte Overlay;

    /// <summary>Draw order within a view.</summary>
    public int Layer;

    /// <summary>The colour.</summary>
    public Color32 Color;

    /// <summary>The entity this item shows, or <see cref="EntityId.None"/>.</summary>
    public EntityId Entity;

    /// <summary>First position (start, centre or anchor).</summary>
    public Vector3 A;

    /// <summary>Second position (end of a line, arrow or capsule axis).</summary>
    public Vector3 B;

    /// <summary>
    /// Zero, or 1 plus the index in <see cref="DrawList.FieldBindings"/> of the scalar field that colours this item (a mesh from
    /// <see cref="DrawList.FieldMesh"/> or a point from <see cref="DrawList.FieldPoints"/>). Renderers that ignore fields use <see cref="Color"/>.
    /// </summary>
    public int Field;

    /// <summary>Third position (triangles).</summary>
    public Vector3 C;

    /// <summary>Orientation for boxes. Identity for everything else.</summary>
    public Quaternion Rotation;

    /// <summary>Extent: box half extents, or radius / point size in X.</summary>
    public Vector3 Size;

    /// <summary>Start in <see cref="DrawList.Indices"/> (meshes) or <see cref="DrawList.TextChars"/> (labels).</summary>
    public int First;

    /// <summary>Index count (meshes) or char count (labels).</summary>
    public int Count;
}

/// <summary>
/// A reusable, neutral list of primitives a module fills and the app renders. Clear it and fill it again each
/// frame; once its buffers have grown to the scene's size, filling it allocates nothing.
/// A list holds at most <see cref="MaxItems"/> items, <see cref="MaxVertices"/> mesh vertices and
/// <see cref="MaxChars"/> label characters; anything past a limit is dropped and counted in <see cref="Dropped"/>.
/// </summary>
public sealed partial class DrawList
{
    /// <summary>The most items one list holds.</summary>
    public const int MaxItems = 1 << 21;

    /// <summary>The most mesh vertices one list holds.</summary>
    public const int MaxVertices = 1 << 22;

    /// <summary>The most label characters one list holds.</summary>
    public const int MaxChars = 1 << 20;

    private DrawItem[] _items = new DrawItem[256];
    private Vector3[] _vertices = new Vector3[64];
    private int[] _indices = new int[192];
    private char[] _chars = new char[256];
    private int _itemCount;
    private int _vertexCount;
    private int _indexCount;
    private int _charCount;
    private DrawItem _discard;

    /// <summary>The items, in the order they were added.</summary>
    public ReadOnlySpan<DrawItem> Items => new(_items, 0, _itemCount);

    /// <summary>The shared mesh vertices.</summary>
    public ReadOnlySpan<Vector3> Vertices => new(_vertices, 0, _vertexCount);

    /// <summary>The shared mesh indices. Each mesh's indices already point into <see cref="Vertices"/>.</summary>
    public ReadOnlySpan<int> Indices => new(_indices, 0, _indexCount);

    /// <summary>The shared label characters.</summary>
    public ReadOnlySpan<char> TextChars => new(_chars, 0, _charCount);

    /// <summary>The number of items dropped since the last <see cref="Clear"/> because a limit was reached or an input was invalid.</summary>
    public int Dropped { get; private set; }

    /// <summary>The text of a label item.</summary>
    public ReadOnlySpan<char> TextOf(in DrawItem item) =>
        item.Kind == DrawKind.Label ? new ReadOnlySpan<char>(_chars, item.First, item.Count) : default;

    /// <summary>Removes everything but keeps the buffers.</summary>
    public void Clear()
    {
        _itemCount = 0;
        _vertexCount = 0;
        _indexCount = 0;
        _charCount = 0;
        Dropped = 0;
        ClearFields();
    }

    /// <summary>Adds a line segment.</summary>
    public void Line(Vector3 a, Vector3 b, in DrawStyle style)
    {
        ref DrawItem item = ref Add(DrawKind.Line, style);
        item.A = a;
        item.B = b;
    }

    /// <summary>Adds a point.</summary>
    /// <param name="at">Position.</param>
    /// <param name="pixels">Diameter in screen pixels.</param>
    /// <param name="style">Style.</param>
    public void Point(Vector3 at, float pixels, in DrawStyle style)
    {
        ref DrawItem item = ref Add(DrawKind.Point, style);
        item.A = at;
        item.Size = new Vector3(pixels, 0, 0);
    }

    /// <summary>Adds a triangle.</summary>
    public void Triangle(Vector3 a, Vector3 b, Vector3 c, in DrawStyle style)
    {
        ref DrawItem item = ref Add(DrawKind.Triangle, style);
        item.A = a;
        item.B = b;
        item.C = c;
    }

    /// <summary>
    /// Adds an indexed triangle mesh. Indices are relative to <paramref name="vertices"/>; the list rebases them.
    /// An index outside the vertex range, or a count that is not a multiple of three, drops the mesh and counts it in <see cref="Dropped"/>.
    /// </summary>
    public void Mesh(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices, in DrawStyle style)
    {
        if (indices.Length % 3 != 0 || _vertexCount + vertices.Length > MaxVertices || _indexCount + indices.Length > MaxVertices * 3L)
        {
            Dropped++;
            return;
        }

        foreach (int index in indices)
        {
            if ((uint)index >= (uint)vertices.Length)
            {
                Dropped++;
                return;
            }
        }

        if (_itemCount >= MaxItems)
        {
            Dropped++;
            return;
        }

        int vertexBase = _vertexCount;
        Grow(ref _vertices, _vertexCount + vertices.Length);
        vertices.CopyTo(_vertices.AsSpan(_vertexCount));
        _vertexCount += vertices.Length;

        Grow(ref _indices, _indexCount + indices.Length);
        for (int i = 0; i < indices.Length; i++)
        {
            _indices[_indexCount + i] = indices[i] + vertexBase;
        }

        ref DrawItem item = ref Add(DrawKind.Mesh, style);
        item.First = _indexCount;
        item.Count = indices.Length;
        _indexCount += indices.Length;
    }

    /// <summary>Adds a box.</summary>
    /// <param name="center">Centre.</param>
    /// <param name="halfExtents">Half size along each local axis.</param>
    /// <param name="rotation">Orientation; use <see cref="Quaternion.Identity"/> for axis aligned.</param>
    /// <param name="style">Style.</param>
    public void Box(Vector3 center, Vector3 halfExtents, Quaternion rotation, in DrawStyle style)
    {
        ref DrawItem item = ref Add(DrawKind.Box, style);
        item.A = center;
        item.Size = halfExtents;
        item.Rotation = rotation;
    }

    /// <summary>Adds a sphere.</summary>
    public void Sphere(Vector3 center, float radius, in DrawStyle style)
    {
        ref DrawItem item = ref Add(DrawKind.Sphere, style);
        item.A = center;
        item.Size = new Vector3(radius, 0, 0);
    }

    /// <summary>Adds a capsule whose segment runs from <paramref name="a"/> to <paramref name="b"/>.</summary>
    public void Capsule(Vector3 a, Vector3 b, float radius, in DrawStyle style)
    {
        ref DrawItem item = ref Add(DrawKind.Capsule, style);
        item.A = a;
        item.B = b;
        item.Size = new Vector3(radius, 0, 0);
    }

    /// <summary>Adds a cylinder whose axis runs from <paramref name="a"/> to <paramref name="b"/>.</summary>
    public void Cylinder(Vector3 a, Vector3 b, float radius, in DrawStyle style)
    {
        ref DrawItem item = ref Add(DrawKind.Cylinder, style);
        item.A = a;
        item.B = b;
        item.Size = new Vector3(radius, 0, 0);
    }

    /// <summary>Adds an arrow.</summary>
    /// <param name="from">Tail.</param>
    /// <param name="to">Head.</param>
    /// <param name="headSize">Head size in world units; zero lets the renderer choose.</param>
    /// <param name="style">Style.</param>
    public void Arrow(Vector3 from, Vector3 to, float headSize, in DrawStyle style)
    {
        ref DrawItem item = ref Add(DrawKind.Arrow, style);
        item.A = from;
        item.B = to;
        item.Size = new Vector3(headSize, 0, 0);
    }

    /// <summary>Adds a text label. The text is copied, so passing a reused buffer is fine.</summary>
    public void Label(Vector3 at, ReadOnlySpan<char> text, in DrawStyle style)
    {
        if (_charCount + text.Length > MaxChars || _itemCount >= MaxItems)
        {
            Dropped++;
            return;
        }

        Grow(ref _chars, _charCount + text.Length);
        text.CopyTo(_chars.AsSpan(_charCount));
        ref DrawItem item = ref Add(DrawKind.Label, style);
        item.A = at;
        item.First = _charCount;
        item.Count = text.Length;
        _charCount += text.Length;
    }

    private ref DrawItem Add(DrawKind kind, in DrawStyle style)
    {
        if (_itemCount >= MaxItems)
        {
            Dropped++;
            _discard = default;
            return ref _discard;
        }

        Grow(ref _items, _itemCount + 1);
        ref DrawItem item = ref _items[_itemCount++];
        item = default;
        item.Kind = kind;
        item.Flags = style.Flags;
        item.Overlay = (byte)(style.Overlay & 63);
        item.Layer = style.Layer;
        item.Color = style.Color;
        item.Entity = style.Entity;
        item.Rotation = Quaternion.Identity;
        return ref item;
    }

    private static void Grow<T>(ref T[] array, int needed)
    {
        if (needed <= array.Length)
        {
            return;
        }

        int size = Math.Max(needed, array.Length * 2);
        Array.Resize(ref array, size);
    }
}
