using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Campfire.Storage.Variants;

/// <summary>
/// A variation's transformations: the Ruby Hash Active Storage keeps after <c>deep_symbolize_keys</c>,
/// with its insertion order. Keys are symbols. Values are <c>null</c>, <see cref="bool"/>,
/// <see cref="long"/>, <see cref="string"/> (UTF-8), <see cref="RubySymbol"/>, arrays of values, or
/// nested <see cref="Transformations"/>; <see cref="int"/>s are taken as <see cref="long"/>s.
/// </summary>
public sealed class Transformations
{
    readonly List<KeyValuePair<string, object?>> entries;

    public Transformations(IEnumerable<KeyValuePair<string, object?>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        this.entries = [];
        foreach (var (key, value) in entries)
        {
            Set(this.entries, key, Normalize(value));
        }
    }

    public Transformations(params ReadOnlySpan<(string Key, object? Value)> entries)
        : this(entries.ToArray().Select(e => KeyValuePair.Create(e.Key, e.Value)))
    {
    }

    public static Transformations Empty { get; } = new();

    public IReadOnlyList<KeyValuePair<string, object?>> Entries => entries;

    public int Count => entries.Count;

    public bool IsEmpty => entries.Count == 0;

    public bool TryGetValue(string key, out object? value)
    {
        foreach (var entry in entries)
        {
            if (entry.Key == key)
            {
                value = entry.Value;
                return true;
            }
        }
        value = null;
        return false;
    }

    /// <summary>
    /// <c>reverse_merge(defaults)</c>, i.e. <c>defaults.merge(self)</c>: the defaults' keys come first and
    /// keep their place when this hash overrides them.
    /// </summary>
    public Transformations ReverseMerge(Transformations defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        var merged = new List<KeyValuePair<string, object?>>(defaults.entries);
        foreach (var (key, value) in entries)
        {
            Set(merged, key, value);
        }
        return new Transformations(merged);
    }

    /// <summary><c>except(*keys)</c>.</summary>
    public Transformations Except(string key) => new(entries.Where(e => e.Key != key));

    /// <summary>
    /// The hash as <c>ActiveStorage.verifier</c> serializes it (<c>as_json</c>): symbols become strings.
    /// </summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject();
        foreach (var (key, value) in entries)
        {
            json[key] = ValueToJson(value);
        }
        return json;
    }

    /// <summary>
    /// <c>Variation.new(json).transformations</c> for a verified variation key: keys symbolized (deeply),
    /// values left as JSON gives them, so formats come back as strings. Null for anything the
    /// transformations can't hold (a non-object, a float, an out-of-range integer).
    /// </summary>
    public static Transformations? FromJson(JsonNode? json) =>
        json is JsonObject && TryFromJson(json, out var value) ? (Transformations)value! : null;

    /// <summary>Ruby 3.4's <c>Hash#inspect</c>, e.g. <c>{format: :webp, resize_to_limit: [512, 512]}</c>.</summary>
    public string Inspect()
    {
        var builder = new StringBuilder();
        Inspect(builder, this);
        return builder.ToString();
    }

    public override string ToString() => Inspect();

    static void Set(List<KeyValuePair<string, object?>> entries, string key, object? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        var index = entries.FindIndex(e => e.Key == key);
        if (index >= 0)
        {
            entries[index] = KeyValuePair.Create(key, value);
        }
        else
        {
            entries.Add(KeyValuePair.Create(key, value));
        }
    }

    static object? Normalize(object? value) => value switch
    {
        null or bool or long or string or RubySymbol or Transformations => value,
        int i => (long)i,
        IEnumerable<KeyValuePair<string, object?>> hash => new Transformations(hash),
        System.Collections.IEnumerable items => items.Cast<object?>().Select(Normalize).ToArray(),
        _ => throw new ArgumentException($"A transformation can't hold a {value.GetType().Name}", nameof(value)),
    };

    static JsonNode? ValueToJson(object? value) => value switch
    {
        null => null,
        bool b => JsonValue.Create(b),
        long n => JsonValue.Create(n),
        string s => JsonValue.Create(s),
        RubySymbol symbol => JsonValue.Create(symbol.Name),
        object?[] items => new JsonArray(items.Select(ValueToJson).ToArray()),
        Transformations hash => hash.ToJson(),
        _ => throw new InvalidOperationException(),
    };

    static bool TryFromJson(JsonNode? json, out object? value)
    {
        value = null;
        switch (json)
        {
            case null:
                return true;
            case JsonObject obj:
                var entries = new List<KeyValuePair<string, object?>>();
                foreach (var (key, item) in obj)
                {
                    if (!TryFromJson(item, out var converted))
                    {
                        return false;
                    }
                    entries.Add(KeyValuePair.Create(key, converted));
                }
                value = new Transformations(entries);
                return true;
            case JsonArray array:
                var items = new object?[array.Count];
                for (var i = 0; i < items.Length; i++)
                {
                    if (!TryFromJson(array[i], out items[i]))
                    {
                        return false;
                    }
                }
                value = items;
                return true;
            case JsonValue scalar:
                switch (scalar.GetValueKind())
                {
                    case JsonValueKind.String:
                        value = scalar.GetValue<string>();
                        return true;
                    case JsonValueKind.True or JsonValueKind.False:
                        value = scalar.GetValue<bool>();
                        return true;
                    case JsonValueKind.Number when scalar.TryGetValue<long>(out var n) && IsInteger(scalar):
                        value = n;
                        return true;
                    default:
                        return false;
                }
            default:
                return false;
        }
    }

    // JSON.parse makes "80" an Integer but "80.0" or "8e1" a Float.
    static bool IsInteger(JsonValue number) => !number.ToJsonString().AsSpan().ContainsAny(".eE");

    static void Inspect(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                builder.Append("nil");
                break;
            case bool b:
                builder.Append(b ? "true" : "false");
                break;
            case long n:
                builder.Append(n.ToString(CultureInfo.InvariantCulture));
                break;
            case string s:
                InspectString(builder, s);
                break;
            case RubySymbol symbol:
                builder.Append(':').Append(symbol.Name);
                break;
            case object?[] items:
                builder.Append('[');
                for (var i = 0; i < items.Length; i++)
                {
                    builder.Append(i == 0 ? "" : ", ");
                    Inspect(builder, items[i]);
                }
                builder.Append(']');
                break;
            case Transformations hash:
                builder.Append('{');
                for (var i = 0; i < hash.entries.Count; i++)
                {
                    builder.Append(i == 0 ? "" : ", ").Append(hash.entries[i].Key).Append(": ");
                    Inspect(builder, hash.entries[i].Value);
                }
                builder.Append('}');
                break;
        }
    }

    // String#inspect for the printable UTF-8 strings transformations hold.
    static void InspectString(StringBuilder builder, string s)
    {
        builder.Append('"');
        foreach (var c in s)
        {
            builder.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\t' => "\\t",
                < ' ' => $"\\x{(int)c:X2}",
                _ => c.ToString(),
            });
        }
        builder.Append('"');
    }
}
