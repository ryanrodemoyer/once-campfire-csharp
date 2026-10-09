using Campfire.Storage.Blobs;
using Campfire.Storage.Media;
using Campfire.Storage.Variants;
using Campfire.Web.Pipeline;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>ActiveStorage::BaseController</c> (activestorage/app/controllers/active_storage/base_controller.rb):
/// <c>ActionController::Base</c>, so <see cref="Controller.BaseCallbacks"/> is the whole chain.
/// Campfire's <see cref="ApplicationController"/> concerns do not run. Downloads stay public;
/// <c>require_active_storage_authentication</c> (reference/app/controllers/concerns/active_storage_authentication.rb)
/// guards only the direct-upload writers.
/// </summary>
public abstract class ActiveStorageController : Controller
{
    /// <summary><c>ActiveStorage.service_urls_expire_in</c>, five minutes.</summary>
    protected static readonly TimeSpan ServiceUrlLifetime = TimeSpan.FromMinutes(5);

    protected Blob? CurrentBlob { get; set; }

    protected BlobStorage Storage => App.RequireStorage();

    /// <summary><c>ActiveStorageAuthentication#require_active_storage_authentication</c>.</summary>
    protected async ValueTask RequireAuthenticationAsync()
    {
        if (await FindSessionByCookieAsync().ConfigureAwait(false) is null)
        {
            Head(401);
        }
    }

    /// <summary>
    /// <c>ActiveStorage::SetBlob#set_blob</c>: <c>Blob.find_signed!(params[:signed_blob_id] || params[:signed_id])</c>.
    /// A bad signature is <c>head :not_found</c> (from this before callback, so <c>text/html</c>). A good
    /// signature for a missing row is <c>ActiveRecord::RecordNotFound</c>.
    /// </summary>
    protected async ValueTask SetBlobAsync()
    {
        var signedId = Params["signed_blob_id"] as string ?? Params["signed_id"] as string ?? "";
        if (Storage.Urls.VerifySignedId(signedId, Now) is not { } id)
        {
            Head(404);
            return;
        }
        CurrentBlob = await ReadAsync(session => BlobRecords.FindBlob(session, id)).ConfigureAwait(false)
            ?? throw new RecordNotFoundException($"Couldn't find ActiveStorage::Blob with 'id'={id}");
    }

    /// <summary>
    /// <c>blob.url(disposition:)</c> on the disk service: this request's base URL, expiring in
    /// <see cref="ServiceUrlLifetime"/>.
    /// </summary>
    protected string ServiceUrl(Blob blob, string? disposition) =>
        Storage.Url(blob, RequestUrl.BaseUrl, Now.Add(ServiceUrlLifetime), disposition);

    /// <summary><c>params[:disposition]</c>, nil when the query omits it (then <c>inline</c>).</summary>
    protected string? DispositionParam => Params["disposition"] as string;
}

/// <summary>
/// <c>ActiveStorage::Representations::BaseController</c>: the blob, then
/// <c>@blob.representation(params[:variation_key]).processed</c>. A bad variation key is
/// <c>head :not_found</c>. Anything else (no representable image, a bad format) propagates.
/// </summary>
public abstract class ActiveStorageRepresentationsController : ActiveStorageController
{
    protected Blob? CurrentImage { get; set; }

    protected async ValueTask SetRepresentationAsync()
    {
        var key = Params["variation_key"] as string ?? "";
        if (Variation.Decode(Storage.Verifier, key, Now) is not { } variation)
        {
            Head(404);
            return;
        }
        var representation = Representable.Representation(CurrentBlob!, variation);
        var processor = new VariantProcessor(App.Database, App.Clock);
        var media = new MediaProcessor(Storage);
        CurrentImage = representation switch
        {
            Preview preview => await processor.ProcessedAsync(preview, media.RenderPreviewImage, media.TransformVariant, RequestAborted).ConfigureAwait(false),
            VariantWithRecord variant => await processor.ProcessedAsync(variant, media.TransformVariant, RequestAborted).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"No previewer found and can't transform blob with ID={CurrentBlob!.Id}"),
        } ?? throw new InvalidOperationException("undefined method `url' for nil:NilClass");
    }
}
