using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Campfire.RailsCompat.Crypto;

/// <summary>
/// The JSON Rails writes and reads: the json gem's <c>JSON.generate</c>, and
/// <c>ActiveSupport::JSON.encode</c> on top of it, which messages and cookies use. Objects keep
/// their key order. JSON <c>null</c> is a <c>null</c> <see cref="JsonNode"/>.
/// </summary>
public static class RailsJson
{
    /// <summary>
    /// <c>JSON.generate</c> / <c>JSON.dump</c> (json 2.21.2): escapes the quote, the backslash and
    /// control characters (lowercase hex), and leaves <c>/</c> and non-ASCII alone. A non-finite
    /// float is written as <c>null</c>, as <c>ActiveSupport::JSON</c> does.
    /// </summary>
    public static string Generate(JsonNode? value)
    {
        var json = new StringBuilder(128);
        Write(json, value);
        return json.ToString();
    }

    /// <summary>
    /// <c>ActiveSupport::JSON.encode</c> with <c>escape_html_entities_in_json</c> (the default):
    /// <see cref="Generate"/>, plus <c>&lt;</c>, <c>&gt;</c> and <c>&amp;</c> escaped as <c>\u003c</c>,
    /// <c>\u003e</c> and <c>\u0026</c>. U+2028 and U+2029 are not escaped, because
    /// <c>load_defaults</c> 8.1+ turns <c>escape_js_separators_in_json</c> off.
    /// </summary>
    public static string Encode(JsonNode? value) => EscapeHtmlEntities(Generate(value));

    /// <summary>
    /// Re-escapes already-encoded JSON. <c>&lt;</c>, <c>&gt;</c> and <c>&amp;</c> only appear inside
    /// JSON strings, so replacing them anywhere is safe, and doing it twice is harmless.
    /// </summary>
    public static string EscapeHtmlEntities(string json)
    {
        if (json.AsSpan().IndexOfAny('<', '>', '&') < 0)
        {
            return json;
        }
        return json.Replace("<", "\\u003c", StringComparison.Ordinal)
            .Replace(">", "\\u003e", StringComparison.Ordinal)
            .Replace("&", "\\u0026", StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>JSON.parse</c>, or <c>false</c> where it raises. A repeated key keeps its first position
    /// and takes the last value, as a Ruby Hash does. Numbers keep their text until written.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> json, out JsonNode? value)
    {
        value = null;
        try
        {
            var bytes = json.ToArray();
            using var document = JsonDocument.Parse(bytes);
            value = ToNode(document.RootElement);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryParse(string json, out JsonNode? value) => TryParse(Encoding.UTF8.GetBytes(json), out value);

    static JsonNode? ToNode(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var property in element.EnumerateObject())
                {
                    obj[property.Name] = ToNode(property.Value);
                }
                return obj;
            case JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in element.EnumerateArray())
                {
                    array.Add(ToNode(item));
                }
                return array;
            case JsonValueKind.String:
                return JsonValue.Create(element.GetString());
            case JsonValueKind.Number:
                return JsonValue.Create(element.Clone());
            case JsonValueKind.True:
                return JsonValue.Create(true);
            case JsonValueKind.False:
                return JsonValue.Create(false);
            default:
                return null;
        }
    }

    static void Write(StringBuilder json, JsonNode? node)
    {
        switch (node)
        {
            case null:
                json.Append("null");
                break;
            case JsonObject obj:
                json.Append('{');
                var first = true;
                foreach (var (key, value) in obj)
                {
                    if (!first)
                    {
                        json.Append(',');
                    }
                    first = false;
                    WriteString(json, key);
                    json.Append(':');
                    Write(json, value);
                }
                json.Append('}');
                break;
            case JsonArray array:
                json.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        json.Append(',');
                    }
                    Write(json, array[i]);
                }
                json.Append(']');
                break;
            case JsonValue value:
                WriteValue(json, value);
                break;
        }
    }

    static void WriteValue(StringBuilder json, JsonValue value)
    {
        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                WriteString(json, value.GetValue<string>());
                break;
            case JsonValueKind.True:
                json.Append("true");
                break;
            case JsonValueKind.False:
                json.Append("false");
                break;
            case JsonValueKind.Number:
                json.Append(Number(value));
                break;
            default:
                json.Append("null");
                break;
        }
    }

    /// <summary>
    /// A number as Ruby writes it back: parsed integers are Integers of any size, anything with a
    /// fraction or exponent is a Float.
    /// </summary>
    static string Number(JsonValue value)
    {
        if (value.TryGetValue<JsonElement>(out var element))
        {
            var text = element.GetRawText();
            return text.AsSpan().IndexOfAny('.', 'e', 'E') < 0
                ? BigInteger.Parse(text, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
                : Float(double.Parse(text, CultureInfo.InvariantCulture));
        }
        if (value.TryGetValue<double>(out var d))
        {
            return Float(d);
        }
        if (value.TryGetValue<float>(out var f))
        {
            return Float(f);
        }
        return value.ToJsonString();
    }

    /// <summary>
    /// A float as the json gem writes it, which is not <c>Float#to_s</c>: json 2.21.2's
    /// <c>fpconv_dtoa</c> (<c>emit_digits</c>) writes <c>1e15</c> as <c>1e+15</c> and <c>1e-5</c>
    /// as <c>0.00001</c>. The digits are the shortest that round-trip.
    /// </summary>
    internal static string Float(double value)
    {
        if (!double.IsFinite(value))
        {
            return "null";
        }
        var sign = double.IsNegative(value) ? "-" : "";
        if (value == 0)
        {
            return sign + "0.0";
        }

        var (digits, exponent) = ShortestDigits(Math.Abs(value));
        // fpconv's K: the power of ten of the last digit.
        var k = exponent + 1 - digits.Length;

        string body;
        if (k >= 0 && exponent < 15)
        {
            body = digits + new string('0', k) + ".0";
        }
        else if (k < 0 && (k > -7 || Math.Abs(exponent) < 10))
        {
            var point = exponent + 1;
            body = point <= 0 ? "0." + new string('0', -point) + digits : digits[..point] + "." + digits[point..];
        }
        else
        {
            var fraction = digits.Length > 1 ? "." + digits[1..] : "";
            body = $"{digits[..1]}{fraction}e{(exponent < 0 ? '-' : '+')}{Math.Abs(exponent)}";
        }
        return sign + body;
    }

    /// <summary>
    /// The shortest round-trip significant digits of a positive finite double, and the power of
    /// ten of the first one (<c>1.5e-7</c> is <c>("15", -7)</c>).
    /// </summary>
    static (string Digits, int Exponent) ShortestDigits(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        var e = text.IndexOf('E', StringComparison.Ordinal);
        var exponent = e < 0 ? 0 : int.Parse(text.AsSpan(e + 1), CultureInfo.InvariantCulture);
        var mantissa = e < 0 ? text : text[..e];
        var dot = mantissa.IndexOf('.', StringComparison.Ordinal);
        var integerPart = dot < 0 ? mantissa : mantissa[..dot];
        var digits = dot < 0 ? mantissa : integerPart + mantissa[(dot + 1)..];

        // Where the decimal point sits within `digits`, then drop the zeros around them.
        var point = integerPart.Length + exponent;
        var trimmed = digits.TrimStart('0');
        point -= digits.Length - trimmed.Length;
        return (trimmed.TrimEnd('0'), point - 1);
    }

    static void WriteString(StringBuilder json, string value)
    {
        json.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    json.Append("\\\"");
                    break;
                case '\\':
                    json.Append("\\\\");
                    break;
                case '\b':
                    json.Append("\\b");
                    break;
                case '\f':
                    json.Append("\\f");
                    break;
                case '\n':
                    json.Append("\\n");
                    break;
                case '\r':
                    json.Append("\\r");
                    break;
                case '\t':
                    json.Append("\\t");
                    break;
                case < ' ':
                    json.Append("\\u00").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    break;
                default:
                    json.Append(c);
                    break;
            }
        }
        json.Append('"');
    }
}
