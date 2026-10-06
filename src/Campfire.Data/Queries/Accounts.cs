using Campfire.Data.Records;
using Campfire.Data.Sqlite;

namespace Campfire.Data.Queries;

// reference/app/models/account.rb and account/joinable.rb
public static class Accounts
{
    const string select = $"SELECT {Account.Columns} FROM \"accounts\"";

    // `Account.first`, which is `Current.account`.
    public static Account? First(SqliteSession session) =>
        Sql.One(session, $"{select} ORDER BY \"accounts\".\"id\" ASC LIMIT 1", Account.Read);

    public static Account? Find(SqliteSession session, long id) =>
        Sql.One(session, $"{select} WHERE \"accounts\".\"id\" = @id LIMIT 1", Account.Read, ("@id", id));

    public static long Count(SqliteSession session) => Sql.Count(session, "SELECT COUNT(*) FROM \"accounts\"");

    // `Account.create!(name:)`: a new join code, and the settings defaults written out by
    // `has_json`'s before_save.
    public static Account Create(SqliteSession session, string name, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        var joinCode = Account.GenerateJoinCode();
        var settings = AccountSettings.Parse(null).ToJson();
        var id = Sql.Insert(session, """
            INSERT INTO "accounts" ("created_at", "join_code", "name", "settings", "updated_at") VALUES (@now, @join_code, @name, @settings, @now) RETURNING "id"
            """, ("@now", Db.Time(now)), ("@join_code", joinCode), ("@name", name), ("@settings", settings));
        return Find(session, id)!;
    }

    // `reset_join_code`
    public static Account ResetJoinCode(SqliteSession session, Account account, DateTimeOffset now) =>
        Update(session, account, now, joinCode: Account.GenerateJoinCode());

    // `update!` of the given attributes: only the changed ones are written, with `updated_at`,
    // and nothing at all when none changed.
    public static Account Update(
        SqliteSession session,
        Account account,
        DateTimeOffset now,
        string? name = null,
        Change<string?>? customStyles = null,
        string? joinCode = null,
        AccountSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        var changes = new List<(string, object?)>();
        if (name is not null && name != account.Name)
        {
            changes.Add(("name", name));
        }
        if (customStyles is { Value: var styles } && styles != account.CustomStyles)
        {
            changes.Add(("custom_styles", styles));
        }
        if (joinCode is not null && joinCode != account.JoinCode)
        {
            changes.Add(("join_code", joinCode));
        }
        // `has_json`'s before_save writes the defaults into the hash, so saving writes the
        // settings whenever they differ from the stored hash, even when nothing was assigned.
        var assigned = settings ?? account.SettingsData;
        if (!assigned.IsStoredAs(account.Settings))
        {
            changes.Add(("settings", assigned.ToJson()));
        }
        return Sql.UpdateChanged(session, "accounts", account.Id, changes, now) ? Find(session, account.Id)! : account;
    }
}
