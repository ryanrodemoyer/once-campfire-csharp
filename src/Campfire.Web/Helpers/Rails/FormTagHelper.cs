using System.Text.RegularExpressions;

namespace Campfire.Web.Helpers;

// ActionView::Helpers::FormTagHelper: the tag helpers that don't take a model, and the form
// wrapper form_with and button_to share. The reference's defaults: default_enforce_utf8 off,
// remove_hidden_field_autocomplete on, no content exfiltration prevention markup.
// reference: actionview/lib/action_view/helpers/form_tag_helper.rb
public partial class View
{
    /// <summary><c>text_field_tag(name, value, options)</c>.</summary>
    public static SafeString TextFieldTag(string name, object? value = null, HtmlOptions? options = null)
    {
        var html = new HtmlOptions { { "type", "text" }, { "name", name }, { "id", SanitizeToId(name) }, { "value", value } };
        return TagHelper.Tag("input", html.Merge(options));
    }

    /// <summary><c>hidden_field_tag(name, value, options)</c>.</summary>
    public static SafeString HiddenFieldTag(string name, object? value = null, HtmlOptions? options = null)
    {
        var html = (options ?? []).Merge(new HtmlOptions { { "type", "hidden" } });
        return TextFieldTag(name, value, html);
    }

    /// <summary><c>check_box_tag(name, value = "1", checked = false, options)</c>.</summary>
    public static SafeString CheckBoxTag(string name, object? value = null, bool isChecked = false, HtmlOptions? options = null)
    {
        var html = new HtmlOptions { { "type", "checkbox" }, { "name", name }, { "id", SanitizeToId(name) }, { "value", value ?? "1" } }.Merge(options);
        if (isChecked)
        {
            html["checked"] = "checked";
        }
        return TagHelper.Tag("input", html);
    }

    /// <summary><c>button_tag(content, options)</c>: a submit button named <c>button</c> unless overridden.</summary>
    public static SafeString ButtonTag(object? content, HtmlOptions? options = null) =>
        TagHelper.ContentTag("button", content ?? "Button", ButtonTagOptions(options));

    /// <summary><c>button_tag(options) do ... end</c>.</summary>
    public static IHtml ButtonTag(HtmlOptions? options, Action body) =>
        TagHelper.ContentTag("button", ButtonTagOptions(options), body);

    /// <summary><c>field_id(object_name, method_name, *suffixes, index:, namespace:)</c>.</summary>
    public static string FieldId(string? objectName, string methodName, IEnumerable<string>? suffixes = null, object? index = null, string? @namespace = null)
    {
        var sanitizedObjectName = FieldIdObjectPattern().Replace(objectName ?? "", "_");
        if (sanitizedObjectName.EndsWith('_'))
        {
            sanitizedObjectName = sanitizedObjectName[..^1];
        }
        var sanitizedMethodName = methodName.EndsWith('?') ? methodName[..^1] : methodName;

        var parts = new List<string?>
        {
            @namespace,
            sanitizedObjectName.Length == 0 ? null : sanitizedObjectName,
            sanitizedObjectName.Length == 0 || index is null ? null : RubyValues.ToS(index),
            sanitizedMethodName,
        };
        parts.AddRange(suffixes ?? []);
        return string.Join("_", parts.Where(part => part is not null));
    }

    /// <summary><c>field_name(object_name, method_name, multiple:, index:)</c>.</summary>
    public static string FieldName(string? objectName, string methodName, bool multiple = false, object? index = null)
    {
        var suffix = multiple ? "[]" : "";
        if (RubyValues.IsBlank(objectName))
        {
            return $"{methodName}{suffix}";
        }
        return index is not null
            ? $"{objectName}[{RubyValues.ToS(index)}][{methodName}]{suffix}"
            : $"{objectName}[{methodName}]{suffix}";
    }

    /// <summary><c>sanitize_to_id(name)</c>.</summary>
    public static string SanitizeToId(string name) =>
        SanitizeToIdPattern().Replace(name.Replace("]", "", StringComparison.Ordinal), "_");

    /// <summary>
    /// <c>html_options_for_form</c>, <c>extra_tags_for_form</c> and the opening tag: the form's
    /// attributes and the hidden fields that follow it.
    /// </summary>
    (HtmlOptions Attributes, SafeString ExtraTags) FormTagParts(string? action, HtmlOptions options)
    {
        var html = options.Clone();
        if (TagHelper.IsTruthy(html.Delete("multipart")))
        {
            html["enctype"] = "multipart/form-data";
        }
        if (action is null)
        {
            html.Delete("action");
        }
        else
        {
            html["action"] = action;
        }
        html["accept-charset"] = "UTF-8";
        if (TagHelper.IsTruthy(html.Delete("remote")))
        {
            html["data-remote"] = true;
        }
        if (TagHelper.IsTruthy(html["data-remote"]) && RubyValues.IsBlank(html["authenticity_token"]))
        {
            // embed_authenticity_token_in_remote_forms is false.
            html["authenticity_token"] = false;
        }
        else if (html["authenticity_token"] is true)
        {
            html["authenticity_token"] = null;
        }

        var authenticityToken = html.Delete("authenticity_token");
        var method = RubyValues.ToS(html.Delete("method")).ToLowerInvariant();
        var formAction = html["action"] is { } value ? RubyValues.ToS(value) : null;
        SafeString extraTags;
        switch (method)
        {
            case "get":
                html["method"] = "get";
                extraTags = SafeString.Empty;
                break;
            case "post" or "":
                html["method"] = "post";
                extraTags = TokenTag(authenticityToken, formAction, "post");
                break;
            default:
                html["method"] = "post";
                extraTags = OutputSafety.Concat(MethodTag(method), TokenTag(authenticityToken, formAction, method));
                break;
        }

        if (TagHelper.IsTruthy(html.Delete("enforce_utf8")))
        {
            var utf8 = TagHelper.Tag("input", new() { { "type", "hidden" }, { "name", "utf8" }, { "value", new SafeString("&#x2713;") } });
            extraTags = OutputSafety.Concat(utf8, extraTags);
        }
        return (html, extraTags);
    }

    static HtmlOptions ButtonTagOptions(HtmlOptions? options) =>
        new HtmlOptions { { "name", "button" }, { "type", "submit" } }.Merge(options);

    [GeneratedRegex(@"\]\[|[^-a-zA-Z0-9:.]")]
    private static partial Regex FieldIdObjectPattern();

    [GeneratedRegex(@"[^-a-zA-Z0-9:.]")]
    private static partial Regex SanitizeToIdPattern();
}
