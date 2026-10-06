using Campfire.RailsCompat.Csrf;

namespace Campfire.Web.Pipeline;

// ActionController::RequestForgeryProtection, as ActionController::Base has it
// (`protect_from_forgery with: :exception`, per-form tokens, the origin check).
public abstract partial class Controller
{
    /// <summary>
    /// <c>ActionController::Base</c>'s own callbacks: <c>verify_authenticity_token</c> before every
    /// action and <c>verify_same_origin_request</c> after it (<c>default_protect_from_forgery</c>).
    /// </summary>
    public static ControllerCallbacks<Controller> BaseCallbacks { get; } = ControllerCallbacks.Empty<Controller>()
        .Before("verify_authenticity_token", controller => controller.VerifyAuthenticityToken())
        .After("verify_same_origin_request", controller => controller.VerifySameOriginRequest());

    string? csrfToken;
    bool markedForSameOriginVerification;

    /// <summary>
    /// <c>real_csrf_token</c>: the session's <c>_csrf_token</c>, or a new one, which is stored in
    /// the session when the response is sent (<c>commit_csrf_token</c>).
    /// </summary>
    public string RealCsrfToken => csrfToken ??= Session["_csrf_token"] ?? AuthenticityToken.GenerateSessionToken();

    /// <summary>
    /// <c>form_authenticity_token(form_options: { action:, method: })</c>: a masked token for that
    /// form, or the global one when both are null (the <c>csrf_meta_tags</c> token).
    /// </summary>
    public string FormAuthenticityToken(string? action = null, string? method = null) =>
        AuthenticityToken.FormToken(RealCsrfToken, action, method, Request.OriginalPath);

    /// <summary><c>reset_csrf_token</c> (from <c>reset_session</c>).</summary>
    public void ResetCsrfToken()
    {
        csrfToken = null;
        Session.Remove("_csrf_token");
    }

    /// <summary>
    /// <c>verify_authenticity_token</c> with the <c>:exception</c> strategy: a write must carry a
    /// token for this session (<c>authenticity_token</c> or <c>X-CSRF-Token</c>) and no foreign
    /// <c>Origin</c>, else <see cref="ForgeryProtectionException"/> (422).
    /// </summary>
    public void VerifyAuthenticityToken()
    {
        markedForSameOriginVerification = Request.Method == "GET";
        if (Request.Method is "GET" or "HEAD")
        {
            return;
        }
        var result = ForgeryProtection.Verify(new ForgeryRequest
        {
            Method = Request.Method,
            Path = Request.OriginalPath,
            BaseUrl = RequestUrl.BaseUrl,
            Origin = Header("Origin"),
            AuthenticityToken = Params["authenticity_token"] as string,
            CsrfTokenHeader = Header(AuthenticityToken.HeaderName),
            SessionToken = ForgeryOriginAccepted() ? RealCsrfToken : null,
        });
        if (!result.Verified)
        {
            throw new ForgeryProtectionException(result.Message ?? ForgeryProtection.TokenFailureMessage);
        }
    }

    /// <summary>
    /// <c>verify_same_origin_request</c>: a GET whose token check ran must not answer a non-XHR
    /// request with JavaScript (another site's <c>&lt;script&gt;</c> tag); 422 otherwise.
    /// </summary>
    public void VerifySameOriginRequest()
    {
        try
        {
            ForgeryProtection.VerifySameOriginRequest(markedForSameOriginVerification, ContentType, Request.IsXhr);
        }
        catch (InvalidCrossOriginRequestException error)
        {
            throw new ForgeryProtectionException(error.Message, error);
        }
    }

    // valid_request_origin? runs before the token is read, so a rejected origin never reads (or
    // makes) the session's token.
    bool ForgeryOriginAccepted() => ForgeryProtection.CheckOrigin(Header("Origin"), RequestUrl.BaseUrl) == OriginVerdict.Accepted;
}
