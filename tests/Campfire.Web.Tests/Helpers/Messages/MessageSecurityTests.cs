using System.Text.RegularExpressions;
using Campfire.RichText.Html;
using Campfire.Web.Helpers;
using Campfire.Web.Routing;
using Campfire.Web.Tests.Assets;

namespace Campfire.Web.Tests.Helpers.Messages;

// Independent of the reference: what a rendered message must never contain, whatever its author,
// body, attachment or boosts hold.
public sealed partial class MessageSecurityTests
{
    public static TheoryData<long> MessageIds() => MessageRenderingTests.MessageIds();

    [Theory]
    [MemberData(nameof(MessageIds))]
    public void No_seed_message_renders_script_event_handlers_or_script_urls(long id) =>
        AssertInert(SeedMessages.Output.MessageHtml[id]);

    [Fact]
    public void Names_boost_contents_and_filenames_are_escaped()
    {
        const string hostile = "<script>alert(1)</script>\"' onmouseover=alert(1) <img src=x onerror=alert(1)>";
        var original = SeedMessages.Output.Messages.First(message => message.Boosts.Count > 0 && message.Creator is not null);
        var user = original.Creator! with { Name = hostile, Title = hostile };
        var message = original with
        {
            Creator = user,
            Room = original.Room with { DisplayName = hostile },
            Boosts = [.. original.Boosts.Select(boost => boost with { Content = hostile, Booster = user })],
        };
        var view = new View { Assets = ReferenceAssets.Bundle, Origin = new UrlBase("http", SeedMessages.Host) };

        var html = HelperGoldenTests.Render(w => view.MessagesBoostsBoosts(w, message)) +
            HelperGoldenTests.Render(w => view.MessagesTemplate(w, user));

        AssertInert(html.Replace("<script type=\"text/template\" data-messages-target=\"template\">", "", StringComparison.Ordinal));
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
    }

    // Parsed as a browser would, so text inside attribute values can't pass for markup.
    static void AssertInert(string html)
    {
        foreach (var element in HtmlParser.ParseFragment(html).Descendants().OfType<HtmlElement>())
        {
            Assert.DoesNotContain(element.Name, ScriptCapableElements);
            Assert.Equal(HtmlNamespace.Html, element.Namespace);
            foreach (var attribute in element.Attributes)
            {
                Assert.False(attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase), $"<{element.Name} {attribute.Name}>");
                if (UrlAttributes.Contains(attribute.Name))
                {
                    Assert.DoesNotMatch(ScriptUrl(), attribute.Value);
                }
            }
        }
    }

    static readonly string[] ScriptCapableElements = ["script", "iframe", "object", "embed", "frame", "frameset", "base"];

    static readonly string[] UrlAttributes = ["href", "src", "action", "poster", "formaction", "data-lightbox-url-value", "data-web-share-files-value", "data-sound-url-value"];

    [GeneratedRegex(@"^\s*(javascript|vbscript|data):", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptUrl();
}
