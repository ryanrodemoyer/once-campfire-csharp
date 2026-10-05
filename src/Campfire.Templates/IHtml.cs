namespace Campfire.Templates;

/// <summary>
/// HTML-safe content that writes itself, such as a block helper or a partial. Appending it never
/// escapes, like a Ruby object whose <c>to_s</c> is <c>html_safe?</c>.
/// </summary>
public interface IHtml
{
    void WriteTo(HtmlWriter writer);
}
