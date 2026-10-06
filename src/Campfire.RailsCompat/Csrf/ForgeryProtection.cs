namespace Campfire.RailsCompat.Csrf;

/// <summary>
/// <c>verify_authenticity_token</c> as Campfire configures it. Authentication calls
/// <c>protect_from_forgery with: :exception, unless: -&gt; { authenticated_by.bot_key? }</c>
/// (<c>reference/app/controllers/concerns/authentication.rb</c>). The pinned Rails checks the
/// <c>Origin</c> header; it does not read <c>Sec-Fetch-Site</c>.
/// </summary>
public static class ForgeryProtection
{
    /// <summary>
    /// <c>ActionDispatch::ExceptionWrapper</c> maps <c>InvalidAuthenticityToken</c> and
    /// <c>InvalidCrossOriginRequest</c> to <c>:unprocessable_content</c>, which is 422.
    /// </summary>
    public const int UnverifiedStatus = 422;

    public const string TokenFailureMessage = "Can't verify CSRF token authenticity.";

    /// <summary><c>NULL_ORIGIN_MESSAGE</c>, including the trailing newline of the squiggly heredoc.</summary>
    public const string NullOriginMessage =
        """
        The browser returned a 'null' origin for a request with origin-based forgery protection turned on. This usually
        means you have the 'no-referrer' Referrer-Policy header enabled, or that the request came from a site that
        refused to give its origin. This makes it impossible for Rails to verify the source of the requests. Likely the
        best solution is to change your referrer policy to something less strict like same-origin or strict-origin.
        If you cannot change the referrer policy, you can disable origin checking with the
        Rails.application.config.action_controller.forgery_protection_origin_check setting.
        """ + "\n";

    public const string CrossOriginJavaScriptMessage =
        "Security warning: an embedded <script> tag on another site requested protected JavaScript. " +
        "If you know what you're doing, go ahead and disable forgery protection on this action to permit cross-origin JavaScript embedding.";

    /// <summary>
    /// The before-action. <see cref="ForgeryRequest.Exempt"/> is the bot-key <c>unless:</c> hook and
    /// <c>skip_forgery_protection</c> (<c>reference/app/controllers/pwa_controller.rb</c>): the
    /// callback does not run, so neither the token nor the origin is checked.
    /// </summary>
    public static ForgeryResult Verify(ForgeryRequest request)
    {
        if (request.Exempt)
        {
            return ForgeryResult.Accepted(markedForSameOriginVerification: false, generatedSessionToken: null);
        }

        // mark_for_same_origin_verification! runs before the token check, and only flags GETs.
        var marked = IsGet(request.Method);
        if (IsGet(request.Method) || IsHead(request.Method) || !request.AllowForgeryProtection)
        {
            return ForgeryResult.Accepted(marked, generatedSessionToken: null);
        }

        var origin = CheckOrigin(request.Origin, request.BaseUrl, request.OriginCheck);
        if (origin == OriginVerdict.NullOrigin)
        {
            return ForgeryResult.Rejected(UnverifiedStatus, NullOriginMessage, marked, generatedSessionToken: null);
        }

        if (origin == OriginVerdict.Mismatch)
        {
            return ForgeryResult.Rejected(UnverifiedStatus, OriginMismatch(request.Origin, request.BaseUrl), marked, generatedSessionToken: null);
        }

        var sessionToken = request.SessionToken;
        string? generated = null;
        if (sessionToken is null)
        {
            sessionToken = AuthenticityToken.GenerateSessionToken();
            generated = sessionToken;
        }

        var valid = AuthenticityToken.IsValid(sessionToken, request.AuthenticityToken, request.Path, request.Method, request.PerFormTokens)
            || AuthenticityToken.IsValid(sessionToken, request.CsrfTokenHeader, request.Path, request.Method, request.PerFormTokens);
        return valid
            ? ForgeryResult.Accepted(marked, generated)
            : ForgeryResult.Rejected(UnverifiedStatus, TokenFailureMessage, marked, generated);
    }

    /// <summary>
    /// <c>valid_request_origin?</c>. A <c>"null"</c> origin raises when the check is on; a missing
    /// origin is accepted, because some user agents never send the header.
    /// </summary>
    public static OriginVerdict CheckOrigin(string? origin, string baseUrl, bool originCheck = true)
    {
        if (!originCheck)
        {
            return OriginVerdict.Accepted;
        }

        if (origin == "null")
        {
            return OriginVerdict.NullOrigin;
        }

        return origin is null || origin == baseUrl ? OriginVerdict.Accepted : OriginVerdict.Mismatch;
    }

    /// <summary>
    /// The <c>verify_same_origin_request</c> after-action. <paramref name="contentType"/> is the
    /// response's content type. A non-XHR JavaScript response to a GET that ran the before-action
    /// raises <see cref="InvalidCrossOriginRequestException"/>.
    /// </summary>
    public static void VerifySameOriginRequest(bool markedForSameOriginVerification, string? contentType, bool xhr)
    {
        if (markedForSameOriginVerification && IsJavaScript(contentType) && !xhr)
        {
            throw new InvalidCrossOriginRequestException(CrossOriginJavaScriptMessage);
        }
    }

    public static string OriginMismatch(string? origin, string baseUrl) =>
        $"HTTP Origin header ({origin}) didn't match request.base_url ({baseUrl})";

    static bool IsGet(string method) => method.Equals("GET", StringComparison.Ordinal);

    static bool IsHead(string method) => method.Equals("HEAD", StringComparison.Ordinal);

    static bool IsJavaScript(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return false;
        }

        var semi = contentType.IndexOf(';');
        var media = (semi < 0 ? contentType : contentType[..semi]).Trim();
        return media.StartsWith("text/javascript", StringComparison.Ordinal)
            || media.StartsWith("application/javascript", StringComparison.Ordinal);
    }
}

/// <summary>What <c>valid_request_origin?</c> decides. <see cref="NullOrigin"/> is the raise.</summary>
public enum OriginVerdict
{
    Accepted,
    Mismatch,
    NullOrigin,
}

/// <summary>One non-GET write, as the forgery before-action sees it.</summary>
public sealed class ForgeryRequest
{
    /// <summary><c>request.request_method</c>, after method override. Uppercase, as Rack sets it.</summary>
    public required string Method { get; init; }

    /// <summary><c>request.path</c>, without the query.</summary>
    public required string Path { get; init; }

    /// <summary><c>request.base_url</c>, for example <c>http://campfire.test</c>.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>The <c>Origin</c> header, or null when the client sent none.</summary>
    public string? Origin { get; init; }

    /// <summary>The <c>authenticity_token</c> parameter.</summary>
    public string? AuthenticityToken { get; init; }

    /// <summary>The <c>X-CSRF-Token</c> header. Either this or the parameter may satisfy the check.</summary>
    public string? CsrfTokenHeader { get; init; }

    /// <summary>The session's <c>_csrf_token</c>, or null when the session has none yet.</summary>
    public string? SessionToken { get; init; }

    /// <summary><c>allow_forgery_protection</c>. Off in the Rails test environment; on everywhere else.</summary>
    public bool AllowForgeryProtection { get; init; } = true;

    /// <summary>
    /// Skip the before-action. Set this when <c>authenticated_by.bot_key?</c> or the controller
    /// called <c>skip_forgery_protection</c>.
    /// </summary>
    public bool Exempt { get; init; }

    /// <summary><c>per_form_csrf_tokens</c>. On under <c>load_defaults 8.2</c>.</summary>
    public bool PerFormTokens { get; init; } = true;

    /// <summary><c>forgery_protection_origin_check</c>. On under <c>load_defaults 8.2</c>.</summary>
    public bool OriginCheck { get; init; } = true;
}

/// <summary>The before-action's decision. Call <see cref="ThrowIfRejected"/> for the <c>:exception</c> strategy.</summary>
public sealed class ForgeryResult
{
    ForgeryResult(bool verified, int? status, string? message, bool marked, string? generatedSessionToken)
    {
        Verified = verified;
        Status = status;
        Message = message;
        MarkedForSameOriginVerification = marked;
        GeneratedSessionToken = generatedSessionToken;
    }

    public bool Verified { get; }

    /// <summary>422 when the request is rejected. Null when it is verified.</summary>
    public int? Status { get; }

    /// <summary>The <c>InvalidAuthenticityToken</c> message Rails would raise. Null when verified.</summary>
    public string? Message { get; }

    /// <summary><c>marked_for_same_origin_verification?</c>, true only for a GET whose before-action ran.</summary>
    public bool MarkedForSameOriginVerification { get; }

    /// <summary>
    /// A session token minted while checking, which Rails stores via <c>commit_csrf_token</c>
    /// even when the check then fails. Null when the session already had one, or the check
    /// never read it.
    /// </summary>
    public string? GeneratedSessionToken { get; }

    public void ThrowIfRejected()
    {
        if (!Verified)
        {
            throw new InvalidAuthenticityTokenException(Message ?? ForgeryProtection.TokenFailureMessage);
        }
    }

    internal static ForgeryResult Accepted(bool markedForSameOriginVerification, string? generatedSessionToken) =>
        new(true, null, null, markedForSameOriginVerification, generatedSessionToken);

    internal static ForgeryResult Rejected(int status, string message, bool marked, string? generatedSessionToken) =>
        new(false, status, message, marked, generatedSessionToken);
}
