using Campfire.Data.MessageAttachments;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.Params;
using Campfire.Storage.Blobs;
using Campfire.Web.Pipeline;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>FirstRunsController</c> (reference/app/controllers/first_runs_controller.rb) and
/// <c>FirstRun</c> (reference/app/models/first_run.rb): the first administrator signs up, which
/// creates the account and its first room.
/// </summary>
public sealed class FirstRunsController : ApplicationController
{
    /// <summary><c>FirstRun::ACCOUNT_NAME</c></summary>
    public const string AccountName = "Campfire";

    /// <summary><c>FirstRun::FIRST_ROOM_NAME</c></summary>
    public const string FirstRoomName = "All Talk";

    static readonly ControllerCallbacks<FirstRunsController> Chain = Callbacks.For<FirstRunsController>()
        .AllowUnauthenticatedAccess()
        .Before("prevent_repeats", c => c.PreventRepeatsAsync());

    static readonly PermitFilter[] UserParams =
        [PermitFilter.Key("name"), PermitFilter.Key("avatar"), PermitFilter.Key("email_address"), PermitFilter.Key("password")];

    public static readonly RequestDelegate Show = Action(Chain, c => c.RenderActionAsync((view, _, w) => view.FirstRunsShow(w)));

    public static readonly RequestDelegate Create = Action(Chain, c => c.CreateAsync());

    async ValueTask CreateAsync()
    {
        var attributes = Params.RequireHash("user").Permit(UserParams);
        User user;
        try
        {
            user = await CreateFirstRunAsync(attributes).ConfigureAwait(false);
        }
        catch (SqliteException error) when (IsRecordNotUnique(error))
        {
            RedirectTo(Routes.RootUrl(RequestUrl.UrlBase));
            return;
        }
        await StartNewSessionForAsync(user).ConfigureAwait(false);
        RedirectTo(Routes.RootUrl(RequestUrl.UrlBase));
    }

    // `redirect_to root_url if Account.any?`
    async ValueTask PreventRepeatsAsync()
    {
        if (await ReadAsync(session => Accounts.Count(session) > 0).ConfigureAwait(false))
        {
            RedirectTo(Routes.RootUrl(RequestUrl.UrlBase));
        }
    }

    // `FirstRun.create!(user_params)`: the account in a transaction of its own, then the
    // administrator (autosaved as the room's creator) and the open room in another. After that
    // commit, `Rooms::Open#grant_access_to_all_users` runs before
    // `User#grant_membership_to_open_rooms` (traced on edbc779). The room's `insert_all` stamps
    // `STRFTIME('%Y-%m-%d %H:%M:%f', 'NOW')` and wins; the later grants conflict and do nothing.
    async ValueTask<User> CreateFirstRunAsync(ParamHash attributes)
    {
        var name = attributes["name"] is { } nameParam ? RubyValues.ToS(nameParam) : null;
        var emailAddress = attributes["email_address"] is { } emailParam ? RubyValues.ToS(emailParam) : null;
        var passwordDigest = attributes["password"] is { } passwordParam ? SecurePassword.Digest(RubyValues.ToS(passwordParam)) : null;
        var avatar = Avatar(attributes["avatar"]);
        var now = Now;

        await WriteAsync(tx => Accounts.Create(tx.Session, AccountName, now)).ConfigureAwait(false);
        using var staged = avatar is null ? null : App.RequireStorage().Stage(avatar);
        // The attachment's `before_validation` analysis (analyze: :immediately).
        staged?.AnalyzeWithoutSaving();
        var jobs = App.RequireSeams().Jobs;
        return await WriteAsync(tx =>
        {
            // users.name is NOT NULL: a missing name fails here, as ActiveRecord::NotNullViolation does.
            // Not UserLifecycle.Create: that would register grant_membership_to_open_rooms first,
            // and its Time.current stamp would beat the room's STRFTIME insert.
            var administrator = Users.Create(tx.Session, name!, emailAddress, passwordDigest, now, UserRole.Administrator);
            if (staged is not null)
            {
                var attached = BlobStorage.AttachOne(tx, staged, "User", administrator.Id, "avatar", now);
                ActiveStoragePurge.Later(tx, jobs, attached.ReplacedBlobId);
                if (!attached.Blob.IsAnalyzed)
                {
                    tx.AfterCommit(_ => jobs.Enqueue(new AnalyzeBlobJob(attached.Blob.Id)));
                }
            }
            var room = Rooms.Create(tx.Session, RoomType.Open, FirstRoomName, administrator.Id, now);
            tx.AfterCommit(after => Memberships.GrantTo(after, room, Users.ActiveIds(after)));
            tx.AfterCommit(after => Memberships.GrantOpenRoomsTo(after, administrator.Id, now));
            tx.AfterCommit(after => Memberships.GrantTo(after, room, [administrator.Id]));
            return administrator;
        }).ConfigureAwait(false);
    }

    // `avatar = value`: an upload is attached; nil and "" attach nothing.
    static UploadedFile? Avatar(object? value) => value switch
    {
        null or "" => null,
        UploadedFile file => file,
        _ => throw new InvalidOperationException("Could not find or build blob: expected attachable"),
    };

    // ActiveRecord::RecordNotUnique: SQLITE_CONSTRAINT_UNIQUE (and _PRIMARYKEY).
    static bool IsRecordNotUnique(SqliteException error) =>
        error.SqliteErrorCode == 19 && error.SqliteExtendedErrorCode is 2067 or 1555;
}
