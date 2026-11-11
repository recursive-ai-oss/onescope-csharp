using System.Numerics;

namespace OneScope.Sdk;

public sealed partial class DrawList
{
    /// <summary>The most fields one list paints in a frame; further field draws are dropped.</summary>
    public const int MaxFieldBindings = 1024;

    /// <summary>The most field values one list holds.</summary>
    public const int MaxFieldValues = 1 << 23;

    private readonly List<FieldBinding> _bindings = [];
    private float[] _fieldValues = new float[64];
    private int _fieldValueCount;

    /// <summary>The fields painted since the last <see cref="Clear"/>. A <see cref="DrawItem.Field"/> of n refers to element n - 1.</summary>
    public IReadOnlyList<FieldBinding> FieldBindings => _bindings;

    /// <summary>The values of every field painted, addressed by <see cref="FieldBinding.ValueStart"/>.</summary>
    public ReadOnlySpan<float> FieldValues => new(_fieldValues, 0, _fieldValueCount);

    private void ClearFields()
    {
        _bindings.Clear();
        _fieldValueCount = 0;
    }

    /// <summary>
    /// Adds an indexed triangle mesh painted by a scalar field. Indices are relative to <paramref name="vertices"/>, as for <see cref="Mesh"/>.
    /// The style's colour is used only where the field has no value (NaN) and by renderers that ignore fields.
    /// </summary>
    /// <param name="vertices">Mesh vertices.</param>
    /// <param name="indices">Triangle indices; a multiple of three.</param>
    /// <param name="values">One value per vertex for <see cref="FieldDomain.Vertex"/>, or one per triangle for <see cref="FieldDomain.Element"/>. NaN means no data.</param>
    /// <param name="domain">What <paramref name="values"/> is indexed by.</param>
    /// <param name="field">The field being painted; its range and map colour the values.</param>
    /// <param name="style">Style of the mesh.</param>
    /// <remarks>A values array of the wrong length, an invalid mesh or a list at a limit drops the mesh and counts it in <see cref="Dropped"/>.</remarks>
    public void FieldMesh(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices, ReadOnlySpan<float> values, FieldDomain domain, in FieldInfo field, in DrawStyle style)
    {
        int expected = domain == FieldDomain.Vertex ? vertices.Length : indices.Length / 3;
        if (values.Length != expected || _bindings.Count >= MaxFieldBindings || _fieldValueCount + values.Length > MaxFieldValues)
        {
            Dropped++;
            return;
        }

        int itemsBefore = _itemCount;
        Mesh(vertices, indices, style);
        if (_itemCount == itemsBefore)
        {
            return;
        }

        int vertexBase = _vertexCount - vertices.Length;
        _items[_itemCount - 1].Field = BindValues(values, domain, field, vertexBase);
    }

    /// <summary>Adds one point per position, each coloured by its value of a scalar field.</summary>
    /// <param name="positions">Where the points are.</param>
    /// <param name="values">One value per position. NaN means no data.</param>
    /// <param name="field">The field being painted.</param>
    /// <param name="pixels">Point diameter in screen pixels.</param>
    /// <param name="style">Style shared by the points (its colour is replaced by the field's colour).</param>
    /// <remarks>A values array of the wrong length, or a list at a limit, drops every point and counts one in <see cref="Dropped"/>.</remarks>
    public void FieldPoints(ReadOnlySpan<Vector3> positions, ReadOnlySpan<float> values, in FieldInfo field, float pixels, in DrawStyle style)
    {
        if (values.Length != positions.Length || _bindings.Count >= MaxFieldBindings || _fieldValueCount + values.Length > MaxFieldValues)
        {
            Dropped++;
            return;
        }

        int binding = BindValues(values, FieldDomain.Vertex, field, 0);
        int start = _bindings[binding - 1].ValueStart;
        for (int i = 0; i < positions.Length; i++)
        {
            int before = _itemCount;
            Point(positions[i], pixels, style);
            if (_itemCount > before)
            {
                ref DrawItem item = ref _items[_itemCount - 1];
                item.Field = binding;
                item.First = start + i;
                item.Color = ColorMaps.Evaluate(field, values[i]);
            }
        }
    }

    /// <summary>The value of the field at a corner of a field mesh's triangle.</summary>
    /// <param name="item">A mesh item with <see cref="DrawItem.Field"/> set.</param>
    /// <param name="indexPosition">A position in <see cref="Indices"/> inside the item's range (<c>item.First</c> to <c>item.First + item.Count - 1</c>).</param>
    /// <returns>The value, or NaN when the item has no field or the position is outside the item.</returns>
    public float FieldValueAt(in DrawItem item, int indexPosition)
    {
        if (item.Field <= 0 || item.Field > _bindings.Count || item.Kind != DrawKind.Mesh || indexPosition < item.First || indexPosition >= item.First + item.Count)
        {
            return float.NaN;
        }

        FieldBinding b = _bindings[item.Field - 1];
        int local = b.Domain == FieldDomain.Vertex ? _indices[indexPosition] - b.VertexBase : (indexPosition - item.First) / 3;
        return (uint)local < (uint)b.ValueCount ? _fieldValues[b.ValueStart + local] : float.NaN;
    }

    /// <summary>The value of a field point item (see <see cref="FieldPoints"/>), or NaN when the item is not one.</summary>
    public float FieldValueOf(in DrawItem item) =>
        item.Field > 0 && item.Field <= _bindings.Count && item.Kind == DrawKind.Point && (uint)item.First < (uint)_fieldValueCount ? _fieldValues[item.First] : float.NaN;

    /// <summary>The colour of a field mesh corner (see <see cref="FieldValueAt"/>), or the item's own colour when it has no field value.</summary>
    public Color32 FieldColorAt(in DrawItem item, int indexPosition)
    {
        float v = FieldValueAt(in item, indexPosition);
        return float.IsNaN(v) ? item.Color : ColorMaps.Evaluate(_bindings[item.Field - 1].Field, v);
    }

    private int BindValues(ReadOnlySpan<float> values, FieldDomain domain, in FieldInfo field, int vertexBase)
    {
        Grow(ref _fieldValues, _fieldValueCount + values.Length);
        values.CopyTo(_fieldValues.AsSpan(_fieldValueCount));
        _bindings.Add(new FieldBinding(field, domain, _fieldValueCount, values.Length, vertexBase));
        _fieldValueCount += values.Length;
        return _bindings.Count;
    }
}
