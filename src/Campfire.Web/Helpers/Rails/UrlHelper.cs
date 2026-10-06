using Campfire.RailsCompat.Ruby;

namespace Campfire.Web.Helpers;

// ActionView::Helpers::UrlHelper with the reference's settings (load_defaults 8.2):
// button_to_generates_button_tag and remove_hidden_field_autocomplete on, no content exfiltration
// prevention markup. URLs are strings here: the typed route helpers in Routes build them.
// reference: actionview/lib/action_view/helpers/url_helper.rb
public partial class View
{
    static readonly string[] ButtonTagMethodVerbs = ["patch", "put", "delete"];

    /// <summary><c>link_to(name, url, html_options)</c>; a null name shows the URL.</summary>
    public static SafeString LinkTo(object? name, string url, HtmlOptions? htmlOptions = null)
    {
        var options = LinkOptions(url, htmlOptions);
        return TagHelper.ContentTag("a", name ?? url, options);
    }

    /// <summary><c>link_to(url, html_options) do ... end</c>.</summary>
    public static IHtml LinkTo(string url, HtmlOptions? htmlOptions, Action body) =>
        TagHelper.ContentTag("a", LinkOptions(url, htmlOptions), body);

    /// <summary>
    /// <c>button_to(name, url, html_options)</c>: a form posting to <paramref name="url"/> (false
    /// in Rails is a null URL) holding a <c>&lt;button&gt;</c>, the <c>_method</c> field for
    /// patch/put/delete, and the authenticity token.
    /// </summary>
    public SafeString ButtonTo(object? name, string? url, HtmlOptions? htmlOptions = null)
    {
        var form = BuildButtonTo(url, htmlOptions);
        var button = TagHelper.ContentTag("button", name ?? url, form.ButtonOptions);
        return TagHelper.ContentTag("form", OutputSafety.Concat(form.Before, button, form.After), form.FormOptions);
    }

    /// <summary><c>button_to(url, html_options) do ... end</c>.</summary>
    public IHtml ButtonTo(string? url, HtmlOptions? htmlOptions, Action body)
    {
        var form = BuildButtonTo(url, htmlOptions);
        var button = TagHelper.ContentTag("button", form.ButtonOptions, body);
        return new ButtonToBlock(TagHelper.TagOptions(form.FormOptions), form.Before, button, form.After);
    }

    /// <summary>
    /// <c>mail_to(email_address, name, html_options)</c>, with the <c>cc</c>, <c>bcc</c>,
    /// <c>body</c>, <c>subject</c> and <c>reply_to</c> options as query parameters.
    /// </summary>
    public static SafeString MailTo(string emailAddress, object? name = null, HtmlOptions? htmlOptions = null)
    {
        var options = htmlOptions?.Clone() ?? [];
        var extras = new List<string>();
        foreach (var item in new[] { "cc", "bcc", "body", "subject", "reply_to" })
        {
            var option = options.Delete(item);
            if (RubyValues.IsPresent(option))
            {
                extras.Add($"{item.Replace('_', '-')}={RubyEscape.UrlEncode(RubyValues.ToS(option))}");
            }
        }
        var query = extras.Count == 0 ? "" : "?" + string.Join("&", extras);
        var encoded = RubyEscape.UrlEncode(emailAddress).Replace("%40", "@", StringComparison.Ordinal);
        options["href"] = $"mailto:{encoded}{query}";
        return TagHelper.ContentTag("a", name ?? emailAddress, options);
    }

    /// <summary><c>url_for(:back)</c>: the referrer unless it is a <c>javascript:</c> URL.</summary>
    public string BackUrl() =>
        Referrer is { } referrer && !IsJavascriptUrl(referrer) ? referrer : "javascript:history.back()";

    // convert_options_to_data_attributes plus link_to's href.
    static HtmlOptions LinkOptions(string url, HtmlOptions? htmlOptions)
    {
        var options = ConvertOptionsToDataAttributes(htmlOptions);
        if (!TagHelper.IsTruthy(options["href"]))
        {
            options["href"] = url;
        }
        return options;
    }

    static HtmlOptions ConvertOptionsToDataAttributes(HtmlOptions? htmlOptions)
    {
        if (htmlOptions is null)
        {
            return [];
        }

        var options = htmlOptions.Clone();
        if (TagHelper.IsTruthy(options.Delete("remote")))
        {
            options["data-remote"] = "true";
        }
        if (options.Delete("method") is { } method)
        {
            var verb = RubyValues.ToS(method);
            if (!verb.Equals("get", StringComparison.OrdinalIgnoreCase) && !RubyValues.ToS(options["rel"]).Contains("nofollow", StringComparison.Ordinal))
            {
                options["rel"] = RubyValues.IsBlank(options["rel"]) ? "nofollow" : $"{RubyValues.ToS(options["rel"])} nofollow";
            }
            options["data-method"] = method;
        }
        return options;
    }

    ButtonToForm BuildButtonTo(string? url, HtmlOptions? htmlOptions)
    {
        var options = htmlOptions?.Clone() ?? [];
        var remote = options.Delete("remote");
        var parameters = options.Delete("params");
        var authenticityToken = options.Delete("authenticity_token");
        var method = RubyValues.ToS(options.Delete("method") is { } given && RubyValues.IsPresent(given) ? given : null);
        var methodTag = ButtonTagMethodVerbs.Contains(method) ? MethodTag(method) : SafeString.Empty;

        var formMethod = method == "get" ? "get" : "post";
        var formOptions = options.Delete("form") as HtmlOptions ?? [];
        if (!TagHelper.IsTruthy(formOptions["class"]))
        {
            formOptions["class"] = options.Delete("form_class") ?? "button_to";
        }
        formOptions["method"] = formMethod;
        formOptions["action"] = url;
        if (TagHelper.IsTruthy(remote))
        {
            formOptions["data-remote"] = true;
        }

        var tokenTag = formMethod == "post"
            ? TokenTag(authenticityToken, url, method.Length == 0 ? "post" : method)
            : SafeString.Empty;

        var buttonOptions = ConvertOptionsToDataAttributes(options);
        buttonOptions["type"] = "submit";

        var after = tokenTag.Value;
        if (parameters is HtmlOptions hash)
        {
            foreach (var (name, value) in ToFormParams(hash, null).OrderBy(pair => pair.Name, StringComparer.Ordinal))
            {
                after += TagHelper.Tag("input", new() { { "type", "hidden" }, { "name", name }, { "value", value } }).Value;
            }
        }
        return new(formOptions, buttonOptions, methodTag, new SafeString(after));
    }

    // to_form_params: nested hashes and arrays as Rack parameter names.
    static List<(string Name, string Value)> ToFormParams(object? attribute, string? prefix)
    {
        var parameters = new List<(string, string)>();
        if (attribute is HtmlOptions hash)
        {
            foreach (var (key, value) in hash)
            {
                parameters.AddRange(ToFormParams(value, prefix is null ? key : $"{prefix}[{key}]"));
            }
        }
        else if (RubyValues.AsArray(attribute) is { } array)
        {
            foreach (var value in array)
            {
                parameters.AddRange(ToFormParams(value, $"{prefix}[]"));
            }
        }
        else
        {
            parameters.Add((prefix ?? "", RubyValues.ToS(attribute)));
        }
        return [.. parameters.OrderBy(pair => pair.Item1, StringComparer.Ordinal)];
    }

    static bool IsJavascriptUrl(string url)
    {
        var colon = url.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && url[..colon].Equals("javascript", StringComparison.OrdinalIgnoreCase);
    }

    sealed record ButtonToForm(HtmlOptions FormOptions, HtmlOptions ButtonOptions, SafeString Before, SafeString After);

    sealed class ButtonToBlock(string? formAttributes, SafeString before, IHtml button, SafeString after) : IHtml
    {
        public void WriteTo(HtmlWriter writer)
        {
            writer.AppendRaw($"<form{formAttributes}>");
            writer.AppendRaw(before);
            button.WriteTo(writer);
            writer.AppendRaw(after);
            writer.AppendRaw("</form>");
        }
    }
}
