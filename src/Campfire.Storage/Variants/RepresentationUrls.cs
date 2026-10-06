using System.Collections.Concurrent;
using Campfire.Storage.Blobs;

namespace Campfire.Storage.Variants;

/// <summary>
/// <c>url_for(representation)</c> and <c>rails_storage_proxy_path(representation)</c>: the signed blob
/// id, the signed variation key and the original blob's filename. Nothing is processed.
/// </summary>
public sealed class RepresentationUrls(BlobStorage storage)
{
    // A key depends only on the transformations, which the digest names exactly, and the app asks for
    // a handful of them, so each is signed once.
    readonly ConcurrentDictionary<string, string> keys = new(StringComparer.Ordinal);

    /// <summary><c>/rails/active_storage/representations/redirect/:signed_blob_id/:variation_key/*filename</c>.</summary>
    public string RedirectPath(IRepresentation representation)
    {
        ArgumentNullException.ThrowIfNull(representation);
        return storage.Urls.RepresentationRedirectPath(representation.Blob, Key(representation.Variation));
    }

    /// <summary><c>/rails/active_storage/representations/proxy/:signed_blob_id/:variation_key/*filename</c>.</summary>
    public string ProxyPath(IRepresentation representation)
    {
        ArgumentNullException.ThrowIfNull(representation);
        return storage.Urls.RepresentationProxyPath(representation.Blob, Key(representation.Variation));
    }

    /// <summary><c>variation.key</c>.</summary>
    public string Key(Variation variation)
    {
        ArgumentNullException.ThrowIfNull(variation);
        return keys.GetOrAdd(variation.Digest, static (_, v) => v.variation.Key(v.storage.Verifier), (variation, storage));
    }
}
