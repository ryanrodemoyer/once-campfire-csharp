using Campfire.RichText.Attachments;
using Campfire.RichText.Html;

namespace Campfire.RichText.Sanitize;

/// <summary>
/// Nokogiri's <c>node[name] = value</c> (<c>StringValueCStr</c>) raises
/// <c>ArgumentError: string contains null byte</c>. <c>HtmlElement.SetAttribute</c> does not;
/// that check belongs to the HTML tree (R01). Call sites that assign a decoded value raise here
/// so both sides raise.
/// </summary>
static class NokogiriAttribute
{
    public static void Set(HtmlElement element, string name, string value)
    {
        if (value.Contains('\0'))
        {
            throw new RichTextRaisedException("ArgumentError: string contains null byte");
        }
        element.SetAttribute(name, value);
    }
}
