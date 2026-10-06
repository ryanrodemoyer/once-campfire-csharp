using Campfire.Storage.Blobs;
using Campfire.Web.Assets;

namespace Campfire.Web.Pipeline;

// What pages need beyond the pipeline's own configuration. The server sets them when it builds
// the app; a controller that renders a page fails without them.
public sealed partial class WebApp
{
    /// <summary>The asset manifest pages render against (<c>asset_path</c>, the layout's tags).</summary>
    public AssetBundle? Assets { get; init; }

    /// <summary>Active Storage, for attachment and representation URLs.</summary>
    public BlobStorage? Storage { get; init; }

    /// <summary><c>Rails.configuration.x.vapid.public_key</c> (<c>VAPID_PUBLIC_KEY</c>), for the layout.</summary>
    public string? VapidPublicKey { get; init; }
}
