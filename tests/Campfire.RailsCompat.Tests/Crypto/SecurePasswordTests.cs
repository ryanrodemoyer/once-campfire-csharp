using Campfire.RailsCompat.Crypto;
using Campfire.Vectors;

namespace Campfire.RailsCompat.Tests.Crypto;

/// <summary>The bcrypt digests in <c>vectors/rails_compat.json</c> (<c>passwords</c>), both ways.</summary>
public class SecurePasswordTests
{
    [Theory]
    [MemberData(nameof(RailsCompatVectors.PasswordChecks), MemberType = typeof(RailsCompatVectors))]
    public void Checks_passwords_against_rails_digests_like_bcrypt(PasswordCheck c) =>
        Assert.Equal(c.Expected, SecurePassword.Authenticate(c.Digest, c.Password));

    [Theory]
    [MemberData(nameof(RailsCompatVectors.PasswordDigests), MemberType = typeof(RailsCompatVectors))]
    public void Rails_digests_authenticate_their_passwords(PasswordDigest c) =>
        Assert.True(SecurePassword.Authenticate(c.Digest, c.Password));

    [Fact]
    public void The_seeded_users_digest_is_secret123456() =>
        Assert.True(SecurePassword.Authenticate(RailsCompatVectors.File.Passwords.SeededUserDigest, "secret123456"));

    [Fact]
    public void New_digests_are_2a_with_the_rails_cost_and_verify()
    {
        var digest = SecurePassword.Digest("secret123456", RailsCompatVectors.File.Passwords.Cost)!;

        Assert.StartsWith("$2a$12$", digest, StringComparison.Ordinal);
        Assert.Equal(60, digest.Length);
        Assert.True(SecurePassword.Authenticate(digest, "secret123456"));
        Assert.False(SecurePassword.Authenticate(digest, "secret12345"));
    }

    [Fact]
    public void Blank_passwords_set_no_digest()
    {
        Assert.Null(SecurePassword.Digest(null));
        Assert.Null(SecurePassword.Digest(""));
    }

    [Fact]
    public void Only_72_bytes_count_and_a_nul_ends_the_secret()
    {
        var digest = SecurePassword.Digest(new string('é', 36), SecurePassword.MinCost)!;

        Assert.True(SecurePassword.Authenticate(digest, new string('é', 36) + "ignored"));
        Assert.True(SecurePassword.Authenticate(SecurePassword.Digest("abc", SecurePassword.MinCost), "abc\0def"));
    }

    [Fact]
    public void A_missing_digest_never_authenticates_and_a_malformed_one_throws()
    {
        Assert.False(SecurePassword.Authenticate(null, "secret123456"));
        Assert.False(SecurePassword.Authenticate("", "secret123456"));
        Assert.Throws<FormatException>(() => SecurePassword.Authenticate("not a digest", "secret123456"));
    }
}
