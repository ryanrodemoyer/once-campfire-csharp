using Microsoft.Data.Sqlite;

namespace Campfire.Data.Records;

// A row of `accounts` (reference/app/models/account.rb). There is one: `Current.account` is
// `Account.first`.
public sealed record Account(
    long Id,
    string Name,
    string JoinCode,
    string? CustomStyles,
    string? Settings,
    long SingletonGuard,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    internal const string Columns = """
        "accounts"."id", "accounts"."name", "accounts"."join_code", "accounts"."custom_styles", "accounts"."settings", "accounts"."singleton_guard", "accounts"."created_at", "accounts"."updated_at"
        """;

    // `has_json :settings, ...`: the stored hash with the schema's defaults.
    public AccountSettings SettingsData => AccountSettings.Parse(Settings);

    // `SecureRandom.alphanumeric(12).scan(/.{4}/).join("-")` (reference/app/models/account/joinable.rb)
    public static string GenerateJoinCode()
    {
        var code = SecureTokens.Alphanumeric(12);
        return $"{code[..4]}-{code[4..8]}-{code[8..]}";
    }

    internal static Account Read(SqliteDataReader reader) => ReadAt(reader, 0);

    internal static Account ReadAt(SqliteDataReader reader, int at) => new(
        reader.GetInt64(at + 0),
        reader.GetString(at + 1),
        reader.GetString(at + 2),
        Db.ReadNullableString(reader, at + 3),
        Db.ReadNullableString(reader, at + 4),
        reader.GetInt64(at + 5),
        Db.ReadTime(reader, at + 6),
        Db.ReadTime(reader, at + 7));
}
