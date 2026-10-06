using System.Text;

namespace Campfire.Web.Helpers.Rails;

/// <summary>
/// <c>ActionView::Helpers::TagHelper</c>: attribute serialization, <c>tag</c>, <c>content_tag</c>
/// and <c>token_list</c>. reference: actionview/lib/action_view/helpers/tag_helper.rb
/// (rails@1a02651, the revision reference/Gemfile.lock pins).
/// </summary>
public static class TagHelper
{
    static readonly HashSet<string> BooleanAttributes = new(StringComparer.Ordinal)
    {
        "allowfullscreen", "allowpaymentrequest", "async", "autofocus", "autoplay", "checked", "compact",
        "controls", "declare", "default", "defaultchecked", "defaultmuted", "defaultselected", "defer",
        "disabled", "enabled", "formnovalidate", "hidden", "indeterminate", "inert", "ismap", "itemscope",
        "loop", "multiple", "muted", "nohref", "nomodule", "noresize", "noshade", "novalidate", "nowrap",
        "open", "pauseonexit", "playsinline", "readonly", "required", "reversed", "scoped", "seamless",
        "selected", "sortable", "truespeed", "typemustmatch", "visible",
    };

    static readonly System.Buffers.SearchValues<char> InvalidTagNameChars = System.Buffers.SearchValues.Create(" \t\n\v\f\r/>");

    /// <summary>
    /// The legacy <c>tag(name, options, open)</c>: a void tag ending in <c> /&gt;</c>, or just the
    /// opening tag when <paramref name="open"/>.
    /// </summary>
    public static SafeString Tag(string name, HtmlOptions? options = null, bool open = false, bool escape = true)
    {
        EnsureValidHtml5TagName(name);
        return new SafeString($"<{name}{(options is null ? null : TagOptions(options, escape))}{(open ? ">" : " />")}");
    }

    /// <summary><c>content_tag(name, content, options)</c>.</summary>
    public static SafeString ContentTag(string name, object? content, HtmlOptions? options = null, bool escape = true)
    {
        EnsureValidHtml5TagName(name);
        return ContentTagString(name, content, options, escape);
    }

    /// <summary><c>content_tag(name, options) do ... end</c>.</summary>
    public static IHtml ContentTag(string name, HtmlOptions? options, Action body)
    {
        EnsureValidHtml5TagName(name);
        return new ContentTagBlock(name, options, escape: true, body);
    }

    /// <summary><c>TagBuilder#content_tag_string</c>.</summary>
    public static SafeString ContentTagString(string name, object? content, HtmlOptions? options, bool escape = true)
    {
        var tagOptions = options is null ? null : TagOptions(options, escape);
        var text = escape && RubyValues.IsPresent(content) ? RubyValues.UnwrappedHtmlEscape(content) : RubyValues.ToS(content);
        return new SafeString($"<{name}{tagOptions}>{PreContent(name)}{text}</{name}>");
    }

    /// <summary><c>TagBuilder#self_closing_tag_string</c>: <c>&lt;name attrs&gt;</c> for HTML5 void elements.</summary>
    public static SafeString VoidTag(string name, HtmlOptions? options, bool escape = true, string suffix = ">") =>
        new($"<{name}{(options is null ? null : TagOptions(options, escape))}{suffix}");

    /// <summary>
    /// <c>TagBuilder#tag_options</c>: every attribute with its leading space, or null when there are
    /// none. <c>data</c> and <c>aria</c> hashes expand into prefixed attributes, boolean attributes
    /// are written as <c>key="key"</c> when truthy, and nil values are skipped.
    /// </summary>
    public static string? TagOptions(HtmlOptions? options, bool escape = true)
    {
        if (options is null || options.Count == 0)
        {
            return null;
        }

        var output = new StringBuilder();
        foreach (var (key, value) in options)
        {
            if (key == "data" && value is HtmlOptions data)
            {
                foreach (var (dataKey, dataValue) in data)
                {
                    if (dataValue is not null)
                    {
                        output.Append(' ').Append(PrefixTagOption("data", dataKey, dataValue, escape));
                    }
                }
            }
            else if (key == "aria" && value is HtmlOptions aria)
            {
                foreach (var (ariaKey, ariaValue) in aria)
                {
                    if (ariaValue is null)
                    {
                        continue;
                    }

                    object tokenValue;
                    if (ariaValue is HtmlOptions || RubyValues.AsArray(ariaValue) is not null)
                    {
                        var tokens = BuildTagValues(ariaValue);
                        if (tokens.Count == 0)
                        {
                            continue;
                        }
                        tokenValue = OutputSafety.SafeJoin(tokens, " ");
                    }
                    else
                    {
                        tokenValue = RubyValues.IsHtmlSafe(ariaValue) ? ariaValue : RubyValues.ToS(ariaValue);
                    }
                    output.Append(' ').Append(PrefixTagOption("aria", ariaKey, tokenValue, escape));
                }
            }
            else if (BooleanAttributes.Contains(key))
            {
                if (IsTruthy(value))
                {
                    output.Append(' ').Append(key).Append("=\"").Append(key).Append('"');
                }
            }
            else if (value is not null)
            {
                output.Append(' ').Append(TagOption(key, value, escape));
            }
        }
        return output.Length == 0 ? null : output.ToString();
    }

    /// <summary><c>token_list</c> / <c>class_names</c>.</summary>
    public static SafeString TokenList(params object?[] args)
    {
        var tokens = new List<object?>();
        foreach (var value in BuildTagValues(args))
        {
            foreach (var token in System.Net.WebUtility.HtmlDecode(value).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!tokens.Contains(token))
                {
                    tokens.Add(token);
                }
            }
        }
        return OutputSafety.SafeJoin(tokens, " ");
    }

    /// <summary><c>build_tag_values</c>: strings, arrays flattened, and the truthy keys of a hash.</summary>
    public static List<string> BuildTagValues(params object?[] args)
    {
        var values = new List<string>();
        foreach (var value in args)
        {
            if (value is HtmlOptions hash)
            {
                foreach (var (key, condition) in hash)
                {
                    if (IsTruthy(condition) && RubyValues.IsPresent(key))
                    {
                        values.Add(key);
                    }
                }
            }
            else if (RubyValues.AsArray(value) is { } array)
            {
                values.AddRange(BuildTagValues([.. array]));
            }
            else if (RubyValues.IsPresent(value))
            {
                values.Add(RubyValues.ToS(value));
            }
        }
        return values;
    }

    /// <summary><c>ensure_valid_html5_tag_name</c>.</summary>
    public static void EnsureValidHtml5TagName(string name)
    {
        var valid = name.Length > 0 && char.IsAsciiLetter(name[0]) &&
            name.AsSpan(1).IndexOfAny(InvalidTagNameChars) < 0;
        if (!valid)
        {
            throw new ArgumentException($"Invalid HTML5 tag name: \"{name}\"", nameof(name));
        }
    }

    /// <summary>Ruby truthiness: everything but nil and false.</summary>
    public static bool IsTruthy(object? value) => value is not (null or false);

    static string PreContent(string name) => name == "textarea" ? "\n" : "";

    static string TagOption(string key, object value, bool escape)
    {
        if (escape)
        {
            key = XmlNameEscape(key);
        }

        string text;
        if (value is HtmlOptions || RubyValues.AsArray(value) is not null)
        {
            var items = value is HtmlOptions hash
                ? [.. hash.Select(entry => (object?)new[] { entry.Key, entry.Value })]
                : RubyValues.AsArray(value)!;
            if (key == "class")
            {
                items = [.. BuildTagValues(value)];
            }
            text = escape ? OutputSafety.SafeJoin(items, " ").Value : string.Join(" ", items.Select(RubyValues.ToS));
        }
        else
        {
            text = escape ? RubyValues.UnwrappedHtmlEscape(value) : RubyValues.ToS(value);
        }

        if (text.Contains('"'))
        {
            text = text.Replace("\"", "&quot;", StringComparison.Ordinal);
        }
        return $"{key}=\"{text}\"";
    }

    static string PrefixTagOption(string prefix, string key, object value, bool escape)
    {
        key = $"{prefix}-{key.Replace('_', '-')}";
        if (value is not (string or SafeString))
        {
            value = RubyValues.ToJson(value);
        }
        return TagOption(key, value, escape);
    }

    // ERB::Util.xml_name_escape, codepoint by codepoint as Ruby indexes strings.
    static string XmlNameEscape(string name)
    {
        if (RubyValues.IsBlank(name))
        {
            return "";
        }

        var output = new StringBuilder(name.Length);
        var first = true;
        foreach (var rune in name.EnumerateRunes())
        {
            var valid = first ? IsNameStart(rune.Value) : IsNameStart(rune.Value) || IsNameFollowing(rune.Value);
            if (valid)
            {
                output.Append(rune.ToString());
            }
            else
            {
                output.Append('_');
            }
            first = false;
        }
        return output.ToString();
    }

    static bool IsNameStart(int c) =>
        c is '@' or ':' or '_' or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or
        (>= 0xC0 and <= 0xD6) or (>= 0xD8 and <= 0xF6) or (>= 0xF8 and <= 0x2FF) or
        (>= 0x370 and <= 0x37D) or (>= 0x37F and <= 0x1FFF) or (>= 0x200C and <= 0x200D) or
        (>= 0x2070 and <= 0x218F) or (>= 0x2C00 and <= 0x2FEF) or (>= 0x3001 and <= 0xD7FF) or
        (>= 0xF900 and <= 0xFDCF) or (>= 0xFDF0 and <= 0xFFFD) or (>= 0x10000 and <= 0xEFFFF);

    static bool IsNameFollowing(int c) =>
        c is '-' or '.' or (>= '0' and <= '9') or 0xB7 or (>= 0x300 and <= 0x36F) or (>= 0x203F and <= 0x2040);

    // A content tag whose content is a template block, written straight to the output buffer.
    sealed class ContentTagBlock(string name, HtmlOptions? options, bool escape, Action body) : IHtml
    {
        public void WriteTo(HtmlWriter writer)
        {
            writer.AppendRaw($"<{name}{(options is null ? null : TagOptions(options, escape))}>{PreContent(name)}");
            body();
            writer.AppendRaw($"</{name}>");
        }
    }
}
