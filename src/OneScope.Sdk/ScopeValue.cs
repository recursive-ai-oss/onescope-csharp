using System.Globalization;
using System.Numerics;

namespace OneScope.Sdk;

/// <summary>The kind of a <see cref="ScopeValue"/>.</summary>
public enum ValueKind : byte
{
    /// <summary>No value.</summary>
    None = 0,
    /// <summary>A boolean.</summary>
    Bool,
    /// <summary>A 64-bit signed integer.</summary>
    Integer,
    /// <summary>A double-precision number.</summary>
    Number,
    /// <summary>A string.</summary>
    Text,
    /// <summary>A <see cref="System.Numerics.Vector3"/> (2D sources leave Z at zero).</summary>
    Vector,
    /// <summary>An <see cref="EntityId"/>.</summary>
    Entity,
}

/// <summary>
/// A small tagged value used for inspector properties and poke arguments. Create one with the
/// <c>Of*</c> methods; read it with the <c>Get*</c> methods, which throw <see cref="InvalidOperationException"/>
/// when the kind does not match.
/// </summary>
public readonly struct ScopeValue : IEquatable<ScopeValue>
{
    private readonly long _bits;
    private readonly Vector3 _vector;
    private readonly string? _text;

    private ScopeValue(ValueKind kind, long bits, Vector3 vector, string? text)
    {
        Kind = kind;
        _bits = bits;
        _vector = vector;
        _text = text;
    }

    /// <summary>The kind of value held.</summary>
    public ValueKind Kind { get; }

    /// <summary>Makes a boolean value.</summary>
    public static ScopeValue OfBool(bool value) => new(ValueKind.Bool, value ? 1 : 0, default, null);

    /// <summary>Makes an integer value.</summary>
    public static ScopeValue OfInteger(long value) => new(ValueKind.Integer, value, default, null);

    /// <summary>Makes a number value. NaN and infinities are allowed and compare by bit pattern.</summary>
    public static ScopeValue OfNumber(double value) => new(ValueKind.Number, BitConverter.DoubleToInt64Bits(value), default, null);

    /// <summary>Makes a text value.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public static ScopeValue OfText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(ValueKind.Text, 0, default, value);
    }

    /// <summary>Makes a vector value.</summary>
    public static ScopeValue OfVector(Vector3 value) => new(ValueKind.Vector, 0, value, null);

    /// <summary>Makes an entity reference value.</summary>
    public static ScopeValue OfEntity(EntityId value) => new(ValueKind.Entity, unchecked((long)value.Value), default, null);

    /// <summary>Reads a boolean.</summary>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="ValueKind.Bool"/>.</exception>
    public bool GetBool() => Expect(ValueKind.Bool) != 0;

    /// <summary>Reads an integer.</summary>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="ValueKind.Integer"/>.</exception>
    public long GetInteger() => Expect(ValueKind.Integer);

    /// <summary>Reads a number.</summary>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="ValueKind.Number"/>.</exception>
    public double GetNumber() => BitConverter.Int64BitsToDouble(Expect(ValueKind.Number));

    /// <summary>Reads a string.</summary>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="ValueKind.Text"/>.</exception>
    public string GetText()
    {
        Expect(ValueKind.Text);
        return _text!;
    }

    /// <summary>Reads a vector.</summary>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="ValueKind.Vector"/>.</exception>
    public Vector3 GetVector()
    {
        Expect(ValueKind.Vector);
        return _vector;
    }

    /// <summary>Reads an entity id.</summary>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="ValueKind.Entity"/>.</exception>
    public EntityId GetEntity() => new(unchecked((ulong)Expect(ValueKind.Entity)));

    private long Expect(ValueKind kind)
    {
        if (Kind != kind)
        {
            throw new InvalidOperationException($"The value is {Kind}, not {kind}.");
        }

        return _bits;
    }

    /// <inheritdoc />
    public bool Equals(ScopeValue other) =>
        Kind == other.Kind && _bits == other._bits && _vector == other._vector && string.Equals(_text, other._text, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ScopeValue other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Kind, _bits, _vector, _text);

    /// <summary>Compares two values.</summary>
    public static bool operator ==(ScopeValue left, ScopeValue right) => left.Equals(right);

    /// <summary>Compares two values.</summary>
    public static bool operator !=(ScopeValue left, ScopeValue right) => !left.Equals(right);

    /// <summary>Formats the value in the invariant culture, for display and diffs.</summary>
    public override string ToString() => Kind switch
    {
        ValueKind.Bool => _bits != 0 ? "true" : "false",
        ValueKind.Integer => _bits.ToString(CultureInfo.InvariantCulture),
        ValueKind.Number => BitConverter.Int64BitsToDouble(_bits).ToString("R", CultureInfo.InvariantCulture),
        ValueKind.Text => _text!,
        ValueKind.Vector => string.Create(CultureInfo.InvariantCulture, $"({_vector.X}, {_vector.Y}, {_vector.Z})"),
        ValueKind.Entity => unchecked((ulong)_bits).ToString(CultureInfo.InvariantCulture),
        _ => string.Empty,
    };
}
