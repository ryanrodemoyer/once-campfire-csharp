using System.Text.Json.Nodes;
using Campfire.RailsCompat.Crypto;

namespace Campfire.RailsCompat.Tests.Crypto;

public class MessageVerifierTests
{
    static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    static readonly KeyGenerator Keys = new("secret");

    static MessageVerifier Verifier(string secretKeyBase = "secret", MessageSerializer serializer = MessageSerializer.JsonAllowMarshal) =>
        new(new KeyGenerator(secretKeyBase).GenerateKey("test"), MessageDigest.Sha1, MessageEncoding.Strict, serializer);

    [Fact]
    public void Round_trips_with_purpose_and_expiry()
    {
        var message = Verifier().Generate(new JsonObject { ["b"] = 1, ["a"] = "<&>" }, "p", Now.AddMinutes(5));

        Assert.Equal("""{"b":1,"a":"\u003c\u0026\u003e"}""", Verifier().VerifyRaw(message, "p", Now).Value);
    }

    [Fact]
    public void Rejects_the_wrong_purpose()
    {
        var verifier = Verifier();
        var message = verifier.Generate(JsonValue.Create(1), "avatar");

        Assert.Equal(MessageError.PurposeMismatch, verifier.Verify(message, "transfer", Now).Error);
        Assert.Equal(MessageError.PurposeMismatch, verifier.Verify(message, null, Now).Error);
        Assert.Equal(MessageError.PurposeMismatch, verifier.Verify(verifier.Generate(JsonValue.Create(1)), "avatar", Now).Error);
    }

    [Fact]
    public void Rejects_at_and_after_expiry()
    {
        var verifier = Verifier();
        var expiresAt = Now.AddHours(4);
        var message = verifier.Generate(JsonValue.Create(1), "transfer", expiresAt);

        Assert.True(verifier.Verify(message, "transfer", expiresAt.AddTicks(-1)).IsValid);
        Assert.Equal(MessageError.Expired, verifier.Verify(message, "transfer", expiresAt).Error);
        Assert.Equal(MessageError.Expired, verifier.Verify(message, "transfer", expiresAt.AddDays(1)).Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(-1)]
    [InlineData(-30)]
    public void Rejects_a_tampered_message(int index)
    {
        var verifier = Verifier();
        var message = verifier.Generate(JsonValue.Create("value"), "p");
        var at = index < 0 ? message.Length + index : index;
        var tampered = message[..at] + (message[at] == 'A' ? 'B' : 'A') + message[(at + 1)..];

        Assert.Equal(MessageError.InvalidSignature, verifier.Verify(tampered, "p", Now).Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("--")]
    [InlineData(" --da39a3ee5e6b4b0d3255bfef95601890afd80709")]
    public void Rejects_malformed_messages(string message) =>
        Assert.Equal(MessageError.InvalidSignature, Verifier().Verify(message, null, Now).Error);

    [Fact]
    public void Rejects_another_secret()
    {
        var message = Verifier("attacker").Generate(JsonValue.Create("gid://campfire/User/1"), "attachable");

        Assert.Equal(MessageError.InvalidSignature, Verifier().Verify(message, "attachable", Now).Error);
    }

    [Fact]
    public void Authentic_but_unreadable_payloads_are_invalid_messages()
    {
        var json = Verifier(serializer: MessageSerializer.JsonWithFallback);
        var marshaled = json.GenerateRaw("\u0004\u0008I\"\u0006x\u0006:\u0006ET");

        Assert.Equal(MessageError.InvalidMessage, json.Verify(marshaled, null, Now).Error);
        Assert.Equal(MessageError.InvalidMessage, json.Verify(json.GenerateRaw("{not json"), null, Now).Error);
        Assert.Equal("x", Verifier().Verify(marshaled, null, Now).Value!.GetValue<string>());
    }

    [Fact]
    public void Falls_back_to_rotations_only_for_unreadable_messages()
    {
        var old = Verifier("old");
        var current = Verifier().FallBackTo(old);

        Assert.Equal(1, current.Verify(old.Generate(JsonValue.Create(1), "p"), "p", Now).Value!.GetValue<int>());
        // An expired or mismatched message stops at the first verifier that can read it.
        var expired = current.Generate(JsonValue.Create(1), "p", Now);
        Assert.Equal(MessageError.Expired, current.Verify(expired, "p", Now).Error);
        Assert.Equal(MessageError.PurposeMismatch, current.Verify(old.Generate(JsonValue.Create(1), "p"), "q", Now).Error);
        Assert.Equal(MessageError.InvalidSignature, current.Verify(Verifier("other").Generate(JsonValue.Create(1)), null, Now).Error);
    }

    [Fact]
    public void Url_safe_messages_read_with_either_alphabet()
    {
        var urlSafe = new MessageVerifier(Keys.GenerateKey("test"), MessageDigest.Sha256, MessageEncoding.UrlSafe, MessageSerializer.Json);
        var value = JsonValue.Create(new string('x', 50) + "?>");
        var message = urlSafe.Generate(value, "p");

        Assert.DoesNotContain('=', message);
        Assert.DoesNotContain('+', message);
        Assert.DoesNotContain('/', message);
        Assert.Equal(64, message.Split("--")[1].Length);
        Assert.Equal(RailsJson.Generate(value), urlSafe.VerifyRaw(message, "p", Now).Value);
    }

    [Fact]
    public void Null_serializer_signs_strings_in_the_legacy_envelope()
    {
        var verifier = Verifier(serializer: MessageSerializer.Null);
        var message = verifier.GenerateRaw("\"token\"", "cookie.session_token");

        Assert.Equal("\"token\"", verifier.VerifyRaw(message, "cookie.session_token", Now).Value);
        Assert.Throws<ArgumentException>(() => verifier.Generate(JsonValue.Create(1)));
    }
}
