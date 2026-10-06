using Campfire.RailsCompat.Csrf;

namespace Campfire.Web.Helpers;

// ActionView::Helpers::CsrfHelper and the token fields forms carry.
// reference: actionview/lib/action_view/helpers/csrf_helper.rb, url_helper.rb (token_tag, method_tag)
public partial class View
{
    /// <summary><c>protect_against_forgery?</c>.</summary>
    public bool ProtectAgainstForgery => FormAuthenticityToken is not null;

    /// <summary><c>csrf_meta_tags</c>: the param name and the global masked token.</summary>
    public SafeString CsrfMetaTags()
    {
        if (FormAuthenticityToken is null)
        {
            return SafeString.Empty;
        }

        var param = TagHelper.Tag("meta", new() { { "name", "csrf-param" }, { "content", AuthenticityToken.ParamName } });
        var token = TagHelper.Tag("meta", new() { { "name", "csrf-token" }, { "content", FormAuthenticityToken(null, null) } });
        return new SafeString(param.Value + "\n" + token.Value);
    }

    /// <summary><c>csp_meta_tag</c>: nothing, since the reference configures no content security policy.</summary>
    public static SafeString CspMetaTag() => SafeString.Empty;

    /// <summary>
    /// <c>token_tag(token, form_options: { action:, method: })</c>: the hidden authenticity token
    /// field. <paramref name="token"/> is the <c>authenticity_token</c> option: null or true for a
    /// generated token, false for none, or a string to use as is.
    /// </summary>
    public SafeString TokenTag(object? token, string? action, string? method)
    {
        if (token is false || FormAuthenticityToken is null)
        {
            return SafeString.Empty;
        }

        var value = token is null or true ? FormAuthenticityToken(action, method) : token;
        return TagHelper.Tag("input", new() { { "type", "hidden" }, { "name", AuthenticityToken.ParamName }, { "value", value } });
    }

    /// <summary><c>method_tag(method)</c>: the hidden <c>_method</c> field.</summary>
    public static SafeString MethodTag(string method) =>
        TagHelper.Tag("input", new() { { "type", "hidden" }, { "name", "_method" }, { "value", method } });
}

/// <summary>Builds <see cref="View.FormAuthenticityToken"/> from the session's CSRF token.</summary>
public static class CsrfTokens
{
    /// <summary>
    /// <c>form_authenticity_token</c> for a request: per-form tokens (on in the reference through
    /// <c>load_defaults</c>) for a form with an action and method, the global token otherwise,
    /// each masked with a fresh pad.
    /// </summary>
    public static Func<string?, string?, string> For(string sessionToken, string requestPath) =>
        (action, method) => AuthenticityToken.FormToken(sessionToken, action, method, requestPath);
}
