using Campfire.Web.Pipeline;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>ActiveStorage::Representations::RedirectController#show</c>: the processed image's
/// <c>url(disposition:)</c>, cached for five minutes. A <c>Range</c> header is not read.
/// </summary>
public sealed class ActiveStorageRepresentationsRedirectController : ActiveStorageRepresentationsController
{
    static readonly ControllerCallbacks<ActiveStorageRepresentationsRedirectController> Chain = BaseCallbacks
        .For<ActiveStorageRepresentationsRedirectController>()
        .Before("set_blob", c => c.SetBlobAsync())
        .Before("set_representation", c => c.SetRepresentationAsync());

    public static readonly RequestDelegate Show = Action(Chain, c => c.ShowAsync());

    ValueTask ShowAsync()
    {
        ExpiresIn(ServiceUrlLifetime);
        RedirectTo(ServiceUrl(CurrentImage!, DispositionParam), allowOtherHost: true);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// <c>ActiveStorage::Representations::ProxyController#show</c>: <c>http_cache_forever public: true</c>
/// and <c>send_blob_stream</c> of the processed image. No <c>Range</c> handling and no
/// <c>Accept-Ranges</c> (this Rails version only ranges the blob proxy and the disk service).
/// </summary>
public sealed class ActiveStorageRepresentationsProxyController : ActiveStorageRepresentationsController
{
    static readonly ControllerCallbacks<ActiveStorageRepresentationsProxyController> Chain = BaseCallbacks
        .For<ActiveStorageRepresentationsProxyController>()
        .Before("set_blob", c => c.SetBlobAsync())
        .Before("set_representation", c => c.SetRepresentationAsync());

    public static readonly RequestDelegate Show = Action(Chain, c => c.ShowAsync());

    protected override bool IsLive => true;

    ValueTask ShowAsync()
    {
        if (BlobStreams.FreshForever(this))
        {
            return ValueTask.CompletedTask;
        }
        BlobStreams.SendBlobStream(this, CurrentImage!, DispositionParam, rememberLength: false);
        return ValueTask.CompletedTask;
    }
}
