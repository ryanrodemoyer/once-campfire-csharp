using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Campfire.RichText.Attachments;

/// <summary>
/// <c>JSON.parse</c> of a binary string, as json 2.21.2's parser (<c>ext/json/ext/parser/parser.c</c>)
/// reads it with the default options: comments allowed, duplicate keys allowed (the last wins),
/// no NaN, no trailing commas, no control characters in strings, and at most 100 nested
/// non-empty arrays and objects. Strings aren't checked for valid UTF-8.
/// </summary>
/// <remarks>
/// A failure raises <c>JSON::ParserError</c>, whose message quotes up to 32 bytes at the parser's
/// cursor for some errors only (<c>build_parse_error_message</c>). When the quote isn't valid
/// UTF-8 the message can't be logged, so the error is <see cref="RichTextRaisedException.Unloggable"/>.
/// </remarks>
public static class RubyJsonParser
{
    // parser_config_init's max_nesting
    const int maxNesting = 100;

    // PARSE_ERROR_FRAGMENT_LEN
    const int fragmentLength = 32;

    /// <summary>The parsed value, its strings decoded as UTF-8 with invalid bytes replaced.</summary>
    /// <exception cref="RichTextRaisedException"><c>JSON::ParserError</c> or <c>JSON::NestingError</c>.</exception>
    public static JsonElement Parse(byte[] source)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { SkipValidation = true }))
        {
            new State(source, writer).ParseDocument();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory, new JsonDocumentOptions { MaxDepth = maxNesting + 2 });
        return document.RootElement.Clone();
    }

    sealed class State(byte[] source, Utf8JsonWriter writer)
    {
        int cursor;
        int nesting;

        bool Eos => cursor >= source.Length;

        // peek(): 0 at the end, and for a NUL byte
        byte Peek => Eos ? (byte)0 : source[cursor];

        public void ParseDocument()
        {
            ParseValue();
            // json_ensure_eof
            EatWhitespace();
            if (!Eos)
            {
                throw Error(quotes: true);
            }
        }

        // json_parse_any's JSON_PHASE_VALUE, with a container's later phases inline
        void ParseValue()
        {
            EatWhitespace();
            var start = cursor;
            switch (Peek)
            {
                case (byte)'n':
                    MatchKeyword("null");
                    writer.WriteNullValue();
                    break;
                case (byte)'t':
                    MatchKeyword("true");
                    writer.WriteBooleanValue(true);
                    break;
                case (byte)'f':
                    MatchKeyword("false");
                    writer.WriteBooleanValue(false);
                    break;
                case (byte)'N' or (byte)'I':
                    // "unexpected token %s"
                    throw Error(quotes: true);
                case (byte)'-':
                    cursor++;
                    ParseNumber(start, negative: true);
                    break;
                case >= (byte)'0' and <= (byte)'9':
                    ParseNumber(start, negative: false);
                    break;
                case (byte)'"':
                    if (ParseString() is not { } value)
                    {
                        // "unexpected end of input, expected closing \""
                        throw Error(quotes: false);
                    }
                    writer.WriteStringValue(value);
                    break;
                case (byte)'[':
                    ParseArray();
                    break;
                case (byte)'{':
                    ParseObject();
                    break;
                case 0 when Eos:
                    // json_parse_any returns false: "unexpected end of input"
                    throw Error(quotes: false);
                default:
                    // "unexpected NULL byte: %s", "unexpected character: %s"
                    throw Error(quotes: true);
            }
        }

        void ParseArray()
        {
            cursor++;
            EatWhitespace();
            writer.WriteStartArray();
            if (Peek == ']')
            {
                cursor++;
                writer.WriteEndArray();
                return;
            }
            Nest();

            while (true)
            {
                ParseValue();
                EatWhitespace();
                if (Peek == ',')
                {
                    cursor++;
                    continue;
                }
                if (Peek == ']')
                {
                    cursor++;
                    nesting--;
                    writer.WriteEndArray();
                    return;
                }
                // "expected ',' or ']' after array value"
                throw Error(quotes: false);
            }
        }

        void ParseObject()
        {
            cursor++;
            EatWhitespace();
            writer.WriteStartObject();
            if (Peek == '}')
            {
                cursor++;
                writer.WriteEndObject();
                return;
            }
            Nest();

            for (var first = true; ; first = false)
            {
                // JSON_PHASE_OBJECT_KEY: "expected object key, got %s" or "got: %s"
                EatWhitespace();
                if (Peek != '"')
                {
                    throw Error(quotes: true);
                }
                var key = ParseString() ?? throw Error(quotes: false);

                // JSON_PHASE_OBJECT_COLON: the first pair's message quotes nothing
                EatWhitespace();
                if (Peek != ':')
                {
                    throw Error(quotes: !first);
                }
                cursor++;
                writer.WritePropertyName(key);
                ParseValue();

                // JSON_PHASE_OBJECT_COMMA: "expected ',' or '}' after object value, got: %s"
                EatWhitespace();
                if (Peek == ',')
                {
                    cursor++;
                    continue;
                }
                if (Peek == '}')
                {
                    cursor++;
                    nesting--;
                    writer.WriteEndObject();
                    return;
                }
                throw Error(quotes: true);
            }
        }

        void Nest()
        {
            if (++nesting > maxNesting)
            {
                throw new RichTextRaisedException($"JSON::NestingError: nesting of {nesting} is too deep");
            }
        }

        // json_match_keyword: "unexpected token %s" at the keyword
        void MatchKeyword(string keyword)
        {
            if (source.Length - cursor >= keyword.Length && source.AsSpan(cursor, keyword.Length).SequenceEqual(Encoding.ASCII.GetBytes(keyword)))
            {
                cursor += keyword.Length;
                return;
            }
            throw Error(quotes: true);
        }

        // json_parse_number: "invalid number: %s" at the number's start
        void ParseNumber(int start, bool negative)
        {
            var first = Peek;
            var digits = Digits();
            var invalid = (first == '0' && digits > 1) || (negative && digits == 0);
            if (!invalid && Peek == '.')
            {
                cursor++;
                invalid = Digits() == 0;
            }
            if (!invalid && (Peek | 0x20) == 'e')
            {
                cursor++;
                if (Peek is (byte)'-' or (byte)'+')
                {
                    cursor++;
                }
                invalid = Digits() == 0;
            }
            if (invalid)
            {
                cursor = start;
                throw Error(quotes: true);
            }
            writer.WriteRawValue(source.AsSpan(start, cursor - start), skipInputValidation: true);
        }

        int Digits()
        {
            var start = cursor;
            while (Peek is >= (byte)'0' and <= (byte)'9')
            {
                cursor++;
            }
            return cursor - start;
        }

        /// <summary>
        /// <c>json_parse_string</c> from the opening quote: the string, or null where the parser
        /// returns <c>Qundef</c> (the end of input inside it, or an escaped NUL).
        /// </summary>
        string? ParseString()
        {
            cursor++;
            var start = cursor;
            var escaped = false;
            while (true)
            {
                if (!Scan())
                {
                    return null;
                }
                var b = source[cursor];
                if (b == '"')
                {
                    break;
                }
                if (b == '\\')
                {
                    // The escaped byte is skipped whatever it is
                    escaped = true;
                    cursor += 2;
                    continue;
                }
                // "invalid ASCII control character in string: %s"
                throw Error(quotes: true);
            }

            var end = cursor;
            cursor++;
            if (!escaped)
            {
                return Encoding.UTF8.GetString(source, start, end - start);
            }
            var unescaped = Unescape(start, end);
            return unescaped is null ? null : Encoding.UTF8.GetString(unescaped.ToArray());
        }

        // string_scan: to the next '"', '\\' or control character, or the end
        bool Scan()
        {
            while (cursor < source.Length)
            {
                var b = source[cursor];
                if (b < 0x20 || b == '"' || b == '\\')
                {
                    return true;
                }
                cursor++;
            }
            cursor = source.Length;
            return false;
        }

        // json_string_unescape over [start, end), which holds at least one backslash
        List<byte>? Unescape(int start, int end)
        {
            var result = new List<byte>(end - start);
            var p = start; // the start of the literal run being copied
            var pe = start;
            while (pe < end && Array.IndexOf(source, (byte)'\\', pe, end - pe) is var backslash and >= 0)
            {
                pe = backslash;
                result.AddRange(source.AsSpan(p, pe - p));
                pe++;
                switch (source[pe])
                {
                    case (byte)'"' or (byte)'/':
                        p = pe;
                        break;
                    case (byte)'\\':
                        result.Add((byte)'\\');
                        p = ++pe;
                        break;
                    case (byte)'n':
                        result.Add((byte)'\n');
                        p = ++pe;
                        break;
                    case (byte)'r':
                        result.Add((byte)'\r');
                        p = ++pe;
                        break;
                    case (byte)'t':
                        result.Add((byte)'\t');
                        p = ++pe;
                        break;
                    case (byte)'b':
                        result.Add((byte)'\b');
                        p = ++pe;
                        break;
                    case (byte)'f':
                        result.Add((byte)'\f');
                        p = ++pe;
                        break;
                    case (byte)'u':
                        var ch = UnicodeEscape(++pe, end);
                        pe += 3;
                        if ((ch & 0xFC00) == 0xD800)
                        {
                            pe++;
                            if (pe > end - 6 || source[pe] != '\\' || source[pe + 1] != 'u')
                            {
                                // "incomplete surrogate pair at %s", quoting from the literal run
                                cursor = p;
                                throw Error(quotes: true);
                            }
                            var low = UnicodeEscape(pe + 2, end);
                            if ((low & 0xFC00) != 0xDC00)
                            {
                                // "invalid surrogate pair at %s"
                                cursor = p;
                                throw Error(quotes: true);
                            }
                            ch = (((ch & 0x3F) << 10) | ((((ch >> 6) & 0xF) + 1) << 16) | (low & 0x3FF));
                            pe += 5;
                        }
                        AppendUtf8(result, ch);
                        p = ++pe;
                        break;
                    case 0:
                        return null;
                    default:
                        // A control character ("Invalid unescaped newline character (\\n) in
                        // string: %s", "invalid ASCII control character in string: %s") or
                        // "invalid escape character in string: %s", all at the backslash
                        cursor = pe - 1;
                        throw Error(quotes: true);
                }
            }
            result.AddRange(source.AsSpan(p, end - p));
            return result;
        }

        // unescape_unicode: "incomplete unicode character escape sequence at %s" at the backslash
        uint UnicodeEscape(int at, int end)
        {
            if (at > end - 4 || !IsHex(source[at]) || !IsHex(source[at + 1]) || !IsHex(source[at + 2]) || !IsHex(source[at + 3]))
            {
                cursor = at - 2;
                throw Error(quotes: true);
            }
            return Convert.ToUInt32(Encoding.ASCII.GetString(source, at, 4), 16);
        }

        static bool IsHex(byte b) => b is (>= (byte)'0' and <= (byte)'9') or (>= (byte)'a' and <= (byte)'f') or (>= (byte)'A' and <= (byte)'F');

        // convert_UTF32_to_UTF8, which encodes a lone surrogate as the three bytes it would be
        static void AppendUtf8(List<byte> result, uint ch)
        {
            if (ch <= 0x7F)
            {
                result.Add((byte)ch);
            }
            else if (ch <= 0x7FF)
            {
                result.Add((byte)(0xC0 | (ch >> 6)));
                result.Add((byte)(0x80 | (ch & 0x3F)));
            }
            else if (ch <= 0xFFFF)
            {
                result.Add((byte)(0xE0 | (ch >> 12)));
                result.Add((byte)(0x80 | ((ch >> 6) & 0x3F)));
                result.Add((byte)(0x80 | (ch & 0x3F)));
            }
            else
            {
                result.Add((byte)(0xF0 | (ch >> 18)));
                result.Add((byte)(0x80 | ((ch >> 12) & 0x3F)));
                result.Add((byte)(0x80 | ((ch >> 6) & 0x3F)));
                result.Add((byte)(0x80 | (ch & 0x3F)));
            }
        }

        // json_eat_whitespace, comments included: "unexpected token %s" at a lone '/', and
        // "unterminated comment, expected closing '*/'", which quotes nothing
        void EatWhitespace()
        {
            while (true)
            {
                switch (Peek)
                {
                    case (byte)' ' or (byte)'\n' or (byte)'\t' or (byte)'\r':
                        cursor++;
                        break;
                    case (byte)'/':
                        var start = cursor;
                        cursor++;
                        if (Peek == '/')
                        {
                            var newline = Array.IndexOf(source, (byte)'\n', cursor);
                            cursor = newline < 0 ? source.Length : newline + 1;
                        }
                        else if (Peek == '*')
                        {
                            cursor++;
                            while (true)
                            {
                                var star = Array.IndexOf(source, (byte)'*', cursor);
                                if (star < 0)
                                {
                                    throw Error(quotes: false);
                                }
                                cursor = star + 1;
                                if (Peek == '/')
                                {
                                    cursor++;
                                    break;
                                }
                            }
                        }
                        else
                        {
                            cursor = start;
                            throw Error(quotes: true);
                        }
                        break;
                    default:
                        return;
                }
            }
        }

        RichTextRaisedException Error(bool quotes) =>
            new("JSON::ParserError", unloggable: quotes && !QuoteIsValidUtf8());

        /// <summary>
        /// Whether <c>build_parse_error_message</c>'s quote of the bytes at the cursor is valid
        /// UTF-8: up to 32 bytes, stopped at ASCII whitespace or NUL, less a trailing partial
        /// character. With nothing to quote it prints from the cursor to the next NUL instead,
        /// and at the end it prints <c>EOF</c>.
        /// </summary>
        bool QuoteIsValidUtf8()
        {
            if (Eos)
            {
                return true;
            }

            var length = 0;
            while (length < fragmentLength && cursor + length < source.Length && source[cursor + length] is not (0 or (byte)'\n' or (byte)' ' or (byte)'\t' or (byte)'\r'))
            {
                length++;
            }

            if (length == 0)
            {
                var nul = Array.IndexOf(source, (byte)0, cursor);
                return System.Text.Unicode.Utf8.IsValid(source.AsSpan(cursor, (nul < 0 ? source.Length : nul) - cursor));
            }

            // buffer[len] is the last quoted byte: drop trailing continuation bytes, then a lead byte
            while (length > 0 && source[cursor + length - 1] is >= 0x80 and < 0xC0)
            {
                length--;
            }
            if (length > 0 && source[cursor + length - 1] >= 0xC0)
            {
                length--;
            }
            return System.Text.Unicode.Utf8.IsValid(source.AsSpan(cursor, length));
        }
    }
}
