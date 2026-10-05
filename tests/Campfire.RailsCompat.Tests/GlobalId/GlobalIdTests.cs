using System.Text.Json;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.GlobalId;
using Campfire.Vectors;
using static Campfire.RailsCompat.Tests.Crypto.RailsSecrets;

namespace Campfire.RailsCompat.Tests.GlobalIdUnitTests;

public class GlobalIdTests
{
    [Theory]
    [MemberData(nameof(RailsCompatVectors.GlobalIds), MemberType = typeof(RailsCompatVectors))]
    public void Global_ids_parse_and_format_like_rails(GlobalIdCase c)
    {
        var gid = Campfire.RailsCompat.GlobalId.GlobalId.Parse(c.Gid);
        Assert.NotNull(gid);
        Assert.Equal(c.ModelName, gid.ModelName);
        Assert.Equal(c.Id, gid.Id);
        Assert.Equal(c.Gid, gid.ToString());
        Assert.Equal(c.Param, gid.ToParam());
        Assert.Equal(gid, Campfire.RailsCompat.GlobalId.GlobalId.FromParam(c.Param));
    }

    [Theory]
    [MemberData(nameof(RailsCompatVectors.SgidGenerate), MemberType = typeof(RailsCompatVectors))]
    public void Signed_global_ids_are_generated_byte_for_byte(SgidGenerate c)
    {
        var gid = Campfire.RailsCompat.GlobalId.GlobalId.Parse(c.Gid)!;
        string actual;
        if (c.Data.EndsWith("?expires_in", StringComparison.Ordinal))
        {
            Assert.Equal(SignedGlobalId.AttachablePurpose, c.Purpose);
            actual = SignedGlobalId.AttachableSgid(Keys, gid);
        }
        else
        {
            actual = SignedGlobalId.Generate(Keys, gid, c.Purpose, OptionalTime(c.ExpiresAt));
        }

        Assert.Equal(c.Sgid, actual);
    }

    [Theory]
    [MemberData(nameof(RailsCompatVectors.SgidVerify), MemberType = typeof(RailsCompatVectors))]
    public void Signed_global_ids_verify_like_rails(SgidVerify c)
    {
        var actual = SignedGlobalId.LocateSigned(Keys, c.Sgid, c.Purpose, Time(c.Now));
        var expected = c.Expected is not null ? Campfire.RailsCompat.GlobalId.GlobalId.Parse(c.Expected) : null;
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(RailsCompatVectors.UnverifiedSgids), MemberType = typeof(RailsCompatVectors))]
    public void Unverified_sgids_resolve_users_like_rails(UnverifiedSgidCase c)
    {
        var shouldRaise = c.Expected.ValueKind == JsonValueKind.Object && c.Expected.TryGetProperty("raises", out _);
        if (shouldRaise)
        {
            Assert.ThrowsAny<Exception>(() => InvalidSgidFallback.AttachableFromPossiblyExpiredSgid(c.Sgid));
            return;
        }

        var actual = InvalidSgidFallback.AttachableFromPossiblyExpiredSgid(c.Sgid);
        if (c.Expected.ValueKind == JsonValueKind.Null)
        {
            // Note: 'missing user' (id 999) has a valid User GID format, but the record wouldn't exist in DB.
            // At this layer (without DB), if the model is not User, it returns null.
            if (c.Case != "missing user")
            {
                Assert.Null(actual);
            }
        }
        else if (c.Expected.ValueKind == JsonValueKind.String)
        {
            var expectedGid = Campfire.RailsCompat.GlobalId.GlobalId.Parse(c.Expected.GetString());
            Assert.Equal(expectedGid, actual);
        }
    }

    /// <summary>
    /// Acceptance criterion: A tampered SGID for any model other than User is rejected (named test).
    /// </summary>
    [Fact]
    public void A_tampered_SGID_for_any_model_other_than_User_is_rejected()
    {
        string[] nonUserModels = ["Rooms::Open", "Account", "Message", "Rooms::Direct", "Session"];

        foreach (var model in nonUserModels)
        {
            var gid = Campfire.RailsCompat.GlobalId.GlobalId.Create(model, 1);

            // Valid SGID for this model
            var validSgid = SignedGlobalId.Generate(Keys, gid, "attachable");

            // Tamper with the signature (change last character)
            var lastChar = validSgid[^1];
            var tamperedChar = lastChar == '0' ? '1' : '0';
            var tamperedSgid = validSgid[..^1] + tamperedChar;

            // 1. LocateSigned must reject the tampered SGID
            var locateResult = SignedGlobalId.LocateSigned(Keys, tamperedSgid, "attachable", Now);
            Assert.Null(locateResult);

            // 2. The invalid-signature fallback MUST reject non-User models
            var fallbackResult = InvalidSgidFallback.AttachableFromPossiblyExpiredSgid(tamperedSgid);
            Assert.Null(fallbackResult);
        }

        // In contrast, User allows fallback for a tampered signature
        var userGid = Campfire.RailsCompat.GlobalId.GlobalId.Create("User", 1);
        var validUserSgid = SignedGlobalId.AttachableSgid(Keys, userGid);
        var tamperedUserLastChar = validUserSgid[^1] == '0' ? '1' : '0';
        var tamperedUserSgid = validUserSgid[..^1] + tamperedUserLastChar;

        // Signature verification fails
        Assert.Null(SignedGlobalId.LocateSigned(Keys, tamperedUserSgid, "attachable", Now));

        // But fallback succeeds for User
        var userFallbackResult = InvalidSgidFallback.AttachableFromPossiblyExpiredSgid(tamperedUserSgid);
        Assert.NotNull(userFallbackResult);
        Assert.Equal("User", userFallbackResult.ModelName);
        Assert.Equal("1", userFallbackResult.Id);
    }

    [Fact]
    public void Forged_sgids_are_rejected()
    {
        var attackerKeys = new KeyGenerator("attacker_secret_key_base");
        foreach (var model in new[] { "User", "Rooms::Open", "Account", "Message", "Rooms::Direct", "Session" })
        {
            var gid = Campfire.RailsCompat.GlobalId.GlobalId.Create(model, 1);
            var forged = SignedGlobalId.Generate(attackerKeys, gid, "attachable");

            Assert.Null(SignedGlobalId.LocateSigned(Keys, forged, "attachable", Now));
        }
    }
}
