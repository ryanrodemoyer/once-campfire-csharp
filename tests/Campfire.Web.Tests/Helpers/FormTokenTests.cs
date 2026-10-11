using Campfire.RailsCompat.Csrf;
using Campfire.Web.Helpers;
using Campfire.Web.Routing;
using Campfire.Web.Tests.Assets;

namespace Campfire.Web.Tests.Helpers;

// Header-only forgery protection needs no secret in forms: the reference overrides token_tag to
// return "" (reference/app/helpers/application_helper.rb), so forms carry no token even when a
// session token is available.
public sealed class FormTokenTests
{
    static readonly string SessionToken = AuthenticityToken.GenerateSessionToken();

    [Fact]
    public void A_button_to_carries_no_token()
    {
        var html = NewView().ButtonTo("Delete", "/rooms/1", new() { { "method", "delete" } }).Value;

        Assert.DoesNotContain("authenticity_token", html, StringComparison.Ordinal);
        Assert.Contains("name=\"_method\" value=\"delete\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_form_with_carries_no_token()
    {
        var view = NewView();
        var form = HelperGoldenTests.Render(w => w.Append(view.FormWith(null, "/rooms/7/messages", null, _ => { })));

        Assert.DoesNotContain("authenticity_token", form, StringComparison.Ordinal);
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
}
