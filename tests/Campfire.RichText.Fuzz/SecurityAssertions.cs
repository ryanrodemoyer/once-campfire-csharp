using Campfire.RichText.Html;

namespace Campfire.RichText.Fuzz;

/// <summary>
/// Security properties of presented HTML, asserted independently of Rails, since the port and the
/// reference could agree on something unsafe. The markup is parsed as a browser would parse it
/// inside the message, so obfuscation in the source doesn't hide anything.
/// </summary>
public static class SecurityAssertions
{
    static readonly HashSet<string> ScriptCapableElements =
    [
        "script", "style", "iframe", "frame", "frameset", "object", "embed", "applet", "base", "meta", "link", "form", "input", "button",
        "textarea", "select", "svg", "math", "template", "noscript", "xmp", "plaintext", "noembed", "noframes", "portal", "fencedframe",
    ];

    // Elements where href navigates or loads something (SVG and MathML elements are violations anyway)
    static readonly HashSet<string> HrefElements = ["a", "area", "base", "link"];

    static readonly HashSet<string> UrlAttributes =
    [
        "src", "action", "formaction", "poster", "cite", "background", "srcset", "data", "longdesc", "lowsrc", "dynsrc", "ping",
        "xlink:href", "codebase", "manifest", "icon",
    ];

    // Attributes that run script, load a document, or restyle the page, whatever their value
    static readonly HashSet<string> ForbiddenAttributes = ["style", "srcdoc", "http-equiv", "formaction", "xmlns", "is", "popovertarget"];

    static readonly string[] DangerousSchemes = ["javascript:", "vbscript:", "livescript:", "data:text/html", "data:image/svg", "data:application"];

    /// <summary>
    /// Every violation in a presented message, empty when it's safe to display. A sound message's
    /// presentation is the app's own markup (its play button), with nothing from the body in it.
    /// </summary>
    public static List<string> PresentationViolations(string presentation) =>
        presentation.StartsWith("<div class=\"sound\"", StringComparison.Ordinal) ? [] : Violations(presentation);

    /// <summary>Every violation in <paramref name="html"/>, empty when it's safe to display.</summary>
    public static List<string> Violations(string html)
    {
        var violations = new List<string>();
        foreach (var element in HtmlParser.ParseFragment(html).Descendants().OfType<HtmlElement>())
        {
            if (element.Namespace != HtmlNamespace.Html || ScriptCapableElements.Contains(element.Name))
            {
                violations.Add($"<{element.Name}>");
            }
            foreach (var attribute in element.Attributes)
            {
                var name = attribute.QualifiedName.ToLowerInvariant();
                if (name.StartsWith("on", StringComparison.Ordinal) || ForbiddenAttributes.Contains(name))
                {
                    violations.Add($"{element.Name}[{name}]");
                }
                if ((UrlAttributes.Contains(name) || (name == "href" && HrefElements.Contains(element.Name))) && DangerousUrl(attribute.Value))
                {
                    violations.Add($"{element.Name}[{name}={attribute.Value}]");
                }
            }
        }
        return violations;
    }

    // What a browser makes of a URL: leading and trailing C0 controls and spaces stripped, tabs and
    // newlines removed anywhere, scheme case-insensitive. Any comma-separated candidate counts
    // (srcset).
    static bool DangerousUrl(string value) =>
        value.Split(',').Any(candidate =>
        {
            var cleaned = new string([.. candidate.Where(c => c > ' ' && c != '\u007F')]).ToLowerInvariant();
            return DangerousSchemes.Any(scheme => cleaned.StartsWith(scheme, StringComparison.Ordinal));
        });
}
