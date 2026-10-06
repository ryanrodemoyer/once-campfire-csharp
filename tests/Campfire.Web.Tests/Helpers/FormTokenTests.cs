using System.Text.RegularExpressions;
using Campfire.RailsCompat.Csrf;
using Campfire.Web.Helpers;
using Campfire.Web.Routing;
using Campfire.Web.Tests.Assets;

namespace Campfire.Web.Tests.Helpers;

// Forms carry tokens C03's verifier accepts for exactly the action and method they submit.
public sealed partial class FormTokenTests
{
    static readonly string SessionToken = AuthenticityToken.GenerateSessionToken();

    [Fact]
    public void A_button_to_token_is_valid_only_for_its_action_and_method()
    {
        var view = NewView();
        var token = Token(view.ButtonTo("Delete", "/rooms/1", new() { { "method", "delete" } }).Value);

        Assert.True(AuthenticityToken.IsValid(SessionToken, token, "/rooms/1", "DELETE"));
        Assert.False(AuthenticityToken.IsValid(SessionToken, token, "/rooms/2", "DELETE"));
        Assert.False(AuthenticityToken.IsValid(SessionToken, token, "/rooms/1", "POST"));
    }

    [Fact]
    public void A_form_with_token_is_valid_for_its_action_and_the_meta_token_everywhere()
    {
        var view = NewView();
        var form = HelperGoldenTests.Render(w => w.Append(view.FormWith(null, "/rooms/7/messages", null, _ => { })));
        var formToken = Token(form);
        var metaToken = MetaToken().Match(view.CsrfMetaTags().Value).Groups[1].Value;

        Assert.True(AuthenticityToken.IsValid(SessionToken, formToken, "/rooms/7/messages", "POST"));
        Assert.False(AuthenticityToken.IsValid(SessionToken, formToken, "/session", "POST"));
        Assert.True(AuthenticityToken.IsValid(SessionToken, metaToken, "/session", "DELETE"));
    }

    [Fact]
    public void Without_forgery_protection_there_are_no_tokens()
    {
        var unprotected = new View { Assets = ReferenceAssets.Bundle, Origin = new UrlBase("https", "campfire.test") };

        Assert.Equal("", unprotected.CsrfMetaTags().Value);
        Assert.DoesNotContain("authenticity_token", unprotected.ButtonTo("Go", "/go").Value, StringComparison.Ordinal);
    }

    static View NewView() => new()
    {
        Assets = ReferenceAssets.Bundle,
        Origin = new UrlBase("https", "campfire.test"),
        RequestPath = "/rooms/7",
        FormAuthenticityToken = CsrfTokens.For(SessionToken, "/rooms/7"),
    };

    static string Token(string html) => FieldToken().Match(html).Groups[1].Value;

    [GeneratedRegex("name=\"authenticity_token\" value=\"([^\"]+)\"")]
    private static partial Regex FieldToken();

    [GeneratedRegex("name=\"csrf-token\" content=\"([^\"]+)\"")]
    private static partial Regex MetaToken();
}
