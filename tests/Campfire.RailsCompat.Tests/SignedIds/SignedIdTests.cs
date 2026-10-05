using System.Text.Json;
using Campfire.RailsCompat.Signing;
using Campfire.Vectors;
using static Campfire.RailsCompat.Tests.Crypto.RailsSecrets;

namespace Campfire.RailsCompat.Tests.SignedIdsUnitTests;

public class SignedIdTests
{
    [Theory]
    [MemberData(nameof(RailsCompatVectors.SignedIdGenerate), MemberType = typeof(RailsCompatVectors))]
    public void Signed_ids_are_generated_byte_for_byte(SignedIdGenerate c)
    {
        var expiresAt = OptionalTime(c.ExpiresAt);
        var actual = SignedId.Generate(Keys, c.Model, c.Id, c.Purpose, expiresAt);
        Assert.Equal(c.SignedId, actual);
    }

    [Theory]
    [MemberData(nameof(RailsCompatVectors.SignedIdVerify), MemberType = typeof(RailsCompatVectors))]
    public void Signed_ids_verify_like_rails(SignedIdVerify c)
    {
        var now = Time(c.Now);
        var actual = SignedId.Verify(Keys, c.Model, c.SignedId, c.Purpose, now);

        long? expected = c.Expected.ValueKind switch
        {
            JsonValueKind.Number => c.Expected.GetInt64(),
            JsonValueKind.String => long.Parse(c.Expected.GetString()!),
            _ => null,
        };

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TransferableUser_generates_and_verifies_with_4h_expiry()
    {
        var now = Now;
        var transferId = TransferableUser.GenerateTransferId(Keys, 42, now);
        Assert.NotNull(transferId);

        // Valid right now
        Assert.Equal(42, TransferableUser.VerifyTransferId(Keys, transferId, now));

        // Valid 3.9 hours later
        Assert.Equal(42, TransferableUser.VerifyTransferId(Keys, transferId, now.AddHours(3.9)));

        // Expired 4.1 hours later
        Assert.Null(TransferableUser.VerifyTransferId(Keys, transferId, now.AddHours(4.1)));
    }

    [Fact]
    public void TransferableUser_avatar_signed_id_has_no_expiry()
    {
        var now = Now;
        var avatarId = TransferableUser.GenerateAvatarSignedId(Keys, 99);
        Assert.NotNull(avatarId);

        // Valid now
        Assert.Equal(99, TransferableUser.VerifyAvatarSignedId(Keys, avatarId, now));

        // Valid 10 years later
        Assert.Equal(99, TransferableUser.VerifyAvatarSignedId(Keys, avatarId, now.AddYears(10)));
    }

    [Theory]
    [MemberData(nameof(RailsCompatVectors.TurboStreamNameGenerate), MemberType = typeof(RailsCompatVectors))]
    public void Turbo_stream_names_are_generated_byte_for_byte(TurboStreamNameGenerate c)
    {
        var actual = TurboStreamName.SignedStreamName(Keys, c.Parts);
        Assert.Equal(c.SignedName, actual);
    }

    [Theory]
    [MemberData(nameof(RailsCompatVectors.TurboStreamNameVerify), MemberType = typeof(RailsCompatVectors))]
    public void Turbo_stream_names_verify_like_rails(TurboStreamNameVerify c)
    {
        var actual = TurboStreamName.VerifiedStreamName(Keys, c.SignedName);
        var expected = c.Expected.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => c.Expected.GetString(),
            _ => c.Expected.GetRawText(),
        };

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Combines_purposes_with_underscoring()
    {
        Assert.Equal("user/avatar", SignedId.CombinePurposes("User", "avatar"));
        Assert.Equal("user", SignedId.CombinePurposes("User", null));
        Assert.Equal("user", SignedId.CombinePurposes("User", ""));
        Assert.Equal("rooms/open", SignedId.CombinePurposes("Rooms::Open", null));
        Assert.Equal("rooms/open/special", SignedId.CombinePurposes("Rooms::Open", "special"));
        Assert.Equal("http_request/x", SignedId.CombinePurposes("HTTPRequest", "x"));
        Assert.Equal("web_push", SignedId.CombinePurposes("WebPush", null));
    }
}
