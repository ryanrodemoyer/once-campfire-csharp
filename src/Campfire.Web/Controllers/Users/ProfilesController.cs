using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.Params;
using Campfire.RailsCompat.Signing;
using Campfire.Storage.Blobs;
using Campfire.Web.Helpers;
using Campfire.Web.Pipeline;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>Users::ProfilesController</c> (reference/app/controllers/users/profiles_controller.rb): the
/// signed-in person's own profile (whatever user the URL names), and changing their name, avatar,
/// email address, password and bio.
/// </summary>
public sealed class UsersProfilesController : ApplicationController
{
    static readonly ControllerCallbacks<UsersProfilesController> Chain = Callbacks.For<UsersProfilesController>();

    static readonly PermitFilter[] UserParams =
    [
        PermitFilter.Key("name"), PermitFilter.Key("avatar"), PermitFilter.Key("email_address"),
        PermitFilter.Key("password"), PermitFilter.Key("bio"),
    ];

    /// <summary>
    /// <c>users/profiles/show</c>, with <c>Current.user.memberships.with_ordered_room</c> split
    /// into direct and shared rooms.
    /// </summary>
    public static readonly RequestDelegate Show = Action(Chain, c => c.ShowAsync());

    public static readonly RequestDelegate Update = Action(Chain, c => c.UpdateAsync());

    // set_user: `@user = Current.user`.
    User CurrentUser => Current.User ?? throw new InvalidOperationException("undefined method 'memberships' for nil");

    ValueTask ShowAsync()
    {
        var user = CurrentUser;
        var avatarPath = Routes.FreshUserAvatarPath(TransferableUser.GenerateAvatarSignedId(App.Keys, user.Id), user.UpdatedAt);
        var transferUrl = Routes.SessionTransferUrl(UrlBase, TransferableUser.GenerateTransferId(App.Keys, user.Id, Now));
        var platform = Platform;
        return this.RenderActionAsync((view, session, w) =>
        {
            var (direct, shared) = Memberships(session, user);
            var avatarAttached = BlobRecords.FindAttachment(session, User.ModelName, user.Id, AttachmentNames.Avatar) is not null;
            view.UsersProfilesShow(w, new ProfilePage(user, FormModelFor(user), avatarPath, avatarAttached, shared, direct, transferUrl, platform));
        });
    }

    // `@user.update user_params` (whose `.compact` drops nil params, so nil changes nothing), then the profile with a notice: a new avatar takes a while to
    // show everywhere.
    async ValueTask UpdateAsync()
    {
        var user = CurrentUser;
        var attributes = Params.RequireHash("user").Permit(UserParams);
        var avatar = await AvatarChangeAsync(attributes).ConfigureAwait(false);
        var now = Now;

        using var staged = avatar is AvatarChange.Upload upload ? App.RequireStorage().Stage(upload.File) : null;
        await WriteAsync(tx =>
        {
            Users.Update(tx.Session, user, now,
                name: attributes["name"] is { } name ? RubyValues.ToS(name) : null,
                emailAddress: attributes["email_address"] is { } emailAddress ? new(RubyValues.ToS(emailAddress)) : null,
                passwordDigest: PasswordDigest(attributes["password"]),
                bio: attributes["bio"] is { } bio ? new(RubyValues.ToS(bio)) : null);
            void Touch(WriteTransaction transaction) => TouchUser(transaction, user.Id, now);
            switch (avatar)
            {
                case AvatarChange.Upload:
                    BlobStorage.AttachOne(tx, staged!, User.ModelName, user.Id, AttachmentNames.Avatar, now, Touch);
                    break;
                case AvatarChange.Existing existing:
                    BlobStorage.AttachOne(tx, existing.Blob, User.ModelName, user.Id, AttachmentNames.Avatar, now, Touch);
                    break;
                case AvatarChange.Remove:
                    UsersAvatarsController.DetachAvatar(tx, user.Id, now);
                    break;
            }
        }).ConfigureAwait(false);

        RedirectTo(Routes.UserProfileUrl(UrlBase), notice: UpdateNotice());
    }

    // `params[:user][:avatar] ? "It may take up to 30 minutes to change everywhere." : "✓"`: any
    // avatar param, even a blank one.
    string UpdateNotice() =>
        Params.RequireHash("user")["avatar"] is not null ? "It may take up to 30 minutes to change everywhere." : "✓";

    // `avatar = value` (has_one_attached): "" detaches it, an upload is attached, and any
    // other string is a blob's signed id (`ActiveStorage::Blob.find_signed!`).
    async ValueTask<AvatarChange?> AvatarChangeAsync(ParamHash attributes)
    {
        switch (attributes["avatar"])
        {
            case null:
                return null;
            case "":
                return new AvatarChange.Remove();
            case UploadedFile file:
                return new AvatarChange.Upload(file);
            case string signedId:
                var id = App.RequireStorage().Urls.VerifySignedId(signedId, Now)
                    ?? throw new InvalidOperationException("ActiveSupport::MessageVerifier::InvalidSignature");
                var blob = await ReadAsync(session => BlobRecords.FindBlob(session, id)).ConfigureAwait(false)
                    ?? throw new RecordNotFoundException($"Couldn't find ActiveStorage::Blob with 'id'={id}");
                return new AvatarChange.Existing(blob);
            case var other:
                throw new InvalidOperationException($"Could not find or build blob: expected attachable, got {other}");
        }
    }

    // has_secure_password's `password=`: an empty password leaves the digest as it is.
    static Change<string?>? PasswordDigest(object? password) =>
        password is not null && RubyValues.ToS(password) is { Length: > 0 } secret ? new(SecurePassword.Digest(secret)) : null;

    // The avatar attachment's `belongs_to :record, touch: true`.
    static void TouchUser(WriteTransaction tx, long userId, DateTimeOffset now) =>
        tx.Session.Execute("""UPDATE "users" SET "updated_at" = @now WHERE "users"."id" = @id""",
            ("@now", Campfire.RailsCompat.Formatting.ActiveRecordTime.ToDb(now)), ("@id", userId));

    // `Current.user.memberships.with_ordered_room.partition { |m| m.room.direct? }`, each with
    // `room_display_name(membership.room)`.
    static (List<ProfileMembership> Direct, List<ProfileMembership> Shared) Memberships(SqliteSession session, User user)
    {
        var (direct, shared) = (new List<ProfileMembership>(), new List<ProfileMembership>());
        foreach (var (membership, room) in Data.Queries.Memberships.ForUserWithOrderedRoom(session, user.Id, visibleOnly: false))
        {
            var members = room.IsDirect ? Users.InRoom(session, room.Id).Select(member => (member.Id, member.Name)) : [];
            var item = new ProfileMembership(
                new RecordKey(room.Type.ClassName(), room.Id),
                room.Id,
                room.IsDirect,
                View.RoomDisplayName(room.Name, room.IsDirect, members, user.Id, user.Name),
                membership.Involvement?.Name() ?? "");
            (room.IsDirect ? direct : shared).Add(item);
        }
        return (direct, shared);
    }

    // `@user` as the profile forms read it.
    static FormModel FormModelFor(User user) => new(
        new RecordKey(User.ModelName, user.Id),
        isPersisted: true,
        new Dictionary<string, object?>
        {
            ["name"] = user.Name,
            ["email_address"] = user.EmailAddress,
            ["password"] = null,
            ["bio"] = user.Bio,
        });

    // What the avatar param does to the attachment.
    abstract record AvatarChange
    {
        public sealed record Remove : AvatarChange;

        public sealed record Upload(UploadedFile File) : AvatarChange;

        public sealed record Existing(Blob Blob) : AvatarChange;
    }
}
