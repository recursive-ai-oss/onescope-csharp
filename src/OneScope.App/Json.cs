using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using OneGame.Control;

namespace OneScope.App;

/// <summary>Reads an operation's JSON arguments. Every accessor names the argument and the type it expected when the request is wrong.</summary>
internal readonly struct Args
{
    private readonly JsonElement _root;

    private Args(JsonElement root) => _root = root;

    public static Args Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Args(default);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (doc.RootElement.ValueKind is not JsonValueKind.Object)
            {
                throw new ControlException("bad_args", "Arguments must be a JSON object.");
            }

            return new Args(doc.RootElement.Clone());
        }
        catch (JsonException ex)
        {
            throw new ControlException("bad_args", $"Arguments are not valid JSON: {ex.Message}");
        }
    }

    public bool Has(string name) => _root.ValueKind == JsonValueKind.Object && _root.TryGetProperty(name, out JsonElement v) && v.ValueKind != JsonValueKind.Null;

    private JsonElement Get(string name) => _root.GetProperty(name);

    public string? String(string name)
    {
        if (!Has(name))
        {
            return null;
        }

        JsonElement v = Get(name);
        return v.ValueKind == JsonValueKind.String ? v.GetString() : throw Wrong(name, "a string");
    }

    public string RequiredString(string name) => String(name) ?? throw new ControlException("bad_args", $"Argument '{name}' is required (a string).");

    public long? Long(string name)
    {
        if (!Has(name))
        {
            return null;
        }

        JsonElement v = Get(name);
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) ? l : throw Wrong(name, "an integer");
    }

    public long RequiredLong(string name) => Long(name) ?? throw new ControlException("bad_args", $"Argument '{name}' is required (an integer).");

    public int? Int(string name)
    {
        long? l = Long(name);
        return l is null ? null : l is >= int.MinValue and <= int.MaxValue ? (int)l.Value : throw Wrong(name, "a 32-bit integer");
    }

    public double? Double(string name)
    {
        if (!Has(name))
        {
            return null;
        }

        JsonElement v = Get(name);
        return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) && double.IsFinite(d) ? d : throw Wrong(name, "a finite number");
    }

    public bool? Bool(string name)
    {
        if (!Has(name))
        {
            return null;
        }

        JsonElement v = Get(name);
        return v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : throw Wrong(name, "true or false");
    }

    public System.Numerics.Vector3? Vector(string name)
    {
        if (!Has(name))
        {
            return null;
        }

        JsonElement v = Get(name);
        if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() is < 2 or > 3)
        {
            throw Wrong(name, "an array of 2 or 3 numbers");
        }

        float[] c = new float[3];
        int i = 0;
        foreach (JsonElement e in v.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Number || !e.TryGetDouble(out double d) || !double.IsFinite(d))
            {
                throw Wrong(name, "an array of 2 or 3 numbers");
            }

            c[i++] = (float)d;
        }

        return new System.Numerics.Vector3(c[0], c[1], c[2]);
    }

    /// <summary>A name or a number: returns the string form either way.</summary>
    public string? NameOrNumber(string name)
    {
        if (!Has(name))
        {
            return null;
        }

        JsonElement v = Get(name);
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => throw Wrong(name, "a name or a number"),
        };
    }

    private static ControlException Wrong(string name, string expected) => new("bad_args", $"Argument '{name}' must be {expected}.");
}

/// <summary>Builds a JSON result without an intermediate object model.</summary>
internal sealed class JsonOut : IDisposable
{
    private readonly ArrayBufferWriter<byte> _buffer = new(1024);

    public JsonOut()
    {
        Writer = new Utf8JsonWriter(_buffer, new JsonWriterOptions { Indented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    public Utf8JsonWriter Writer { get; }

    public static string Build(Action<Utf8JsonWriter> write)
    {
        using var o = new JsonOut();
        write(o.Writer);
        return o.ToJson();
    }

    public string ToJson()
    {
        Writer.Flush();
        return Encoding.UTF8.GetString(_buffer.WrittenSpan);
    }

    public void Dispose() => Writer.Dispose();

    public static string Hex(ulong hash) => hash.ToString("x16", CultureInfo.InvariantCulture);
}
