using System.Text;

namespace Campfire.RailsCompat.Params;

/// <summary>
/// A <c>key=value</c> pair before the builder sees it: %-decoded bytes, a file, or no value at all
/// (no <c>=</c>, which Rails turns into nil). The builder checks the bytes against
/// <see cref="Encoding"/>, which is UTF-8 except for multipart text parts that name a charset.
/// </summary>
public sealed record ParamPair(byte[] Key, object? Value, Encoding Encoding)
{
    public static ParamPair Text(string key, string? value) =>
        new(Encoding.UTF8.GetBytes(key), value is null ? null : Encoding.UTF8.GetBytes(value), Encoding.UTF8);

    public static ParamPair File(string key, UploadedFile file) => new(Encoding.UTF8.GetBytes(key), file, Encoding.UTF8);
}

/// <summary>
/// Splitting and decoding <c>key=value&amp;...</c> strings: <c>ActionDispatch::QueryParser.each_pair</c>
/// for query strings, and <c>Rack::QueryParser#parse_query_pairs</c> (which adds limits) for
/// urlencoded bodies.
/// </summary>
public static class QueryParser
{
    /// <summary><c>Rack::QueryParser::BYTESIZE_LIMIT</c>.</summary>
    public const int FormByteSizeLimit = 4 * 1024 * 1024;

    /// <summary><c>Rack::QueryParser::PARAMS_LIMIT</c>.</summary>
    public const int FormParamsLimit = 4096;

    /// <summary>
    /// <c>ActionDispatch::QueryParser.each_pair</c>: split on <c>/&amp; */</c>, skip empty parts,
    /// split each on its first <c>=</c>, and <c>URI.decode_www_form_component</c> both halves. Rails
    /// puts no limits on the query string.
    /// </summary>
    public static IEnumerable<ParamPair> EachPair(ReadOnlyMemory<byte> query)
    {
        var start = 0;
        while (start <= query.Length)
        {
            var span = query.Span;
            var amp = span[start..].IndexOf((byte)'&');
            var end = amp < 0 ? query.Length : start + amp;
            if (end > start)
            {
                yield return DecodePair(query.Span[start..end]);
            }
            if (amp < 0)
            {
                yield break;
            }
            start = end + 1;
            while (start < query.Length && query.Span[start] == (byte)' ')
            {
                start++;
            }
        }
    }

    public static IEnumerable<ParamPair> EachPair(string query) => EachPair(Encoding.UTF8.GetBytes(query));

    /// <summary>
    /// <c>Rack::Request#form_pairs</c> for an urlencoded body (as read with
    /// <c>read(bytesize_limit + 2)</c>): the trailing NUL Safari once appended is dropped, then Rack's
    /// size and count limits apply before the pairs are decoded.
    /// </summary>
    public static List<ParamPair> FormPairs(ReadOnlySpan<byte> body)
    {
        if (body.Length > 0 && body[^1] == 0)
        {
            body = body[..^1];
        }
        if (body.IsEmpty)
        {
            return [];
        }
        if (body.Length > FormByteSizeLimit)
        {
            throw new ParamException(ParamErrorKind.Limit, $"total query size exceeds limit ({FormByteSizeLimit})");
        }
        // qs.split(/& */, PARAMS_LIMIT + 1) keeps empty parts, so every & counts.
        var parts = body.Count((byte)'&') + 1;
        if (parts > FormParamsLimit)
        {
            throw new ParamException(ParamErrorKind.Limit, $"total number of query parameters ({parts}) exceeds limit ({FormParamsLimit})");
        }
        return [.. EachPair(body.ToArray())];
    }

    static ParamPair DecodePair(ReadOnlySpan<byte> part)
    {
        var equals = part.IndexOf((byte)'=');
        return equals < 0
            ? new ParamPair(DecodeWwwFormComponent(part), null, Encoding.UTF8)
            : new ParamPair(DecodeWwwFormComponent(part[..equals]), DecodeWwwFormComponent(part[(equals + 1)..]), Encoding.UTF8);
    }

    /// <summary>
    /// Ruby's <c>URI.decode_www_form_component</c>: <c>+</c> is a space, <c>%XX</c> is a byte, and a
    /// <c>%</c> without two hex digits after it is an error (Rails' InvalidParameterError).
    /// </summary>
    public static byte[] DecodeWwwFormComponent(ReadOnlySpan<byte> component)
    {
        var decoded = new byte[component.Length];
        var length = 0;
        for (var i = 0; i < component.Length; i++)
        {
            var b = component[i];
            if (b == (byte)'+')
            {
                decoded[length++] = (byte)' ';
            }
            else if (b == (byte)'%')
            {
                if (i + 2 >= component.Length || !char.IsAsciiHexDigit((char)component[i + 1]) || !char.IsAsciiHexDigit((char)component[i + 2]))
                {
                    throw new ParamException(ParamErrorKind.Invalid, $"invalid %-encoding ({Encoding.Latin1.GetString(component)})");
                }
                decoded[length++] = (byte)((HexValue(component[i + 1]) << 4) | HexValue(component[i + 2]));
                i += 2;
            }
            else
            {
                decoded[length++] = b;
            }
        }
        return decoded[..length];
    }

    static int HexValue(byte digit) => digit <= '9' ? digit - '0' : (digit | 0x20) - 'a' + 10;
}
