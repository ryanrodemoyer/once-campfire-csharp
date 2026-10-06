using System.Text;

namespace Campfire.Web.Helpers;

// ActionView::Helpers::CaptureHelper over the view's OutputFlow.
// reference: actionview/lib/action_view/helpers/capture_helper.rb, flows.rb
public partial class View
{
    readonly Dictionary<string, StringBuilder> flow = new(StringComparer.Ordinal);

    /// <summary>
    /// <c>content_for(name, content)</c>: appends to (or with <paramref name="flush"/>, replaces)
    /// what <c>yield name</c> writes. A plain string is escaped; null appends nothing.
    /// </summary>
    public void ContentFor(string name, object? content, bool flush = false)
    {
        if (content is null)
        {
            return;
        }

        var text = RubyValues.UnwrappedHtmlEscape(content);
        if (flush || !flow.TryGetValue(name, out var stored))
        {
            flow[name] = new StringBuilder(text);
        }
        else
        {
            stored.Append(text);
        }
    }

    /// <summary><c>content_for(name) do ... end</c>: captures the block's output from <paramref name="writer"/>.</summary>
    public void ContentFor(HtmlWriter writer, string name, Action body, bool flush = false) =>
        ContentFor(name, Capture(writer, body), flush);

    /// <summary><c>content_for(name)</c>: the stored content, or null when blank.</summary>
    public SafeString? ContentFor(string name) =>
        flow.TryGetValue(name, out var stored) && RubyValues.IsPresent(stored.ToString()) ? new SafeString(stored.ToString()) : null;

    /// <summary><c>content_for?(name)</c>.</summary>
    public bool HasContentFor(string name) => ContentFor(name) is not null;

    /// <summary><c>yield name</c> in a layout: the stored content, empty when there is none.</summary>
    public SafeString Yield(string name) =>
        flow.TryGetValue(name, out var stored) ? new SafeString(stored.ToString()) : SafeString.Empty;

    /// <summary>
    /// <c>render "partial"</c> in an expression tag: a template method run into the writer the
    /// tag appends to.
    /// </summary>
    public static IHtml Render(Action<HtmlWriter> template) => new RenderedTemplate(template);

    /// <summary>
    /// <c>capture { ... }</c> for a template block: what the block wrote. Rails falls back to the
    /// block's value when the output is blank; a C# block has no value, so blank output is returned
    /// as written.
    /// </summary>
    public static SafeString Capture(HtmlWriter writer, Action body) => writer.Capture(body);

    sealed class RenderedTemplate(Action<HtmlWriter> template) : IHtml
    {
        public void WriteTo(HtmlWriter writer) => template(writer);
    }
}
