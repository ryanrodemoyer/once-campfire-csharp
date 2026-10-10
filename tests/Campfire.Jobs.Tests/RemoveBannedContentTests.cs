using Campfire.Data.Events;
using Campfire.Data.Lifecycle;
using Campfire.Data.MessageAttachments;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.Jobs.Runner;

namespace Campfire.Jobs.Tests;

public sealed class RemoveBannedContentTests : IDisposable
{
    static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    static readonly DateTimeOffset Now = new(2026, 3, 2, 16, 0, 0, TimeSpan.Zero);

    readonly string directory = Path.Combine(Path.GetTempPath(), "campfire-jobs-tests", Guid.NewGuid().ToString("N"));
    readonly SqliteDatabase database;
    readonly RecordingSeams recorded = new();

    public RemoveBannedContentTests()
    {
        Directory.CreateDirectory(directory);
        database = SqliteDatabase.Open(new SqliteDatabaseOptions(Path.Combine(directory, "production.sqlite3")) { Readers = 1 });
    }

    public void Dispose()
    {
        database.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    T Write<T>(Func<WriteTransaction, T> work) => database.WriteAsync(work, Cancellation).GetAwaiter().GetResult();

    T Read<T>(Func<SqliteSession, T> work) => database.ReadAsync(work, Cancellation).GetAwaiter().GetResult();

    Message Post(Room room, User creator, string clientMessageId, string body) =>
        Write(tx => MessageLifecycle.Create(tx, recorded.Seams, room.Id, creator.Id, clientMessageId, $"<div>{body}</div>", body, Now));

    // reference/test/controllers/users/bans_controller_test.rb "RemoveBannedContentJob deletes
    // messages": the ban enqueues the job, and once it has run the user has no messages left.
    [Fact]
    public async Task Ban_enqueues_the_job_and_performing_it_destroys_the_users_messages()
    {
        var (kevin, david) = Write(tx => (
            UserLifecycle.Create(tx, "Kevin", "kevin@37signals.com", null, Now),
            UserLifecycle.Create(tx, "David", "david@37signals.com", null, Now)));
        var hq = Write(tx => Rooms.CreateFor(tx.Session, RoomType.Open, "HQ", david.Id, [kevin.Id, david.Id], Now));
        var direct = Write(tx => Rooms.CreateFor(tx.Session, RoomType.Direct, null, david.Id, [kevin.Id, david.Id], Now));
        // The stream names below are `[room, :messages]` for gid://campfire/Rooms::Open/1 and
        // gid://campfire/Rooms::Direct/2 (vectors/rails_compat.json global_ids).
        Assert.Equal((1, 2), (hq.Id, direct.Id));
        Write(tx => Sessions.Start(tx.Session, kevin.Id, "Test", "203.0.113.1", Now));
        var first = Post(hq, kevin, "test-123", "Test message");
        Post(direct, kevin, "x\"<y>", "Another");
        var kept = Post(hq, david, "david-1", "Stays");
        Write(tx => Boosts.Create(tx.Session, first.Id, david.Id, "👍", Now));
        recorded.Clear();

        var runner = new JobRunner((_, _, _) => { });
        new RemoveBannedContent(database, recorded, TimeProvider.System).RegisterWith(runner);
        var seams = new DomainSeams(recorded, runner, recorded);
        await database.WriteAsync(tx => UserLifecycle.Ban(tx, seams, kevin, Now), Cancellation);
        Assert.True(await runner.ShutdownAsync(TimeSpan.FromSeconds(10)));

        Assert.Empty(Read(session => Messages.ByCreator(session, kevin.Id)));
        Assert.Equal([kept.Id], Read(session => Messages.InRoom(session, hq.Id)).Select(message => message.Id));
        Assert.Empty(Read(session => Boosts.ForMessages(session, [first.Id])));
        Assert.Null(Read(session => RichTexts.For(session, Message.ModelName, first.Id, "body")));
        Assert.Equal(
            [
                new Broadcast(
                    "Z2lkOi8vY2FtcGZpcmUvUm9vbXM6Ok9wZW4vMQ:messages",
                    "\"\\u003cturbo-stream action=\\\"remove\\\" target=\\\"message_test-123\\\"\\u003e\\u003c/turbo-stream\\u003e\""),
                new Broadcast(
                    "Z2lkOi8vY2FtcGZpcmUvUm9vbXM6OkRpcmVjdC8y:messages",
                    "\"\\u003cturbo-stream action=\\\"remove\\\" target=\\\"message_x\\u0026quot;\\u0026lt;y\\u0026gt;\\\"\\u003e\\u003c/turbo-stream\\u003e\""),
            ],
            recorded.Broadcasts);
        Assert.Equal(UserStatus.Banned, Read(session => Users.Find(session, kevin.Id))!.Status);
    }

    [Fact]
    public async Task A_user_without_messages_broadcasts_nothing()
    {
        var kevin = Write(tx => UserLifecycle.Create(tx, "Kevin", "kevin@37signals.com", null, Now));

        await new RemoveBannedContent(database, recorded, TimeProvider.System).PerformAsync(new RemoveBannedContentJob(kevin.Id), Cancellation);

        Assert.Empty(recorded.Events);
    }

    [Fact]
    public async Task Performing_destroys_messages_with_attachments_and_enqueues_purge()
    {
        var kevin = Write(tx => UserLifecycle.Create(tx, "Kevin", "kevin@37signals.com", null, Now));
        var hq = Write(tx => Rooms.CreateFor(tx.Session, RoomType.Open, "HQ", kevin.Id, [kevin.Id], Now));
        var message = Post(hq, kevin, "file-msg", "File attached");
        var blob = Write(tx => Blobs.Create(tx.Session, "blobkey123", "test.pdf", "application/pdf", "{}", "local", 100, "checksum", Now));
        Write(tx => Attachments.Create(tx.Session, Message.ModelName, message.Id, AttachmentNames.Attachment, blob.Id, Now));

        var job = new RemoveBannedContent(database, recorded, recorded, TimeProvider.System);
        recorded.Clear();

        await job.PerformAsync(new RemoveBannedContentJob(kevin.Id), Cancellation);

        Assert.Empty(Read(session => Messages.ByCreator(session, kevin.Id)));
        Assert.Null(Read(session => Attachments.For(session, Message.ModelName, message.Id, AttachmentNames.Attachment)));
        var enqueued = Assert.Single(recorded.Events.OfType<Enqueued>());
        var purge = Assert.IsType<PurgeBlobJob>(enqueued.Job);
        Assert.Equal(blob.Id, purge.BlobId);
    }
}
