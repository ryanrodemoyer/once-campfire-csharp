using System.Text;

namespace Campfire.RailsCompat.Params;

/// <summary>
/// <c>ActionDispatch::ParamBuilder</c> (<c>action_dispatch/http/param_builder.rb</c>): nests
/// <c>a[b][]=1</c>-style pairs into hashes and arrays, raising what Rails raises.
/// </summary>
public static partial class ParamBuilder
{
    /// <summary><c>ActionDispatch::ParamBuilder.default</c>'s depth limit.</summary>
    public const int DepthLimit = 100;

    static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    /// <summary><c>ParamBuilder.from_query_string</c>: the query string as Rails' <c>request.GET</c> sees it.</summary>
    public static ParamHash FromQueryString(ReadOnlyMemory<byte> query) => FromPairs(QueryParser.EachPair(query));

    public static ParamHash FromQueryString(string query) => FromPairs(QueryParser.EachPair(query));

    /// <summary><c>ParamBuilder.from_pairs</c>.</summary>
    public static ParamHash FromPairs(IEnumerable<ParamPair> pairs)
    {
        var parameters = new ParamHash();
        foreach (var pair in pairs)
        {
            // Names are walked as bytes (one char per byte), so the brackets are found the same way
            // whether or not the name is valid in its encoding; each key is checked as it's taken.
            var name = Encoding.Latin1.GetString(pair.Key);
            var value = pair.Value is byte[] bytes ? new RawValue(bytes, pair.Encoding) : pair.Value;
            StoreNestedParam(parameters, name, value, 0, pair.Encoding);
        }
        return parameters;
    }

    // A string value whose encoding is checked once its top-level key is known, as Rails does.
    sealed record RawValue(byte[] Bytes, Encoding Encoding);

    // store_nested_param: returns the hash it was given, a one-element array (for a trailing []
    // below the top level), or null (for an empty key).
    static object? StoreNestedParam(ParamHash parameters, string name, object? v, int depth, Encoding encoding)
    {
        if (depth >= DepthLimit)
        {
            throw ParamException.TooDeep();
        }

        string k, after;
        if (depth == 0)
        {
            // Start of parsing, don't treat [] or [ at start of string specially.
            var start = name.Length > 1 ? name.IndexOf('[', 1) : -1;
            (k, after) = start >= 0 ? (name[..start], name[start..]) : (name, "");
        }
        else if (name.StartsWith("[]", StringComparison.Ordinal))
        {
            (k, after) = ("[]", name[2..]);
        }
        else if (name.StartsWith('[') && name.IndexOf(']', 1) is var close and > 0)
        {
            (k, after) = (name[1..close], name[(close + 1)..]);
        }
        else
        {
            // Probably malformed input, nested but not starting with [.
            (k, after) = (name, "");
        }

        if (k.Length == 0)
        {
            return null;
        }

        var key = Decode(k, encoding) ?? throw new ParamException(ParamErrorKind.Invalid, $"Invalid encoding for parameter: {Scrub(k)}");

        if (v is RawValue raw)
        {
            v = DecodeValue(raw);
        }

        if (after.Length == 0)
        {
            if (k == "[]" && depth != 0)
            {
                return v is null ? new List<object?>() : new List<object?> { v };
            }
            parameters[key] = v;
        }
        else if (after == "[")
        {
            parameters[Decode(name, encoding)!] = v;
        }
        else if (after == "[]")
        {
            var array = ArraySlot(parameters, key);
            if (v is not null)
            {
                array.Add(v);
            }
        }
        else if (after.StartsWith("[]", StringComparison.Ordinal))
        {
            // Recognize x[][y] (hash inside array) parameters; otherwise nest what follows the [].
            var childKey = after.Length > 3 && after[2] == '[' && after.EndsWith(']') && after[3..^1] is { Length: > 0 } inner && inner.IndexOfAny(['[', ']']) < 0
                ? inner
                : after[2..];
            var array = ArraySlot(parameters, key);
            if (array.Count > 0 && array[^1] is ParamHash last && !HasKey(last, childKey, encoding))
            {
                StoreNestedParam(last, childKey, v, depth + 1, encoding);
            }
            else
            {
                var child = new ParamHash();
                array.Add(StoreNestedParam(child, childKey, v, depth + 1, encoding));
            }
        }
        else
        {
            var existing = parameters[key];
            var child = existing switch
            {
                null => new ParamHash(),
                ParamHash hash => hash,
                _ => throw new ParamException(ParamErrorKind.Type, $"expected Hash (got {ParamValues.RubyClassName(existing)}) for param `{key}'"),
            };
            parameters[key] = StoreNestedParam(child, after, v, depth + 1, encoding);
        }

        return parameters;
    }

    // Rails checks a string value's encoding at the top level, once its key is known to be non-empty.
    static string DecodeValue(RawValue raw) =>
        Decode(Encoding.Latin1.GetString(raw.Bytes), raw.Encoding)
            ?? throw new ParamException(ParamErrorKind.Invalid, $"Invalid encoding for parameter: {Scrub(Encoding.Latin1.GetString(raw.Bytes))}");

    // params[k] ||= [] followed by the Array type check.
    static List<object?> ArraySlot(ParamHash parameters, string key)
    {
        var existing = parameters[key];
        if (existing is null)
        {
            var array = new List<object?>();
            parameters[key] = array;
            return array;
        }
        return existing as List<object?>
            ?? throw new ParamException(ParamErrorKind.Type, $"expected Array (got {ParamValues.RubyClassName(existing)}) for param `{key}'");
    }

    // params_hash_has_key?: walks key.split(/[\[\]]+/), skipping empty parts.
    static bool HasKey(ParamHash hash, string key, Encoding encoding)
    {
        if (key.Contains("[]", StringComparison.Ordinal))
        {
            return false;
        }
        // Ruby's regexp split raises ArgumentError on a broken string, which from_pairs re-raises
        // as InvalidParameterError.
        var decoded = Decode(key, encoding) ?? throw new ParamException(ParamErrorKind.Invalid, $"invalid byte sequence in {encoding.WebName}");
        object? current = hash;
        foreach (var part in decoded.Split(['[', ']'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (current is not ParamHash map || !map.TryGetValue(part, out current))
            {
                return false;
            }
        }
        return true;
    }

    // A byte string (one char per byte) decoded in its encoding, or null when it isn't valid there.
    static string? Decode(string byteString, Encoding encoding)
    {
        if (Ascii.IsValid(byteString))
        {
            return byteString;
        }
        var bytes = Encoding.Latin1.GetBytes(byteString);
        if (encoding.CodePage == Encoding.Latin1.CodePage)
        {
            return byteString;
        }
        try
        {
            var strict = encoding.CodePage == Encoding.UTF8.CodePage
                ? StrictUtf8
                : Encoding.GetEncoding(encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            return strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    // String#scrub, for error messages.
    static string Scrub(string byteString) => Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(byteString));
}
