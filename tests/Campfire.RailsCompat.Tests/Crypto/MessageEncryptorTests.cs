using System.Text.Json.Nodes;
using Campfire.RailsCompat.Crypto;

namespace Campfire.RailsCompat.Tests.Crypto;

public class MessageEncryptorTests
{
    static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    static MessageEncryptor Encryptor(byte key = 7) => new(Enumerable.Repeat(key, 32).ToArray(), MessageSerializer.Null);

    [Fact]
    public void Round_trips_with_a_fresh_iv_each_time()
    {
        var encryptor = Encryptor();
        var first = encryptor.EncryptAndSign(JsonValue.Create("hi"), "p", Now.AddHours(1));
        var second = encryptor.EncryptAndSign(JsonValue.Create("hi"), "p", Now.AddHours(1));

        Assert.NotEqual(first, second);
        Assert.Equal("hi", encryptor.DecryptAndVerify(first, "p", Now).Value!.GetValue<string>());
        Assert.Matches("^[A-Za-z0-9+/]+=*--[A-Za-z0-9+/]{16}--[A-Za-z0-9+/]{22}==$", first);
    }

    [Fact]
    public void Rejects_the_wrong_purpose_and_expired_messages()
    {
        var encryptor = Encryptor();
        var message = encryptor.EncryptAndSign(JsonValue.Create("hi"), "p", Now.AddHours(1));

        Assert.Equal(MessageError.PurposeMismatch, encryptor.DecryptAndVerify(message, "q", Now).Error);
        Assert.Equal(MessageError.PurposeMismatch, encryptor.DecryptAndVerify(message, null, Now).Error);
        Assert.Equal(MessageError.Expired, encryptor.DecryptAndVerify(message, "p", Now.AddHours(1)).Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Rejects_a_tampered_part(int part)
    {
        var encryptor = Encryptor();
        var parts = encryptor.EncryptAndSign(JsonValue.Create("hi"), "p").Split("--");
        var bytes = RubyBase64.StrictDecode(parts[part])!;
        bytes[0] ^= 1;
        parts[part] = RubyBase64.StrictEncode(bytes);

        Assert.Equal(MessageError.InvalidSignature, encryptor.DecryptAndVerify(string.Join("--", parts), "p", Now).Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("YQ==--YWJjZGVmZ2hpams=--YWJjZGVmZ2hpamtsbW5vcA==")]
    public void Rejects_malformed_messages(string message) =>
        Assert.Equal(MessageError.InvalidSignature, Encryptor().DecryptAndVerify(message, null, Now).Error);

    [Fact]
    public void Rejects_another_key()
    {
        var message = Encryptor(8).EncryptAndSign(JsonValue.Create("hi"), "p");

        Assert.Equal(MessageError.InvalidSignature, Encryptor().DecryptAndVerify(message, "p", Now).Error);
    }

    [Fact]
    public void Needs_a_32_byte_key() =>
        Assert.Throws<ArgumentException>(() => new MessageEncryptor(new byte[64], MessageSerializer.Null));
}
