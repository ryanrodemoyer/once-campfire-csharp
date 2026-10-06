using Campfire.Data.Records;
using Campfire.Data.Sqlite;

namespace Campfire.Data.Tests.Records;

// The values Rails wrote to the parity seed (Oracle/parity-seed.sqlite3, built by
// `parity/bin/seed build default` and matching parity/.seed/SEED.sha256): every row reads into a
// record, and each STI name, enum integer, involvement string and polymorphic record_type maps to
// the record's value and back to the same text or integer.
public class SeedValuesTests : IDisposable
{
    readonly TestDatabase file = TestDatabase.FromParitySeed();
    readonly SqliteDatabase database;

    public SeedValuesTests() => database = SqliteDatabase.Open(new SqliteDatabaseOptions(file.Path) { Readers = 1 });

    public void Dispose()
    {
        database.Dispose();
        file.Dispose();
        GC.SuppressFinalize(this);
    }

    Task<T> Read<T>(Func<SqliteSession, T> work) => database.ReadAsync(work, TestContext.Current.CancellationToken);

    static List<(long Id, T Value)> Column<T>(SqliteSession session, string table, string column) =>
        session.Query($"SELECT id, \"{column}\" FROM \"{table}\" ORDER BY id", reader => (reader.GetInt64(0), reader.GetFieldValue<T>(1)));

    [Fact]
    public async Task Room_types_are_the_sti_class_names()
    {
        var (raw, rooms) = await Read(session => (Column<string>(session, "rooms", "type"), session.Query($"SELECT {Room.Columns} FROM rooms ORDER BY id", Room.Read)));

        Assert.Equal(["Rooms::Closed", "Rooms::Direct", "Rooms::Open"], raw.Select(row => row.Value).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(raw.Select(row => row.Value), rooms.Select(room => room.Type.ClassName()));
        Assert.Equal(raw.Select(row => row.Value == "Rooms::Direct"), rooms.Select(room => room.IsDirect));
        Assert.Equal(raw.Select(row => row.Value == "Rooms::Open"), rooms.Select(room => room.IsOpen));
        Assert.Equal(raw.Select(row => row.Value == "Rooms::Closed"), rooms.Select(room => room.IsClosed));
    }

    [Fact]
    public async Task User_roles_and_statuses_are_the_enum_integers()
    {
        var (roles, statuses, users) = await Read(session => (
            Column<long>(session, "users", "role"),
            Column<long>(session, "users", "status"),
            session.Query($"SELECT {User.Columns} FROM users ORDER BY id", User.Read)));

        // Every role and every status appears in the seed.
        Assert.Equal([0L, 1, 2], roles.Select(row => row.Value).Distinct().Order());
        Assert.Equal([0L, 1, 2], statuses.Select(row => row.Value).Distinct().Order());
        Assert.Equal(roles.Select(row => row.Value), users.Select(user => (long)user.Role));
        Assert.Equal(statuses.Select(row => row.Value), users.Select(user => (long)user.Status));
        Assert.All(users, user => Assert.True(Enum.IsDefined(user.Role) && Enum.IsDefined(user.Status)));

        // Named as the reference names them (user/role.rb, user.rb).
        Assert.Equal((UserRole)0, UserRole.Member);
        Assert.Equal((UserRole)1, UserRole.Administrator);
        Assert.Equal((UserRole)2, UserRole.Bot);
        Assert.Equal((UserStatus)0, UserStatus.Active);
        Assert.Equal((UserStatus)1, UserStatus.Deactivated);
        Assert.Equal((UserStatus)2, UserStatus.Banned);
        Assert.All(users.Where(user => user.IsBot), bot => Assert.NotNull(bot.BotToken));
    }

    [Fact]
    public async Task Involvements_are_the_enum_names()
    {
        var (raw, memberships) = await Read(session => (
            Column<string>(session, "memberships", "involvement"),
            session.Query($"SELECT {Membership.Columns} FROM memberships ORDER BY id", Membership.Read)));

        Assert.Equal(["everything", "invisible", "mentions", "nothing"], raw.Select(row => row.Value).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(raw.Select(row => row.Value), memberships.Select(membership => membership.Involvement!.Value.Name()));
    }

    [Fact]
    public async Task Polymorphic_record_types_are_the_model_names()
    {
        var (richTexts, attachments, attachmentRows) = await Read(session => (
            session.Query($"SELECT {ActionTextRichText.Columns} FROM action_text_rich_texts ORDER BY id", ActionTextRichText.Read),
            session.Query("SELECT DISTINCT record_type, name FROM active_storage_attachments ORDER BY record_type, name", reader => (reader.GetString(0), reader.GetString(1))),
            session.Query($"SELECT {ActiveStorageAttachment.Columns} FROM active_storage_attachments ORDER BY id", ActiveStorageAttachment.Read)));

        Assert.All(richTexts, richText => Assert.Equal((RecordTypes.Message, AttachmentNames.Body), (richText.RecordType, richText.Name)));
        Assert.Equal(
        [
            (RecordTypes.Blob, AttachmentNames.PreviewImage),
            (RecordTypes.VariantRecord, AttachmentNames.Image),
            (RecordTypes.Message, AttachmentNames.Attachment),
            (RecordTypes.User, AttachmentNames.Avatar),
        ], attachments);
        Assert.Equal(["ActiveStorage::Blob", "ActiveStorage::VariantRecord", "Message", "User"], attachments.Select(pair => pair.Item1));
        Assert.Equal(attachments.Count, attachmentRows.Select(row => (row.RecordType, row.Name)).Distinct().Count());
    }

    [Fact]
    public async Task Every_row_reads_into_its_record()
    {
        var counts = await Read(session => new Dictionary<string, (long Rows, int Records)>
        {
            ["accounts"] = Count(session, "accounts", Account.Columns, Account.Read),
            ["users"] = Count(session, "users", User.Columns, User.Read),
            ["sessions"] = Count(session, "sessions", Session.Columns, Session.Read),
            ["rooms"] = Count(session, "rooms", Room.Columns, Room.Read),
            ["memberships"] = Count(session, "memberships", Membership.Columns, Membership.Read),
            ["messages"] = Count(session, "messages", Message.Columns, Message.Read),
            ["boosts"] = Count(session, "boosts", Boost.Columns, Boost.Read),
            ["bans"] = Count(session, "bans", Ban.Columns, Ban.Read),
            ["searches"] = Count(session, "searches", Search.Columns, Search.Read),
            ["push_subscriptions"] = Count(session, "push_subscriptions", PushSubscription.Columns, PushSubscription.Read),
            ["webhooks"] = Count(session, "webhooks", Webhook.Columns, Webhook.Read),
            ["action_text_rich_texts"] = Count(session, "action_text_rich_texts", ActionTextRichText.Columns, ActionTextRichText.Read),
            ["active_storage_blobs"] = Count(session, "active_storage_blobs", ActiveStorageBlob.Columns, ActiveStorageBlob.Read),
            ["active_storage_attachments"] = Count(session, "active_storage_attachments", ActiveStorageAttachment.Columns, ActiveStorageAttachment.Read),
            ["active_storage_variant_records"] = Count(session, "active_storage_variant_records", ActiveStorageVariantRecord.Columns, ActiveStorageVariantRecord.Read),
        });

        Assert.All(counts, pair => Assert.Equal(pair.Value.Rows, pair.Value.Records));
        Assert.All(counts, pair => Assert.True(pair.Value.Rows > 0, $"{pair.Key} has rows in the seed"));
    }

    [Fact]
    public async Task Timestamps_read_back_as_the_text_rails_wrote()
    {
        var (raw, messages) = await Read(session => (
            Column<string>(session, "messages", "created_at"),
            session.Query($"SELECT {Message.Columns} FROM messages ORDER BY id", Message.Read)));

        Assert.Equal(raw.Select(row => row.Value), messages.Select(message => Db.Time(message.CreatedAt)));
    }

    static (long Rows, int Records) Count<T>(SqliteSession session, string table, string columns, Func<Microsoft.Data.Sqlite.SqliteDataReader, T> read) =>
        (session.Scalar<long>($"SELECT COUNT(*) FROM \"{table}\""), session.Query($"SELECT {columns} FROM \"{table}\"", read).Count);
}
