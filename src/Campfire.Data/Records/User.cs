using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Campfire.Data.Records;

// `enum :role, %i[ member administrator bot ]` (reference/app/models/user/role.rb): the integer
// Rails stores.
public enum UserRole
{
    Member = 0,
    Administrator = 1,
    Bot = 2,
}

// `enum :status, %i[ active deactivated banned ], default: :active`
public enum UserStatus
{
    Active = 0,
    Deactivated = 1,
    Banned = 2,
}

// A row of `users` (reference/app/models/user.rb and user/*.rb). Bots are users with the bot
// role and a `bot_token`.
public sealed partial record User(
    long Id,
    string Name,
    string? EmailAddress,
    string? PasswordDigest,
    UserRole Role,
    UserStatus Status,
    string? Bio,
    string? BotToken,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    // The polymorphic `record_type` of a user's rows (avatar attachments, mentions' SGIDs).
    public const string ModelName = "User";

    // `SecureRandom.alphanumeric(12)` (`User.generate_bot_token`)
    public const int BotTokenLength = 12;

    internal const string Columns = """
        "users"."id", "users"."name", "users"."email_address", "users"."password_digest", "users"."role", "users"."status", "users"."bio", "users"."bot_token", "users"."created_at", "users"."updated_at"
        """;

    public bool IsMember => Role == UserRole.Member;

    public bool IsAdministrator => Role == UserRole.Administrator;

    public bool IsBot => Role == UserRole.Bot;

    public bool IsActive => Status == UserStatus.Active;

    public bool IsDeactivated => Status == UserStatus.Deactivated;

    public bool IsBanned => Status == UserStatus.Banned;

    // `"#{id}-#{bot_token}"`
    public string BotKey => $"{Id}-{BotToken}";

    // `name.scan(/\b\w/).join`. Ruby's `\w` is ASCII while its `\b` knows Unicode letters, so
    // "Über" contributes nothing and neither does the "y" in "éy".
    public string Initials => string.Concat(InitialPattern().Matches(Name).Select(match => match.Value));

    // `[ name, bio ].compact_blank.join(" – ")`
    public string Title => string.Join(" – ", new[] { Name, Bio }.Where(part => !string.IsNullOrWhiteSpace(part)));

    // `can_administer?(record)`: administrators can, and so can the record's creator.
    public bool CanAdminister(long? creatorId = null) => IsAdministrator || (creatorId is { } id && id == Id);

    // `User.generate_bot_token`
    public static string GenerateBotToken() => SecureTokens.Alphanumeric(BotTokenLength);

    // `email_address&.gsub(/@/, "-deactivated-#{SecureRandom.uuid}@")`
    public static string? DeactivatedEmailAddress(string? emailAddress, string uuid) =>
        emailAddress?.Replace("@", $"-deactivated-{uuid}@", StringComparison.Ordinal);

    internal static User Read(SqliteDataReader reader) => ReadAt(reader, 0);

    internal static User ReadAt(SqliteDataReader reader, int at) => new(
        reader.GetInt64(at + 0),
        reader.GetString(at + 1),
        Db.ReadNullableString(reader, at + 2),
        Db.ReadNullableString(reader, at + 3),
        (UserRole)reader.GetInt32(at + 4),
        (UserStatus)reader.GetInt32(at + 5),
        Db.ReadNullableString(reader, at + 6),
        Db.ReadNullableString(reader, at + 7),
        Db.ReadTime(reader, at + 8),
        Db.ReadTime(reader, at + 9));

    [GeneratedRegex(@"\b[A-Za-z0-9_]")]
    private static partial Regex InitialPattern();
}
