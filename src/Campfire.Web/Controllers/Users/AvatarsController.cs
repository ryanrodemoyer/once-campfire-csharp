using System.Buffers;
using Campfire.Data.Events;
using Campfire.Data.MessageAttachments;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Formatting;
using Campfire.RailsCompat.Signing;
using Campfire.Storage.Blobs;
using Campfire.Storage.Media;
using Campfire.Storage.Variants;
using Campfire.Web.Helpers;
using Campfire.Web.Pipeline;
using Campfire.Web.ResponseCache;
using Campfire.Web.Routing;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>Users::AvatarsController</c> (reference/app/controllers/users/avatars_controller.rb): a
/// person's avatar by its token, the 512px WebP of their upload, the stock bot icon, or their
/// initials as SVG; and removing the signed-in person's own upload.
/// </summary>
public sealed class UsersAvatarsController : ApplicationController
{
    /// <summary>
    /// <c>ActionView::Digestor.digest</c> of <c>users/avatars/show.svg.erb</c>, which has no
    /// dependencies: <c>ActiveSupport::Digest.hexdigest("#{source}-")</c>.
    /// </summary>
    public const string ShowTemplateDigest = "d500db55e2a67222018ef0156839c3c9";

    // `rescue_from(ActiveSupport::MessageVerifier::InvalidSignature) { head :not_found }`: rescued,
    // so the after callbacks (the Sec-Fetch-Site Vary) don't run.
    static readonly ControllerCallbacks<UsersAvatarsController> Chain = Callbacks.For<UsersAvatarsController>()
        .RescueFrom<InvalidSignatureException>((c, _) => c.Head(404));

    public static readonly RequestDelegate Show = Action(Chain, c => c.ShowAsync());

    // `include ActiveStorage::Streaming`, which includes ActionController::Live.
    protected override bool IsLive => true;

    /// <summary><c>Current.user.avatar.destroy</c> (whatever user the URL names), then their profile.</summary>
    public static readonly RequestDelegate Destroy = Action(Chain, async c =>
    {
        var user = c.Current.User ?? throw new InvalidOperationException("undefined method 'avatar' for nil");
        await c.WriteAsync(tx => DetachAvatar(tx, c.App.RequireSeams().Jobs, user.Id, c.Now)).ConfigureAwait(false);
        c.App.Avatars.Evict(user.Id);
        c.RedirectTo(Routes.UserProfileUrl(c.UrlBase));
    });

    // `if stale?(etag: @user)`: cached publicly for half an hour, then the upload's variant, the
    // stock bot icon, or the initials. Thruster caches that response in the reference's image; the
    // app keeps what the database and the disk answered for the same 30 minutes (AvatarCache), so a
    // repeat needs neither. A request that answers 304 on a miss caches nothing: no body was made.
    async ValueTask ShowAsync()
    {
        var id = AvatarUserId() ?? throw new InvalidSignatureException();
        var avatars = App.Avatars;
        if (avatars.TryGet(id, out var cached))
        {
            if (IsStale(Validators(cached.Etag)))
            {
                Respond(cached);
            }
            return;
        }

        var loadedAt = avatars.Generation;
        var user = await ReadAsync(session => Users.Find(session, id)).ConfigureAwait(false)
            ?? throw new RecordNotFoundException($"Couldn't find User with 'id'={id}");
        var etag = CacheKeys.Record("users", user.Id, user.UpdatedAt);
        if (!IsStale(Validators(etag)))
        {
            return;
        }
        var avatar = await LoadAsync(user, etag).ConfigureAwait(false);
        avatars.Add(id, avatar, loadedAt);
        Respond(avatar);
    }

    // `User.from_avatar_token(params[:user_id])`: `find_signed!(sid, purpose: :avatar)`. Null for a bad
    // signature; a good one for a missing user is RecordNotFound.
    long? AvatarUserId() =>
        Params["user_id"] is string token ? TransferableUser.VerifyAvatarSignedId(App.Keys, token, Now) : null;

    Freshness Validators(string etag) => new() { Etag = etag, Template = TemplateForEtag() };

    // The body to send: `@user.avatar_variant`'s file, the stock bot icon, or `render formats: :svg`.
    async ValueTask<CachedAvatar> LoadAsync(User user, string etag)
    {
        if (await AvatarVariantAsync(user.Id).ConfigureAwait(false) is { } variant)
        {
            // `send_file path, content_type: "image/webp", disposition: :inline`.
            var path = App.RequireStorage().Service.PathFor(variant.Key);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"Cannot read file {path}");
            }
            return new CachedAvatar(etag, AvatarKind.Upload, Path.GetFileName(path), "image/webp", await File.ReadAllBytesAsync(path, RequestAborted).ConfigureAwait(false));
        }
        if (user.IsBot)
        {
            // `send_file "app/assets/images/default-bot-avatar.svg", content_type: "image/svg+xml", disposition: :inline`.
            const string filename = "default-bot-avatar.svg";
            var assets = App.RequireAssets();
            var bytes = assets.AssetPath(filename) is { } url && assets.Files.TryGetValue(url, out var file)
                ? file
                : throw new InvalidOperationException($"Cannot read file app/assets/images/{filename}");
            return new CachedAvatar(etag, AvatarKind.BotIcon, filename, "image/svg+xml", bytes);
        }
        // `render formats: :svg`: users/avatars/show.svg, with no layout.
        var buffer = new ArrayBufferWriter<byte>();
        View.UsersAvatarsShow(new HtmlWriter(buffer), user);
        return new CachedAvatar(etag, AvatarKind.Initials, "", MimeType.Svg.Value, buffer.WrittenMemory.ToArray());
    }

    void Respond(CachedAvatar avatar)
    {
        ExpiresIn(TimeSpan.FromMinutes(30), isPublic: true, staleWhileRevalidate: TimeSpan.FromDays(7));
        if (avatar.Kind == AvatarKind.Initials)
        {
            Render(avatar.Body, avatar.ContentType);
        }
        else
        {
            SendInline(avatar.FileName, avatar.ContentType, (stream, cancellationToken) => stream.WriteAsync(avatar.Body, cancellationToken).AsTask());
        }
    }

    // EtagWithTemplateDigest: `lookup_context.find_all(action_name, _prefixes)` finds show.svg.erb
    // only when the request's formats take SVG.
    string? TemplateForEtag() => Request.NegotiateMime([MimeType.Svg]) is null ? null : ShowTemplateDigest;

    // `@user.avatar_variant`: `avatar.variant(:square).processed if avatar.variable?`.
    async ValueTask<Blob?> AvatarVariantAsync(long userId)
    {
        var avatar = await ReadAsync(session => BlobRecords.FindAttachedBlob(session, User.ModelName, userId, AttachmentNames.Avatar)).ConfigureAwait(false);
        if (avatar is null || !avatar.IsVariable)
        {
            return null;
        }
        var storage = App.RequireStorage();
        var variant = Representable.Variant(avatar, NamedVariants.AvatarSquare);
        var processor = new VariantProcessor(App.Database, App.Clock);
        return await processor.ProcessedAsync(variant, new MediaProcessor(storage).TransformVariant, RequestAborted).ConfigureAwait(false);
    }

    // send_file_headers!: the disposition with the file's name, a binary transfer encoding, and
    // the content type with no charset.
    void SendInline(string filename, string contentType, Func<Stream, CancellationToken, Task> write)
    {
        Headers["Content-Disposition"] = ContentDisposition.Format("inline", filename);
        Headers["Content-Transfer-Encoding"] = "binary";
        SetContentType(contentType, charset: false);
        SendStream(write);
    }

    /// <summary>
    /// <c>user.avatar.destroy</c>: the attachment goes and touches the user (<c>belongs_to :record,
    /// touch: true</c>), and its blob is purged later (<c>dependent: :purge_later</c>). Nothing
    /// when there's no avatar.
    /// </summary>
    internal static void DetachAvatar(WriteTransaction tx, IJobQueue jobs, long userId, DateTimeOffset now)
    {
        if (BlobRecords.FindAttachment(tx.Session, User.ModelName, userId, AttachmentNames.Avatar) is { } attachment)
        {
            BlobRecords.DeleteAttachment(tx.Session, attachment.Id);
            ActiveStoragePurge.Later(tx, jobs, attachment.BlobId);
            tx.Session.Execute("""UPDATE "users" SET "updated_at" = @now WHERE "users"."id" = @id""",
                ("@now", ActiveRecordTime.ToDb(now)), ("@id", userId));
        }
    }
}

/// <summary><c>ActiveSupport::MessageVerifier::InvalidSignature</c> from <c>find_signed!</c>.</summary>
sealed class InvalidSignatureException : Exception;
