using Campfire.Data.Events;
using Campfire.Storage.Blobs;
using Campfire.Web.Assets;

namespace Campfire.Web.Pipeline;

// What pages and message writes need beyond the pipeline's own configuration. The server sets
// them when it builds the app; a controller that renders or writes messages fails without them.
public sealed partial class WebApp
{
    /// <summary>The asset manifest pages render against (<c>asset_path</c>, the layout's tags).</summary>
    public AssetBundle? Assets { get; init; }

    /// <summary>Active Storage, for attachment and representation URLs.</summary>
    public BlobStorage? Storage { get; init; }

    /// <summary>
    /// Where domain events go: Action Cable broadcasts, enqueued jobs and connection resets
    /// (D03's seams).
    /// </summary>
    public DomainSeams? Seams { get; init; }

    /// <summary><c>Rails.configuration.x.vapid.public_key</c> (<c>VAPID_PUBLIC_KEY</c>), for the layout.</summary>
    public string? VapidPublicKey { get; init; }

    internal AssetBundle RequireAssets() => Assets ?? throw new InvalidOperationException("WebApp.Assets isn't set");

    internal BlobStorage RequireStorage() => Storage ?? throw new InvalidOperationException("WebApp.Storage isn't set");

    internal DomainSeams RequireSeams() => Seams ?? throw new InvalidOperationException("WebApp.Seams isn't set");
}
