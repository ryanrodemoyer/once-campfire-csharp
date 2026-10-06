using System.Security.Cryptography;
using System.Text;
using Campfire.RailsCompat.Crypto;

namespace Campfire.RailsCompat.Csrf;

/// <summary>
/// Masked CSRF tokens from <c>ActionController::RequestForgeryProtection</c>
/// (rails@1a02651, the revision in <c>reference/Gemfile.lock</c>).
/// Campfire turns on per-form tokens via <c>config.load_defaults 8.2</c>
/// (<c>reference/config/application.rb</c>).
/// </summary>
public static class AuthenticityToken
{
    /// <summary><c>AUTHENTICITY_TOKEN_LENGTH</c>.</summary>
    public const int TokenLength = 32;

    /// <summary>The session key <c>SessionStore</c> uses. <c>Session.CsrfToken</c> stores it.</summary>
    public const string SessionKey = "_csrf_token";

    /// <summary>The default <c>request_forgery_protection_token</c>.</summary>
    public const string ParamName = "authenticity_token";

    /// <summary><c>ActionDispatch::Request#x_csrf_token</c>, the <c>X-CSRF-Token</c> header.</summary>
    public const string HeaderName = "X-CSRF-Token";

    static ReadOnlySpan<byte> GlobalIdentifier => "!real_csrf_token"u8;

    /// <summary><c>generate_csrf_token</c>: <c>SecureRandom.urlsafe_base64(32)</c>.</summary>
    public static string GenerateSessionToken() =>
        RubyBase64.UrlSafeEncode(RandomNumberGenerator.GetBytes(TokenLength), padding: false);

    /// <summary>
    /// <c>form_authenticity_token</c>. A null <paramref name="action"/> or <paramref name="method"/>
    /// is the meta-tag token (the masked global token). An empty action is still per-form: Ruby
    /// treats <c>""</c> as present, and <c>normalize_action_path</c> resolves it against
    /// <paramref name="requestPath"/>.
    /// </summary>
    public static string FormToken(string sessionToken, string? action, string? method, string requestPath, bool perFormTokens = true)
    {
        var raw = perFormTokens && action is not null && method is not null
            ? PerFormBytes(sessionToken, NormalizeActionPath(action, requestPath), method)
            : GlobalBytes(sessionToken);
        return Mask(raw);
    }

    /// <summary><c>normalize_action_path</c>, including the relative-path join Rails uses for <c>./</c> and blank actions.</summary>
    public static string NormalizeActionPath(string actionPath, string requestPath)
    {
        // URI.parse then: relative and (blank or not starting with "/") → join onto request.path.
        if (!HasScheme(actionPath) && (IsBlank(actionPath) || !actionPath.StartsWith('/')))
        {
            var relative = IsBlank(actionPath) ? "" : PathOf(actionPath);
            var joined = string.Concat(requestPath, "/", relative).Replace("/./", "/", StringComparison.Ordinal);
            return ChompSlash(joined);
        }

        return ChompSlash(PathOf(actionPath));
    }

    /// <summary>
    /// <c>valid_authenticity_token?</c>. <paramref name="requestPath"/> is <c>request.path</c>
    /// and <paramref name="requestMethod"/> is <c>request.request_method</c> (the method the app
    /// sees after <c>Rack::MethodOverride</c>, so a <c>_method=patch</c> form is <c>PATCH</c>).
    /// </summary>
    public static bool IsValid(string sessionToken, string? encodedToken, string requestPath, string requestMethod, bool perFormTokens = true)
    {
        if (string.IsNullOrEmpty(encodedToken))
        {
            return false;
        }

        var masked = RubyBase64.UrlSafeDecode(encodedToken);
        if (masked is null)
        {
            return false;
        }

        var real = RealBytes(sessionToken);
        if (masked.Length == TokenLength)
        {
            // An unmasked session token, accepted so old tokens keep working.
            return SecurityUtils.SecureCompare(masked, real);
        }

        if (masked.Length != TokenLength * 2)
        {
            return false;
        }

        var token = Unmask(masked);
        return SecurityUtils.SecureCompare(token, GlobalBytes(real))
            || SecurityUtils.SecureCompare(token, real)
            || (perFormTokens && SecurityUtils.SecureCompare(token, PerFormBytes(real, ChompSlash(requestPath), requestMethod)));
    }

    internal static byte[] RealBytes(string sessionToken)
    {
        var raw = RubyBase64.UrlSafeDecode(sessionToken);
        if (raw is null || raw.Length != TokenLength)
        {
            throw new FormatException("Session CSRF token is not urlsafe Base64 of 32 bytes.");
        }

        return raw;
    }

    internal static byte[] GlobalBytes(string sessionToken) => GlobalBytes(RealBytes(sessionToken));

    internal static string Mask(ReadOnlySpan<byte> raw, ReadOnlySpan<byte> pad)
    {
        if (raw.Length != TokenLength || pad.Length != TokenLength)
        {
            throw new ArgumentException($"CSRF tokens are {TokenLength} bytes.");
        }

        Span<byte> masked = stackalloc byte[TokenLength * 2];
        pad.CopyTo(masked);
        for (var i = 0; i < TokenLength; i++)
        {
            masked[TokenLength + i] = (byte)(pad[i] ^ raw[i]);
        }

        return RubyBase64.UrlSafeEncode(masked, padding: false);
    }

    /// <summary>One-time-pad split of a masked token. Used to show a C# token is the Rails HMAC under a fresh pad.</summary>
    internal static byte[] UnmaskEncoded(string encoded)
    {
        var masked = RubyBase64.UrlSafeDecode(encoded) ?? throw new FormatException("CSRF token is not urlsafe Base64.");
        if (masked.Length != TokenLength * 2)
        {
            throw new FormatException("Masked CSRF token is not 64 bytes.");
        }

        return Unmask(masked);
    }

    static string Mask(ReadOnlySpan<byte> raw)
    {
        Span<byte> pad = stackalloc byte[TokenLength];
        RandomNumberGenerator.Fill(pad);
        return Mask(raw, pad);
    }

    static byte[] GlobalBytes(byte[] real) => HMACSHA256.HashData(real, GlobalIdentifier);

    static byte[] PerFormBytes(string sessionToken, string actionPath, string method) =>
        PerFormBytes(RealBytes(sessionToken), actionPath, method);

    static byte[] PerFormBytes(byte[] real, string actionPath, string method) =>
        HMACSHA256.HashData(real, Encoding.UTF8.GetBytes($"{actionPath}#{method.ToLowerInvariant()}"));

    static byte[] Unmask(byte[] masked)
    {
        var token = new byte[TokenLength];
        for (var i = 0; i < TokenLength; i++)
        {
            token[i] = (byte)(masked[i] ^ masked[TokenLength + i]);
        }

        return token;
    }

    /// <summary><c>String#chomp("/")</c>: one trailing slash, so <c>"/"</c> becomes <c>""</c>.</summary>
    internal static string ChompSlash(string path) => path.EndsWith('/') ? path[..^1] : path;

    static string PathOf(string action)
    {
        if (HasScheme(action))
        {
            var rest = action[(action.IndexOf("://", StringComparison.Ordinal) + 3)..];
            var slash = rest.IndexOf('/');
            action = slash < 0 ? "" : rest[slash..];
        }

        var cut = action.IndexOfAny(['?', '#']);
        return cut < 0 ? action : action[..cut];
    }

    static bool HasScheme(string action)
    {
        var scheme = action.IndexOf("://", StringComparison.Ordinal);
        if (scheme <= 0 || !char.IsAsciiLetter(action[0]))
        {
            return false;
        }

        for (var i = 1; i < scheme; i++)
        {
            var c = action[i];
            if (!char.IsAsciiLetterOrDigit(c) && c is not '+' and not '-' and not '.')
            {
                return false;
            }
        }

        return true;
    }

    static bool IsBlank(string value) => string.IsNullOrWhiteSpace(value);
}
