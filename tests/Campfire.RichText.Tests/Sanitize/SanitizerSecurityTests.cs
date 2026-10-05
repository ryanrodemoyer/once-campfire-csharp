using Campfire.RichText.Html;
using Campfire.RichText.Sanitize;

namespace Campfire.RichText.Tests.Sanitize;

public class SanitizerSecurityTests
{
    [Theory]
    [InlineData("<script>alert('xss')</script>")]
    [InlineData("<SCRIPT SRC=\"https://example.com/xss.js\"></SCRIPT>")]
    [InlineData("<script\n>alert(1)</script>")]
    [InlineData("<div><script>alert(1)</script>hello</div>")]
    [InlineData("<p>before<script>nested</script>after</p>")]
    [InlineData("<script><div>still in script</div></script>")]
    public void NoScriptTagsSurvive(string input)
    {
        var sanitized = SafeListSanitizer.Sanitize(input, SafeList.ContentFilter);
        Assert.DoesNotContain("<script", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("</script", sanitized, StringComparison.OrdinalIgnoreCase);

        var presentation = ContentFilters.ApplyTextMessagePresentationFilters(input);
        Assert.DoesNotContain("<script", presentation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("</script", presentation, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("<img src=\"x\" onload=\"alert(1)\">")]
    [InlineData("<a href=\"/\" onclick=\"steal()\">link</a>")]
    [InlineData("<div onmouseover=\"bad()\">hover</div>")]
    [InlineData("<b ONERROR=\"fail()\">bold</b>")]
    [InlineData("<span onfocus=\"hack()\">focus</span>")]
    public void NoEventHandlerAttributesSurvive(string input)
    {
        var sanitized = SafeListSanitizer.Sanitize(input, SafeList.ContentFilter);
        AssertNoOnAttributes(sanitized);

        var presentation = ContentFilters.ApplyTextMessagePresentationFilters(input);
        AssertNoOnAttributes(presentation);
    }

    [Theory]
    [InlineData("<a href=\"javascript:alert(1)\">click</a>")]
    [InlineData("<a href=\"JavaScript:alert(1)\">click</a>")]
    [InlineData("<a href=\"  javascript:alert(1)\">click</a>")]
    [InlineData("<a href=\"vbscript:msgbox(1)\">click</a>")]
    [InlineData("<a href=\"data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==\">click</a>")]
    [InlineData("<img src=\"javascript:alert(1)\">")]
    [InlineData("<img src=\"data:text/html,<h1>bad</h1>\">")]
    public void NoDangerousUrlsSurvive(string input)
    {
        var sanitized = SafeListSanitizer.Sanitize(input, SafeList.ContentFilter);
        Assert.DoesNotContain("javascript:", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vbscript:", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:text/html", sanitized, StringComparison.OrdinalIgnoreCase);

        var presentation = ContentFilters.ApplyTextMessagePresentationFilters(input);
        Assert.DoesNotContain("javascript:", presentation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vbscript:", presentation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:text/html", presentation, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("<div style=\"position: fixed; top: 0; left: 0;\">x</div>", "<div style=\"\">x</div>")]
    [InlineData("<div style=\"background: url(javascript:alert(1))\">x</div>", "<div style=\"\">x</div>")]
    [InlineData("<div style=\"behavior: url(x.htc)\">x</div>", "<div style=\"\">x</div>")]
    [InlineData("<div style=\"width: expression(alert(1))\">x</div>", "<div style=\"\">x</div>")]
    [InlineData("<div style=\"color: red; position: absolute;\">x</div>", "<div style=\"color:red;\">x</div>")]
    [InlineData("<div style=\"background-color: #ff0000; display: none;\">x</div>", "<div style=\"background-color:#ff0000;\">x</div>")]
    public void OnlyAllowlistedStylePropertiesSurvive(string input, string expected)
    {
        var sanitized = SafeListSanitizer.Sanitize(input, SafeList.ContentFilter);
        Assert.Equal(expected, sanitized);
    }

    [Fact]
    public void AllCorpusVectorOutputsSatisfySecurityProperties()
    {
        var vectorPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../reference-rust/crates/richtext/tests/corpus/expected.json"));

        if (!File.Exists(vectorPath))
        {
            vectorPath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "../../../../reference-rust/crates/richtext/tests/corpus/expected.json"));
        }

        var json = File.ReadAllText(vectorPath);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var cases = doc.RootElement.GetProperty("cases").EnumerateArray();

        foreach (var c in cases)
        {
            var body = c.GetProperty("body").GetString()!;
            var host = c.GetProperty("host").GetString()!;
            var filteredProp = c.GetProperty("filtered");

            if (filteredProp.TryGetProperty("ok", out var okProp))
            {
                var output = ContentFilters.ApplyTextMessagePresentationFilters(body, host);

                var fragment = HtmlParser.ParseFragment(output);

                // 1. No <script> tags as DOM elements
                var scriptElement = fragment.Descendants().OfType<HtmlElement>()
                    .FirstOrDefault(e => e.IsHtml("script"));
                Assert.Null(scriptElement);

                // 2. No on* attributes
                AssertNoOnAttributes(fragment);

                // 3. No javascript: or vbscript: or data:text/html URLs in attributes
                AssertNoDangerousUrls(fragment);

                // 4. No disallowed style properties
                AssertNoDisallowedStyles(fragment);
            }
        }
    }

    private static void AssertNoOnAttributes(string html) => AssertNoOnAttributes(HtmlParser.ParseFragment(html));

    private static void AssertNoOnAttributes(HtmlFragment fragment)
    {
        foreach (var element in fragment.Descendants().OfType<HtmlElement>())
        {
            foreach (var attr in element.Attributes)
            {
                Assert.False(
                    attr.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase),
                    $"Found event handler attribute '{attr.Name}' on <{element.Name}>");
            }
        }
    }

    private static void AssertNoDangerousUrls(HtmlFragment fragment)
    {
        ReadOnlySpan<string> uriAttrs = ["href", "src", "action", "cite"];
        foreach (var element in fragment.Descendants().OfType<HtmlElement>())
        {
            foreach (var attrName in uriAttrs)
            {
                var val = element.GetAttribute(attrName);
                if (val is null) continue;

                var trimmed = val.Trim();
                Assert.False(
                    trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase),
                    $"Found dangerous URI '{val}' in attribute '{attrName}' on <{element.Name}>");
            }
        }
    }

    private static void AssertNoDisallowedStyles(HtmlFragment fragment)
    {
        foreach (var element in fragment.Descendants().OfType<HtmlElement>())
        {
            var style = element.GetAttribute("style");
            if (string.IsNullOrWhiteSpace(style)) continue;

            var declarations = style.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var decl in declarations)
            {
                var colon = decl.IndexOf(':');
                if (colon < 0) continue;
                var prop = decl[..colon].Trim().ToLowerInvariant();
                Assert.True(
                    prop is "color" or "background-color" or "white-space",
                    $"Disallowed CSS property '{prop}' in style '{style}'");
            }
        }
    }
}
