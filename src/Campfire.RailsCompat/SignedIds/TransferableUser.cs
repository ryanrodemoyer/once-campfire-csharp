using Campfire.RailsCompat.Crypto;

namespace Campfire.RailsCompat.Signing;

/// <summary>
/// <c>User::Transferable</c> (<c>reference/app/models/user/transferable.rb</c>):
/// <c>TRANSFER_LINK_EXPIRY_DURATION = 4.hours</c>.
/// </summary>
public static class TransferableUser
{
    public const string ModelName = "User";
    public const string Purpose = "transfer";
    public static readonly TimeSpan TransferLinkExpiryDuration = TimeSpan.FromHours(4);

    /// <summary><c>user.transfer_id</c>: 4-hour signed ID.</summary>
    public static string GenerateTransferId(KeyGenerator keys, long userId, DateTimeOffset now) =>
        SignedId.Generate(keys, ModelName, userId, Purpose, now + TransferLinkExpiryDuration);

    /// <summary><c>User.find_by_transfer_id(id)</c>.</summary>
    public static long? VerifyTransferId(KeyGenerator keys, string transferId, DateTimeOffset now) =>
        SignedId.Verify(keys, ModelName, transferId, Purpose, now);

    /// <summary><c>user.avatar_signed_id</c>: avatar signed ID with no expiry.</summary>
    public static string GenerateAvatarSignedId(KeyGenerator keys, long userId) =>
        SignedId.Generate(keys, ModelName, userId, "avatar", null);

    /// <summary><c>User.find_by_avatar_signed_id(id)</c>.</summary>
    public static long? VerifyAvatarSignedId(KeyGenerator keys, string avatarSignedId, DateTimeOffset now) =>
        SignedId.Verify(keys, ModelName, avatarSignedId, "avatar", now);
}
