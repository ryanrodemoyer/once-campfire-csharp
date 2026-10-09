using Campfire.Web.Pipeline;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>ActiveStorage::Blobs::RedirectController#show</c>: <c>expires_in 5.minutes</c> and
/// <c>redirect_to @blob.url(disposition:), allow_other_host: true</c>.
/// </summary>
public sealed class ActiveStorageBlobsRedirectController : ActiveStorageController
{
    static readonly ControllerCallbacks<ActiveStorageBlobsRedirectController> Chain = BaseCallbacks
        .For<ActiveStorageBlobsRedirectController>()
        .Before("set_blob", c => c.SetBlobAsync());

    public static readonly RequestDelegate Show = Action(Chain, c => c.ShowAsync());

    ValueTask ShowAsync()
    {
        ExpiresIn(ServiceUrlLifetime);
        RedirectTo(ServiceUrl(CurrentBlob!, DispositionParam), allowOtherHost: true);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// <c>ActiveStorage::Blobs::ProxyController#show</c>. A <c>Range</c> header (present, so not blank)
/// is served as byte ranges; otherwise the whole file is cached forever and streamed.
/// <c>include ActiveStorage::Streaming</c>, so the response is live: no default security headers.
/// </summary>
public sealed class ActiveStorageBlobsProxyController : ActiveStorageController
{
    static readonly ControllerCallbacks<ActiveStorageBlobsProxyController> Chain = BaseCallbacks
        .For<ActiveStorageBlobsProxyController>()
        .Before("set_blob", c => c.SetBlobAsync());

    public static readonly RequestDelegate Show = Action(Chain, c => c.ShowAsync());

    protected override bool IsLive => true;

    ValueTask ShowAsync()
    {
        var blob = CurrentBlob!;
        // `request.headers["Range"].present?`: nil and blank are the full-file path.
        if (Header("Range") is { } range && range.Trim().Length > 0)
        {
            BlobStreams.SendBlobByteRange(this, blob, range, DispositionParam);
            return ValueTask.CompletedTask;
        }
        if (BlobStreams.FreshForever(this))
        {
            return ValueTask.CompletedTask;
        }
        Headers["Accept-Ranges"] = "bytes";
        BlobStreams.SendBlobStream(this, blob, DispositionParam, rememberLength: true);
        return ValueTask.CompletedTask;
    }
}
