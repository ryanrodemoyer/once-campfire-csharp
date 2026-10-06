using System.Collections;
using System.Globalization;
using System.Text.Json.Nodes;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.Ruby;

namespace Campfire.Web.Helpers.Rails;

/// <summary>
/// How the Ruby values helpers receive turn into text: <c>to_s</c>, Active Support's
/// <c>to_json</c>, <c>blank?</c>, and <c>ERB::Util.unwrapped_html_escape</c>. Only the value types
/// <see cref="HtmlOptions"/> documents are accepted; anything else throws instead of rendering
/// differently from Ruby.
/// </summary>
public static class RubyValues
{
    /// <summary><c>value.to_s</c>.</summary>
    public static string ToS(object? value) => value switch
    {
        null => "",
        string text => text,
        SafeString safe => safe.Value,
        bool flag => flag ? "true" : "false",
        int number => number.ToString(CultureInfo.InvariantCulture),
        long number => number.ToString(CultureInfo.InvariantCulture),
        double number => RubyFloat.ToS(number),
        IHtml html => Render(html).Value,
        _ => throw Unsupported(value),
    };

    /// <summary><c>html_safe?</c> after <c>to_s</c>.</summary>
    public static bool IsHtmlSafe(object? value) => value is SafeString or IHtml;

    /// <summary><c>ERB::Util.unwrapped_html_escape(value)</c>: <c>to_s</c>, escaped unless HTML-safe.</summary>
    public static string UnwrappedHtmlEscape(object? value) =>
        IsHtmlSafe(value) ? ToS(value) : SafeString.Escape(ToS(value)).Value;

    /// <summary><c>ERB::Util.html_escape(value)</c>.</summary>
    public static SafeString HtmlEscape(object? value) => new(UnwrappedHtmlEscape(value));

    /// <summary><c>value.blank?</c>.</summary>
    public static bool IsBlank(object? value) => value switch
    {
        null => true,
        bool flag => !flag,
        string text => IsRubyBlank(text),
        SafeString safe => IsRubyBlank(safe.Value),
        HtmlOptions options => options.Count == 0,
        IHtml html => IsRubyBlank(Render(html).Value),
        IEnumerable sequence => !sequence.GetEnumerator().MoveNext(),
        _ => false,
    };

    public static bool IsPresent(object? value) => !IsBlank(value);

    /// <summary>Active Support's <c>value.to_json</c>.</summary>
    public static string ToJson(object? value) => RailsJson.Encode(ToJsonNode(value));

    /// <summary>A value as an array, or null when it isn't one (a string is not an array).</summary>
    public static IReadOnlyList<object?>? AsArray(object? value) => value switch
    {
        string or HtmlOptions or null => null,
        IEnumerable sequence => sequence.Cast<object?>().ToList(),
        _ => null,
    };

    /// <summary>Renders self-writing content to a string.</summary>
    public static SafeString Render(IHtml html)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        html.WriteTo(new HtmlWriter(buffer));
        return new SafeString(System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    // String#blank?: empty or only whitespace as Ruby's [[:space:]] matches it.
    static bool IsRubyBlank(string text)
    {
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c))
            {
                return false;
            }
        }
        return true;
    }

    static JsonNode? ToJsonNode(object? value) => value switch
    {
        null => null,
        string text => JsonValue.Create(text),
        SafeString safe => JsonValue.Create(safe.Value),
        bool flag => JsonValue.Create(flag),
        int number => JsonValue.Create(number),
        long number => JsonValue.Create(number),
        double number => JsonValue.Create(number),
        HtmlOptions options => new JsonObject(options.Select(entry => KeyValuePair.Create(entry.Key, ToJsonNode(entry.Value)))),
        IEnumerable sequence => new JsonArray([.. sequence.Cast<object?>().Select(ToJsonNode)]),
        _ => throw Unsupported(value),
    };

    static NotSupportedException Unsupported(object value) =>
        new($"{value.GetType()} has no Ruby counterpart here; convert it to a string the way Ruby would first");
}
