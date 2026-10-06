namespace Campfire.RailsCompat.Crypto;

/// <summary>
/// <c>has_secure_password</c>'s digest (<c>ActiveModel::SecurePassword</c> over the bcrypt gem,
/// 3.1.22): <c>BCrypt::Password.create(password, cost:)</c> writes a <c>$2a$</c> digest and
/// <c>authenticate_password</c> re-hashes with the digest's salt.
/// <para>
/// The gem hands the secret to crypt_blowfish as a C string, so it ends at the first NUL, and
/// Blowfish's key schedule only reads 72 bytes of it: anything past either is ignored.
/// </para>
/// </summary>
public static class SecurePassword
{
    /// <summary><c>BCrypt::Engine.cost</c> outside tests (<c>DEFAULT_COST</c>).</summary>
    public const int DefaultCost = 12;

    /// <summary><c>BCrypt::Engine::MIN_COST</c>, which tests use (<c>SecurePassword.min_cost</c>).</summary>
    public const int MinCost = 4;

    /// <summary>
    /// <c>password = value</c>: null for a null password, and also for an empty one (which leaves
    /// the digest unchanged, so a new record's stays nil); otherwise a new <c>$2a$</c> digest.
    /// </summary>
    public static string? Digest(string? password, int cost = DefaultCost)
    {
        if (string.IsNullOrEmpty(password))
        {
            return null;
        }
        var salt = BCrypt.Net.BCrypt.GenerateSalt(cost, 'a');
        return BCrypt.Net.BCrypt.HashPassword(CString(password), salt);
    }

    /// <summary>
    /// <c>authenticate_password(password)</c>: <c>password_digest.present? &amp;&amp;
    /// BCrypt::Password.new(password_digest).is_password?(password)</c>. A digest bcrypt can't
    /// parse throws, as <c>BCrypt::Errors::InvalidHash</c> does.
    /// </summary>
    public static bool Authenticate(string? digest, string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (string.IsNullOrWhiteSpace(digest))
        {
            return false;
        }
        if (!IsValidHash(digest))
        {
            throw new FormatException("invalid hash");
        }
        var hashed = BCrypt.Net.BCrypt.HashPassword(CString(password), digest[..29]);
        return SecurityUtils.SecureCompare(hashed, digest);
    }

    // BCrypt::Password#valid_hash?: /\A\$[0-9a-z]{2}\$[0-9]{2}\$[A-Za-z0-9\.\/]{53}\z/
    static bool IsValidHash(string digest) =>
        digest.Length == 60 && digest[0] == '$' && IsRevision(digest[1]) && IsRevision(digest[2]) && digest[3] == '$'
        && char.IsAsciiDigit(digest[4]) && char.IsAsciiDigit(digest[5]) && digest[6] == '$'
        && digest.AsSpan(7).IndexOfAnyExcept(Bcrypt64) < 0;

    static readonly System.Buffers.SearchValues<char> Bcrypt64 =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789./");

    static bool IsRevision(char c) => char.IsAsciiDigit(c) || char.IsAsciiLetterLower(c);

    // The secret as crypt_blowfish reads it: up to the first NUL.
    static string CString(string password) =>
        password.IndexOf('\0', StringComparison.Ordinal) is var nul and >= 0 ? password[..nul] : password;
}
