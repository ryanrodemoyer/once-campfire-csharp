namespace Campfire.Web.Helpers.Rails;

/// <summary>
/// The HTML5 tag builder, <c>tag.div(...)</c>. Content elements take content (escaped unless
/// HTML-safe) or a template block; void elements end in <c>&gt;</c>. Elements without a method
/// here go through <see cref="Element(string, object?, HtmlOptions?, bool)"/>, which dasherizes
/// the name like <c>tag.turbo_frame</c> does.
/// reference: actionview/lib/action_view/helpers/tag_helper.rb (TagBuilder)
/// </summary>
public static class Tag
{
    /// <summary><c>tag.some_name(content, **options)</c>, for any element.</summary>
    public static SafeString Element(string name, object? content = null, HtmlOptions? options = null, bool escape = true)
    {
        name = name.Replace('_', '-');
        TagHelper.EnsureValidHtml5TagName(name);
        return TagHelper.ContentTagString(name, content, options, escape);
    }

    /// <summary><c>tag.some_name(**options) do ... end</c>, for any element.</summary>
    public static IHtml Element(string name, HtmlOptions? options, Action body) =>
        TagHelper.ContentTag(name.Replace('_', '-'), options, body);

    /// <summary><c>tag.attributes(...)</c>: the attributes alone, without the leading space.</summary>
    public static SafeString Attributes(HtmlOptions options) => new(TagHelper.TagOptions(options)?.Trim() ?? "");

    public static SafeString A(object? content = null, HtmlOptions? options = null, bool escape = true) => Element("a", content, options, escape);
    public static IHtml A(HtmlOptions? options, Action body) => Element("a", options, body);
    public static SafeString Button(object? content = null, HtmlOptions? options = null, bool escape = true) => Element("button", content, options, escape);
    public static IHtml Button(HtmlOptions? options, Action body) => Element("button", options, body);
    public static SafeString Dd(object? content = null, HtmlOptions? options = null, bool escape = true) => Element("dd", content, options, escape);
    public static SafeString Details(object? content = null, HtmlOptions? options = null, bool escape = true) => Element("details", content, options, escape);
    public static IHtml Details(HtmlOptions? options, Action body) => Element("details", options, body);
    public static SafeString Div(object? content = null, HtmlOptions? options = null, bool escape = true) => Element("div", content, options, escape);
    public static IHtml Div(HtmlOptions? options, Action body) => Element("div", options, body);
    public static SafeString Dl(object? content = null, HtmlOptions? options = null, bool escape = true) => Element("dl", content, options, escape);
    public static SafeString Dt(object? content = null, HtmlOptions? options = null, bool escape = true) => Element("dt", content, options, escape);
    public static SafeString Figure(object? content = null, HtmlOptions? options = null, bool escape = true) => Element("figure", content, options, escape);
    public static SafeString Menu(object? content = null, HtmlOptions? options = null, bool escape = true) => Element("menu", content, options, escape);
    public static IHtml Menu(HtmlOptions? options, Action body) => Element("menu", options, body);
    public static SafeString P(object? content = null, HtmlOptions? options = null, bool escape = true) => Element("p", content, options, escape);
    public static SafeString Span(object? content = null, HtmlOptions? options = null, bool escape = true) => Element("span", content, options, escape);
    public static IHtml Span(HtmlOptions? options, Action body) => Element("span", options, body);
    public static SafeString Style(object? content = null, HtmlOptions? options = null, bool escape = true) => Element("style", content, options, escape);
    public static SafeString Summary(object? content = null, HtmlOptions? options = null, bool escape = true) => Element("summary", content, options, escape);
    public static SafeString Template(object? content = null, HtmlOptions? options = null, bool escape = true) => Element("template", content, options, escape);
    public static SafeString Time(object? content = null, HtmlOptions? options = null, bool escape = true) => Element("time", content, options, escape);
    public static SafeString Title(object? content = null, HtmlOptions? options = null, bool escape = true) => Element("title", content, options, escape);
    public static SafeString Video(object? content = null, HtmlOptions? options = null, bool escape = true) => Element("video", content, options, escape);

    public static SafeString Br(HtmlOptions? options = null, bool escape = true) => TagHelper.VoidTag("br", options, escape);
    public static SafeString Hr(HtmlOptions? options = null, bool escape = true) => TagHelper.VoidTag("hr", options, escape);
    public static SafeString Img(HtmlOptions? options = null, bool escape = true) => TagHelper.VoidTag("img", options, escape);
    public static SafeString Input(HtmlOptions? options = null, bool escape = true) => TagHelper.VoidTag("input", options, escape);
    public static SafeString Link(HtmlOptions? options = null, bool escape = true) => TagHelper.VoidTag("link", options, escape);
    public static SafeString Meta(HtmlOptions? options = null, bool escape = true) => TagHelper.VoidTag("meta", options, escape);
}
