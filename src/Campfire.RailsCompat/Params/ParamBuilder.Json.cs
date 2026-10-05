using System.Numerics;
using System.Text.Json;

namespace Campfire.RailsCompat.Params;

public static partial class ParamBuilder
{
    // JSON.parse's default max_nesting of 100 lets 101 levels through.
    const int jsonMaxDepth = 101;

    /// <summary>
    /// A JSON body: <c>ActiveSupport::JSON.decode</c> (json 2.21: comments allowed, trailing commas
    /// not), wrapped as <c>{ "_json" =&gt; data }</c> unless it's an object, then
    /// <c>ParamBuilder.from_hash</c>, whose deep munge drops nils from arrays.
    /// </summary>
    public static ParamHash FromJson(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = false,
            MaxDepth = jsonMaxDepth,
        });
        object? value;
        try
        {
            if (!reader.Read())
            {
                throw ParamException.Parse();
            }
            value = ReadJsonValue(ref reader);
            if (reader.Read())
            {
                throw ParamException.Parse();
            }
        }
        catch (JsonException)
        {
            throw ParamException.Parse();
        }
        catch (InvalidOperationException)
        {
            throw ParamException.Parse();
        }

        if (value is ParamHash hash)
        {
            return hash;
        }
        return new ParamHash { ["_json"] = value };
    }

    static object? ReadJsonValue(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                var hash = new ParamHash();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var key = reader.GetString()!;
                    reader.Read();
                    // Duplicate keys: the last value wins, in the first one's place.
                    hash[key] = ReadJsonValue(ref reader);
                }
                return hash;
            case JsonTokenType.StartArray:
                var list = new List<object?>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    // NoNilParamEncoder: nils are compacted out of arrays.
                    if (ReadJsonValue(ref reader) is { } element)
                    {
                        list.Add(element);
                    }
                }
                return list;
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.Number:
                return JsonNumber(reader.ValueSpan);
            case JsonTokenType.True:
                return true;
            case JsonTokenType.False:
                return false;
            case JsonTokenType.Null:
                return null;
            default:
                throw ParamException.Parse();
        }
    }

    // Integers stay exact (Ruby's Integer is unbounded); anything with a fraction or exponent is a
    // Float, and one too big for a double is Infinity, as in Ruby.
    static object JsonNumber(ReadOnlySpan<byte> text)
    {
        var s = System.Text.Encoding.ASCII.GetString(text);
        if (text.IndexOfAny((byte)'.', (byte)'e', (byte)'E') >= 0)
        {
            return double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        }
        return long.TryParse(s, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? (object)n
            : BigInteger.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
    }
}
