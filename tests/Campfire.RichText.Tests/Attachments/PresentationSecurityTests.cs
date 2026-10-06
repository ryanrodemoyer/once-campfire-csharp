using Campfire.RichText.Attachments;
using Campfire.RichText.Html;
using Campfire.Vectors;

namespace Campfire.RichText.Tests.Attachments;

/// <summary>Security properties of every presented body, asserted without reference to Rails.</summary>
public class PresentationSecurityTests
{
    static readonly string[] DangerousElements =
    [
        "script", "style", "iframe", "frame", "frameset", "object", "embed", "applet", "base", "meta", "link", "form", "input",
        "button", "textarea", "select", "svg", "math", "template", "noscript", "xmp", "plaintext", "noembed",
    ];

    static readonly string[] UrlAttributes = ["href", "src", "action", "formaction", "poster", "cite", "background", "srcset", "data"];

    public static TheoryData<RichTextCase> Cases() => RichTextVectors.Cases();

    [Theory]
    [MemberData(nameof(Cases))]
    public void Presented_bodies_hold_no_script(RichTextCase vector)
    {
        if (MessagePresentation.Present(vector.Body, VectorRecords.Context(vector.Host)) is not Presentation.Html html)
        {
            return;
        }

        Assert.Empty(Violations(html.Value));
    }

    [Fact]
    public void A_mentioned_users_name_and_title_are_escaped()
    {
        var mallory = RichTextVectors.File.Users.Single(u => u.Key == "mallory");
        var user = new MentionUser(mallory.Id, mallory.Name, mallory.Title, mallory.AttachableSgid, mallory.UserPath, mallory.AvatarPath);

        var html = AttachmentPartials.RenderMention(user);

        Assert.Empty(Violations(html));
        Assert.DoesNotContain("<b>", html);
        Assert.Contains("Mallory &lt;b&gt;&amp;amp;&lt;/b&gt; &quot;Evil&quot;</span>", html);
    }

    [Fact]
    public void The_assertions_catch_planted_defects()
    {
        Assert.NotEmpty(Violations("<p onclick=\"x()\">a</p>"));
        Assert.NotEmpty(Violations("<a href=\" java\tscript:alert(1)\">a</a>"));
        Assert.NotEmpty(Violations("<svg><script>alert(1)</script></svg>"));
    }

    static List<string> Violations(string html)
    {
        var violations = new List<string>();
        foreach (var element in HtmlParser.ParseFragment(html).Descendants().OfType<HtmlElement>())
        {
            if (element.Namespace != HtmlNamespace.Html || DangerousElements.Contains(element.Name))
            {
                violations.Add($"<{element.Name}>");
            }
            foreach (var attribute in element.Attributes)
            {
                if (attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"{element.Name}[{attribute.Name}]");
                }
                if (UrlAttributes.Contains(attribute.Name) && DangerousUrl(attribute.Value))
                {
                    violations.Add($"{element.Name}[{attribute.Name}={attribute.Value}]");
                }
            }
        }
        return violations;
    }

    // What a browser would see: control characters and whitespace ignored, case folded
    static bool DangerousUrl(string value)
    {
        var cleaned = new string(value.Where(c => !char.IsControl(c) && !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
        return cleaned.StartsWith("javascript:", StringComparison.Ordinal) || cleaned.StartsWith("vbscript:", StringComparison.Ordinal)
            || cleaned.StartsWith("livescript:", StringComparison.Ordinal) || cleaned.StartsWith("data:text/html", StringComparison.Ordinal);
    }
}
