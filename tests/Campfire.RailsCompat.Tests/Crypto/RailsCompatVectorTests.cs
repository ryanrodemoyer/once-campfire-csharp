using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Campfire.RailsCompat.Crypto;
using Campfire.Vectors;
using static Campfire.RailsCompat.Tests.Crypto.RailsSecrets;

namespace Campfire.RailsCompat.Tests.Crypto;

/// <summary>
/// The crypto in <c>vectors/rails_compat.json</c>, both ways: what Rails generated verifies here
/// (or is refused where Rails refuses it), and what we generate is byte-for-byte what Rails
/// generated, so Rails verifies it.
/// </summary>
public class RailsCompatVectorTests
{
    [Theory]
    [MemberData(nameof(RailsCompatVectors.KeyGenerator), MemberType = typeof(RailsCompatVectors))]
    public void Key_generator_derives_rails_keys(KeyGeneratorCase c) =>
        Assert.Equal(c.KeyHex, Convert.ToHexStringLower(Keys.GenerateKey(c.Salt, c.Length)));

    [Theory]
    [MemberData(nameof(RailsCompatVectors.AppVerifierGenerate), MemberType = typeof(RailsCompatVectors))]
    public void App_verifier_generates_rails_messages(AppVerifierGenerate c)
    {
        var verifier = MessageVerifier.ForApp(Keys, c.Name);
        var message = verifier.GenerateRaw(c.DataJson, c.Purpose, OptionalTime(c.ExpiresAt));

        Assert.Equal(c.Message, message);
        Assert.Equal(c.DataJson, verifier.VerifyRaw(message, c.Purpose, Now).Value);
    }

    [Theory]
    [MemberData(nameof(RailsCompatVectors.AppVerifierVerify), MemberType = typeof(RailsCompatVectors))]
    public void App_verifier_verifies_like_rails(AppVerifierVerify c)
    {
        var result = MessageVerifier.ForApp(Keys, c.Name).VerifyRaw(c.Message, c.Purpose, Time(c.Now));

        Assert.Equal(c.ExpectedJson, result.Value);
        Assert.Equal(c.ExpectedJson is not null, result.IsValid);
    }

    [Theory]
    [MemberData(nameof(RailsCompatVectors.SignedCookieGenerate), MemberType = typeof(RailsCompatVectors))]
    public void Signed_cookie_values_are_rails_bytes(SignedCookieGenerate c)
    {
        var dumped = RailsJson.Encode(JsonValue.Create(c.Value));
        var raw = SignedCookies().GenerateRaw(dumped, $"cookie.{c.Name}", OptionalTime(c.ExpiresAt));

        Assert.Equal(c.Raw, raw);
    }

    [Theory]
    [MemberData(nameof(RailsCompatVectors.SignedCookieVerify), MemberType = typeof(RailsCompatVectors))]
    public void Signed_cookie_values_verify_like_rails(SignedCookieVerify c)
    {
        var verifier = SignedCookies();
        var dumped = ReadCookie(purpose => verifier.Verify(c.Raw, purpose, Time(c.Now)), $"cookie.{c.Name}");

        Assert.Equal(Json(c.Expected), Json(dumped));
    }

    [Theory]
    [MemberData(nameof(RailsCompatVectors.EncryptedCookieGenerate), MemberType = typeof(RailsCompatVectors))]
    public void Encrypted_cookie_values_are_rails_bytes(EncryptedCookieGenerate c)
    {
        var encryptor = EncryptedCookies();
        var purpose = $"cookie.{c.Name}";
        var expiresAt = OptionalTime(c.ExpiresAt);

        // Rails' plaintext decrypts, and ours is the same bytes.
        Assert.Equal(c.Plaintext, Encoding.UTF8.GetString(encryptor.Decrypt(c.Raw)!));
        var dumped = RailsJson.Encode(Node(c.Value));
        var plaintext = MessageMetadata.Serialize(MessageSerializer.Null, JsonValue.Create(dumped), purpose, expiresAt);
        Assert.Equal(c.Plaintext, Encoding.UTF8.GetString(plaintext));

        // With Rails' IV, our ciphertext, IV and auth tag are Rails' too.
        var iv = RubyBase64.StrictDecode(c.Raw.Split("--")[1])!;
        Assert.Equal(c.Raw, encryptor.Encrypt(plaintext, iv));

        // A random IV round-trips.
        var raw = encryptor.EncryptAndSignRaw(dumped, purpose, expiresAt);
        Assert.NotEqual(c.Raw, raw);
        Assert.Equal(dumped, encryptor.DecryptAndVerify(raw, purpose, Now).Value!.GetValue<string>());
    }

    [Theory]
    [MemberData(nameof(RailsCompatVectors.EncryptedCookieVerify), MemberType = typeof(RailsCompatVectors))]
    public void Encrypted_cookie_values_decrypt_like_rails(EncryptedCookieVerify c)
    {
        var encryptor = EncryptedCookies();
        var value = ReadCookie(purpose => encryptor.DecryptAndVerify(c.Raw, purpose, Time(c.Now)), $"cookie.{c.Name}");

        Assert.Equal(Json(c.Expected), Json(value));
    }

    [Fact]
    public void Rails_session_cookie_decrypts()
    {
        var session = RailsCompatVectors.File.Session;
        var encryptor = EncryptedCookies();
        var value = ReadCookie(purpose => encryptor.DecryptAndVerify(session.SessionCookieRaw, purpose, Now), "cookie._campfire_session");

        var expected = new JsonObject(session.Session.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)JsonValue.Create(pair.Value))));
        Assert.True(JsonNode.DeepEquals(expected, value));
    }

    [Theory]
    [MemberData(nameof(RailsCompatVectors.SignedIdGenerate), MemberType = typeof(RailsCompatVectors))]
    public void Signed_ids_are_rails_bytes(SignedIdGenerate c)
    {
        var signedId = SignedIds().Generate(JsonValue.Create(c.Id), SignedIdPurpose(c.Model, c.Purpose), OptionalTime(c.ExpiresAt));

        Assert.Equal(c.SignedId, signedId);
    }

    [Theory]
    [MemberData(nameof(RailsCompatVectors.SignedIdVerify), MemberType = typeof(RailsCompatVectors))]
    public void Signed_ids_verify_like_rails(SignedIdVerify c)
    {
        var result = SignedIds().Verify(c.SignedId, SignedIdPurpose(c.Model, c.Purpose), Time(c.Now));

        Assert.Equal(Json(c.Expected), result.IsValid ? Json(result.Value) : "null");
    }

    [Theory]
    [MemberData(nameof(RailsCompatVectors.TurboStreamNameGenerate), MemberType = typeof(RailsCompatVectors))]
    public void Turbo_stream_names_are_rails_bytes(TurboStreamNameGenerate c) =>
        Assert.Equal(c.SignedName, TurboStreams().Generate(JsonValue.Create(string.Join(':', c.Parts))));

    [Theory]
    [MemberData(nameof(RailsCompatVectors.TurboStreamNameVerify), MemberType = typeof(RailsCompatVectors))]
    public void Turbo_stream_names_verify_like_rails(TurboStreamNameVerify c)
    {
        var result = TurboStreams().Verify(c.SignedName, null, Now);

        Assert.Equal(Json(c.Expected), result.IsValid ? Json(result.Value) : "null");
    }

    [Theory]
    [MemberData(nameof(RailsCompatVectors.SgidGenerate), MemberType = typeof(RailsCompatVectors))]
    public void Signed_global_ids_are_rails_bytes(SgidGenerate c) =>
        Assert.Equal(c.Sgid, SignedGlobalIds().Generate(JsonValue.Create(c.Data), c.Purpose, OptionalTime(c.ExpiresAt)));

    /// <summary>
    /// How the cookie jars read: purpose <c>cookie.&lt;name&gt;</c>, then no purpose (values signed
    /// before Rails 5.2), then <c>SerializerWithFallback[:json]</c> on the string. Rails' nil is <c>null</c>.
    /// </summary>
    static JsonNode? ReadCookie(Func<string?, MessageResult<JsonNode>> read, string purpose)
    {
        var result = read(purpose);
        if (!result.IsValid)
        {
            result = read(null);
        }
        if (!result.IsValid)
        {
            return null;
        }
        var loaded = MessageSerializer.JsonWithFallback.Load(Encoding.UTF8.GetBytes(result.Value!.GetValue<string>()));
        return loaded.IsValid ? loaded.Value : null;
    }

    /// <summary><c>combine_signed_id_purposes</c> for the models in the vectors (<c>User</c>, <c>Room</c>).</summary>
    static string SignedIdPurpose(string model, string? purpose) =>
        string.IsNullOrWhiteSpace(purpose) ? model.ToLowerInvariant() : $"{model.ToLowerInvariant()}/{purpose}";

    static JsonNode? Node(JsonElement element)
    {
        Assert.True(RailsJson.TryParse(element.GetRawText(), out var node));
        return node;
    }
}
