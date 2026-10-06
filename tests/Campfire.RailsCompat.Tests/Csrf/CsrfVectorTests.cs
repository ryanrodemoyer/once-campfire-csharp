using Campfire.RailsCompat.Csrf;
using Campfire.Vectors;

namespace Campfire.RailsCompat.Tests.Csrf;

/// <summary>
/// <c>vectors/rails_compat.json</c> <c>csrf</c> and the session round trip: tokens Rails minted
/// validate here, and tokens we mint unmask to the HMAC Rails recorded, so Rails accepts them.
/// </summary>
public class CsrfVectorTests
{
    static readonly CsrfVectors Csrf = RailsCompatVectors.File.Csrf;
    static readonly SessionVector Session = RailsCompatVectors.File.Session;

    [Fact]
    public void Campfire_defaults_match_the_reference_controller()
    {
        var request = new ForgeryRequest { Method = "GET", Path = "/", BaseUrl = "http://campfire.test" };

        Assert.True(Csrf.PerFormCsrfTokens);
        Assert.True(Csrf.ForgeryProtectionOriginCheck);
        Assert.Equal(Csrf.PerFormCsrfTokens, request.PerFormTokens);
        Assert.Equal(Csrf.ForgeryProtectionOriginCheck, request.OriginCheck);
    }

    [Fact]
    public void Global_tokens_unmask_to_the_rails_hmac()
    {
        foreach (var token in Csrf.GlobalTokens)
        {
            Assert.Equal(Csrf.GlobalTokenHex, Convert.ToHexStringLower(AuthenticityToken.UnmaskEncoded(token)));
        }

        Assert.Equal(Csrf.GlobalTokenHex, Convert.ToHexStringLower(AuthenticityToken.GlobalBytes(Csrf.SessionToken)));
    }

    [Fact]
    public void Mask_with_rails_pad_is_the_rails_token()
    {
        var pad = new byte[AuthenticityToken.TokenLength];
        Array.Fill(pad, (byte)0x01);
        var masked = AuthenticityToken.Mask(AuthenticityToken.RealBytes(Csrf.SessionToken), pad);
        var expected = Csrf.Validity.First(c => c.Case == "session token masked with a pad").Token;

        Assert.Equal(expected, masked);
    }

    [Theory]
    [MemberData(nameof(RailsCompatVectors.CsrfFormTokens), MemberType = typeof(RailsCompatVectors))]
    public void Per_form_tokens_match_rails_path_and_hmac(CsrfFormToken form)
    {
        Assert.Equal(form.NormalizedActionPath, AuthenticityToken.NormalizeActionPath(form.Action, form.PagePath));
        Assert.Equal(form.UnmaskedHex, Convert.ToHexStringLower(AuthenticityToken.UnmaskEncoded(form.Token)));

        // A fresh mask of the same HMAC is what C# renders. Rails accepts every mask of those bytes.
        var minted = AuthenticityToken.FormToken(Csrf.SessionToken, form.Action, form.Method, form.PagePath);
        Assert.Equal(form.UnmaskedHex, Convert.ToHexStringLower(AuthenticityToken.UnmaskEncoded(minted)));
    }

    [Theory]
    [MemberData(nameof(RailsCompatVectors.CsrfValidity), MemberType = typeof(RailsCompatVectors))]
    public void Token_validity_matches_rails(CsrfValidityCase c) =>
        Assert.Equal(c.Expected, AuthenticityToken.IsValid(Csrf.SessionToken, c.Token, c.Path, c.Method));

    [Theory]
    [MemberData(nameof(RailsCompatVectors.CsrfOrigin), MemberType = typeof(RailsCompatVectors))]
    public void Origin_check_matches_rails(CsrfOriginCase c)
    {
        var verdict = ForgeryProtection.CheckOrigin(c.Origin, c.BaseUrl);

        if (c.Expected.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            Assert.Equal("raises", c.Expected.GetString());
            Assert.Equal(OriginVerdict.NullOrigin, verdict);
            return;
        }

        var accepted = verdict == OriginVerdict.Accepted;
        Assert.Equal(c.Expected.GetBoolean(), accepted);
    }

    [Fact]
    public void Reference_session_form_submits_and_bad_mask_and_cross_origin_are_422()
    {
        var sessionToken = Session.Session["_csrf_token"];

        var form = Write("/session", authenticityToken: Session.SessionFormToken, sessionToken: sessionToken);
        Assert.True(form.Verified);

        var header = Write("/session", csrfHeader: Session.CsrfMetaToken, sessionToken: sessionToken);
        Assert.True(header.Verified);

        var bad = Write("/session", authenticityToken: "bad", sessionToken: sessionToken);
        Assert.False(bad.Verified);
        Assert.Equal(Session.PostWithBadTokenStatus, bad.Status);
        Assert.Equal(422, bad.Status);
        Assert.Equal(ForgeryProtection.TokenFailureMessage, bad.Message);

        var cross = Write("/session", authenticityToken: Session.SessionFormToken, sessionToken: sessionToken, origin: "http://evil.test");
        Assert.False(cross.Verified);
        Assert.Equal(Session.PostWithCrossOriginStatus, cross.Status);
        Assert.Equal(ForgeryProtection.OriginMismatch("http://evil.test", "http://campfire.test"), cross.Message);

        // The token C# would put in a form for this same session is the same per-form HMAC.
        var minted = AuthenticityToken.FormToken(sessionToken, "/session", "post", "/session/new");
        var back = Write("/session", authenticityToken: minted, sessionToken: sessionToken);
        Assert.True(back.Verified);
    }

    [Fact]
    public void Missing_token_is_rejected_with_the_reference_status()
    {
        var missing = Write("/session", sessionToken: Session.Session["_csrf_token"]);

        Assert.False(missing.Verified);
        Assert.Equal(Session.PostWithBadTokenStatus, missing.Status);
        var exception = Assert.Throws<InvalidAuthenticityTokenException>(missing.ThrowIfRejected);
        Assert.Equal(ForgeryProtection.TokenFailureMessage, exception.Message);
    }

    static ForgeryResult Write(string path, string? authenticityToken = null, string? csrfHeader = null, string? sessionToken = null, string? origin = null) =>
        ForgeryProtection.Verify(new ForgeryRequest
        {
            Method = "POST",
            Path = path,
            BaseUrl = "http://campfire.test",
            Origin = origin,
            AuthenticityToken = authenticityToken,
            CsrfTokenHeader = csrfHeader,
            SessionToken = sessionToken,
        });
}
