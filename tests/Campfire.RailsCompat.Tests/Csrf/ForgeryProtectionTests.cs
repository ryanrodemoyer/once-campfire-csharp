using Campfire.RailsCompat.Csrf;
using Campfire.Vectors;

namespace Campfire.RailsCompat.Tests.Csrf;

public class ForgeryProtectionTests
{
    const string baseUrl = "http://campfire.test";
    static readonly string SessionToken = RailsCompatVectors.File.Csrf.SessionToken;

    [Fact]
    public void Get_and_head_skip_the_token_and_only_get_is_marked()
    {
        var get = Verify("GET", "/rooms/1");
        Assert.True(get.Verified);
        Assert.True(get.MarkedForSameOriginVerification);
        Assert.Null(get.GeneratedSessionToken);

        var head = Verify("HEAD", "/rooms/1");
        Assert.True(head.Verified);
        Assert.False(head.MarkedForSameOriginVerification);
    }

    [Fact]
    public void Bot_key_exemption_skips_token_and_origin()
    {
        var result = ForgeryProtection.Verify(new ForgeryRequest
        {
            Method = "POST",
            Path = "/rooms/1/messages",
            BaseUrl = baseUrl,
            Origin = "http://evil.test",
            Exempt = true,
        });

        Assert.True(result.Verified);
        Assert.False(result.MarkedForSameOriginVerification);
        Assert.Null(result.GeneratedSessionToken);
    }

    [Fact]
    public void Either_the_parameter_or_the_header_is_enough()
    {
        var token = AuthenticityToken.FormToken(SessionToken, null, null, "/");
        var byHeader = Verify("POST", "/session", csrfHeader: token, authenticityToken: "bad");
        var byParam = Verify("POST", "/session", authenticityToken: token, csrfHeader: "bad");

        Assert.True(byHeader.Verified);
        Assert.True(byParam.Verified);
    }

    [Fact]
    public void Per_form_token_is_path_and_method_specific()
    {
        var token = AuthenticityToken.FormToken(SessionToken, "/rooms/1/messages", "post", "/rooms/1");

        Assert.True(AuthenticityToken.IsValid(SessionToken, token, "/rooms/1/messages", "POST"));
        Assert.True(AuthenticityToken.IsValid(SessionToken, token, "/rooms/1/messages/", "POST"));
        Assert.False(AuthenticityToken.IsValid(SessionToken, token, "/rooms/1/messages", "PATCH"));
        Assert.False(AuthenticityToken.IsValid(SessionToken, token, "/rooms/1", "POST"));
        Assert.False(AuthenticityToken.IsValid(SessionToken, token, "/rooms/1/messages", "POST", perFormTokens: false));
    }

    [Fact]
    public void Null_origin_raises_and_a_missing_origin_does_not()
    {
        var nullOrigin = Verify("POST", "/session", origin: "null", authenticityToken: Meta());
        Assert.False(nullOrigin.Verified);
        Assert.Equal(422, nullOrigin.Status);
        Assert.Equal(ForgeryProtection.NullOriginMessage, nullOrigin.Message);
        Assert.EndsWith("\n", nullOrigin.Message);

        var missing = Verify("POST", "/session", authenticityToken: Meta());
        Assert.True(missing.Verified);
    }

    [Fact]
    public void Origin_check_can_be_turned_off_the_way_the_rails_setting_does()
    {
        var result = ForgeryProtection.Verify(new ForgeryRequest
        {
            Method = "POST",
            Path = "/session",
            BaseUrl = baseUrl,
            Origin = "null",
            AuthenticityToken = Meta(),
            SessionToken = SessionToken,
            OriginCheck = false,
        });

        Assert.True(result.Verified);
    }

    [Fact]
    public void Protection_off_accepts_a_write_with_no_token()
    {
        var result = ForgeryProtection.Verify(new ForgeryRequest
        {
            Method = "POST",
            Path = "/session",
            BaseUrl = baseUrl,
            AllowForgeryProtection = false,
        });

        Assert.True(result.Verified);
        Assert.Null(result.GeneratedSessionToken);
    }

    [Fact]
    public void A_new_session_token_is_minted_when_the_check_has_to_read_one()
    {
        var result = ForgeryProtection.Verify(new ForgeryRequest
        {
            Method = "POST",
            Path = "/session",
            BaseUrl = baseUrl,
            AuthenticityToken = "bad",
        });

        Assert.False(result.Verified);
        Assert.NotNull(result.GeneratedSessionToken);
        Assert.Equal(43, result.GeneratedSessionToken!.Length);
        Assert.Equal(32, AuthenticityToken.RealBytes(result.GeneratedSessionToken).Length);
    }

    [Fact]
    public void Generated_session_tokens_have_rails_shape()
    {
        var minted = AuthenticityToken.GenerateSessionToken();
        var sample = RailsCompatVectors.File.Csrf.GeneratedSessionTokenExample;

        Assert.Equal(sample.Length, minted.Length);
        Assert.Equal(32, AuthenticityToken.RealBytes(sample).Length);
        Assert.Equal(32, AuthenticityToken.RealBytes(minted).Length);
    }

    [Fact]
    public void Non_xhr_javascript_gets_are_rejected()
    {
        var get = Verify("GET", "/rooms/1.js");
        var rejected = Assert.Throws<InvalidCrossOriginRequestException>(() =>
            ForgeryProtection.VerifySameOriginRequest(get.MarkedForSameOriginVerification, "text/javascript; charset=utf-8", xhr: false));

        Assert.Equal(ForgeryProtection.CrossOriginJavaScriptMessage, rejected.Message);

        ForgeryProtection.VerifySameOriginRequest(get.MarkedForSameOriginVerification, "text/javascript", xhr: true);
        ForgeryProtection.VerifySameOriginRequest(get.MarkedForSameOriginVerification, "text/html", xhr: false);
        ForgeryProtection.VerifySameOriginRequest(get.MarkedForSameOriginVerification, "application/javascript", xhr: true);

        var post = Verify("POST", "/session", authenticityToken: Meta());
        ForgeryProtection.VerifySameOriginRequest(post.MarkedForSameOriginVerification, "application/javascript", xhr: false);
    }

    static string Meta() => AuthenticityToken.FormToken(SessionToken, action: null, method: null, requestPath: "/");

    static ForgeryResult Verify(string method, string path, string? authenticityToken = null, string? csrfHeader = null, string? origin = null) =>
        ForgeryProtection.Verify(new ForgeryRequest
        {
            Method = method,
            Path = path,
            BaseUrl = baseUrl,
            Origin = origin,
            AuthenticityToken = authenticityToken,
            CsrfTokenHeader = csrfHeader,
            SessionToken = SessionToken,
        });
}
