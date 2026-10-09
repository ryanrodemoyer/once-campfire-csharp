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

    static readonly ControllerCallbacks<UsersAvatarsController> Chain = Callbacks.For<UsersAvatarsController>();

    public static readonly RequestDelegate Show = Action(Chain, c => c.ShowAsync());

    // `include ActiveStorage::Streaming`, which includes ActionController::Live.
    protected override bool IsLive => true;

    /// <summary><c>Current.user.avatar.destroy</c> (whatever user the URL names), then their profile.</summary>
    public static readonly RequestDelegate Destroy = Action(Chain, async c =>
    {
        var user = c.Current.User ?? throw new InvalidOperationException("undefined method 'avatar' for nil");
        await c.WriteAsync(tx => DetachAvatar(tx, user.Id, c.Now)).ConfigureAwait(false);
        c.RedirectTo(Routes.UserProfileUrl(c.UrlBase));
    });

    // `if stale?(etag: @user)`: cached publicly for half an hour, then the upload's variant, the
    // stock bot icon, or the initials.
    async ValueTask ShowAsync()
    {
        if (await FromAvatarTokenAsync().ConfigureAwait(false) is not { } user)
        {
            // `rescue_from(ActiveSupport::MessageVerifier::InvalidSignature) { head :not_found }`
            Head(404);
            return;
        }
        if (!IsStale(new Freshness { Etag = CacheKeys.Record("users", user.Id, user.UpdatedAt), Template = TemplateForEtag() }))
        {
            return;
        }
        ExpiresIn(TimeSpan.FromMinutes(30), isPublic: true, staleWhileRevalidate: TimeSpan.FromDays(7));

        if (await AvatarVariantAsync(user.Id).ConfigureAwait(false) is { } variant)
        {
            SendWebpBlobFile(App.RequireStorage().Service.PathFor(variant.Key));
        }
        else if (user.IsBot)
        {
            RenderDefaultBot();
        }
        else
        {
            RenderInitials(user);
        }
    }

    // `User.from_avatar_token(params[:user_id])`: `find_signed!(sid, purpose: :avatar)`. Null for
    // a bad signature; a good one for a missing user is RecordNotFound.
    async ValueTask<User?> FromAvatarTokenAsync()
    {
        if (Params["user_id"] is not string token || TransferableUser.VerifyAvatarSignedId(App.Keys, token, Now) is not { } id)
        {
            return null;
        }
        return await ReadAsync(session => Users.Find(session, id)).ConfigureAwait(false)
            ?? throw new RecordNotFoundException($"Couldn't find User with 'id'={id}");
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

    // `send_file path, content_type: "image/webp", disposition: :inline`.
    void SendWebpBlobFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Cannot read file {path}");
        }
        SendInline(Path.GetFileName(path), "image/webp", async (stream, cancellationToken) =>
        {
            await using var file = File.OpenRead(path);
            await file.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        });
    }

    // `send_file "app/assets/images/default-bot-avatar.svg", content_type: "image/svg+xml", disposition: :inline`.
    void RenderDefaultBot()
    {
        const string filename = "default-bot-avatar.svg";
        var assets = App.RequireAssets();
        var bytes = assets.AssetPath(filename) is { } url && assets.Files.TryGetValue(url, out var file)
            ? file
            : throw new InvalidOperationException($"Cannot read file app/assets/images/{filename}");
        SendInline(filename, "image/svg+xml", (stream, cancellationToken) => stream.WriteAsync(bytes, cancellationToken).AsTask());
    }

    // `render formats: :svg`: users/avatars/show.svg, with no layout.
    void RenderInitials(User user) =>
        Render(MimeType.Svg, buffer => View.UsersAvatarsShow(new HtmlWriter(buffer), user));

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
    /// touch: true</c>); its blob is left for <c>ActiveStorage::PurgeJob</c>. Nothing when there's
    /// no avatar.
    /// </summary>
    internal static void DetachAvatar(WriteTransaction tx, long userId, DateTimeOffset now)
    {
        if (BlobRecords.FindAttachment(tx.Session, User.ModelName, userId, AttachmentNames.Avatar) is { } attachment)
        {
            BlobRecords.DeleteAttachment(tx.Session, attachment.Id);
            tx.Session.Execute("""UPDATE "users" SET "updated_at" = @now WHERE "users"."id" = @id""",
                ("@now", ActiveRecordTime.ToDb(now)), ("@id", userId));
        }
    }
}
