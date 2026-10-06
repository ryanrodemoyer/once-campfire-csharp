using System.Text.Json.Nodes;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.Ruby;

namespace Campfire.Storage.Blobs;

/// <summary>
/// Blob signed ids and the Active Storage route helpers (activestorage/config/routes.rb).
/// <c>rails_blob_path</c> and <c>url_for(representation)</c> go through
/// <c>resolve_model_to_route = :rails_storage_redirect</c>. Campfire leaves <c>urls_expire_in</c>
/// unset, so these signed ids never expire.
/// </summary>
public sealed class BlobUrls(MessageVerifier verifier)
{
    public const string Prefix = "/rails/active_storage";

    /// <summary>
    /// <c>blob.signed_id</c>: <c>ActiveStorage.verifier</c> with purpose <c>blob_id</c>. Blob
    /// overrides both <c>signed_id_verifier</c> and <c>combine_signed_id_purposes</c>, so this is not
    /// the Active Record signed id scheme.
    /// </summary>
    public string SignedId(long blobId, DateTimeOffset? expiresAt = null) => verifier.Generate(JsonValue.Create(blobId), "blob_id", expiresAt);

    /// <summary>The id half of <c>ActiveStorage::Blob.find_signed(signed_id)</c>, or null.</summary>
    public long? VerifySignedId(string signedId, DateTimeOffset now)
    {
        var result = verifier.Verify(signedId, "blob_id", now);
        return result.IsValid && result.Value is JsonValue value && value.TryGetValue<long>(out var id) ? id : null;
    }

    /// <summary><c>rails_blob_path(blob, disposition:)</c>: <c>/rails/active_storage/blobs/redirect/:signed_id/*filename</c>.</summary>
    public string BlobRedirectPath(Blob blob, string? disposition = null) => BlobPath("redirect", blob, disposition);

    /// <summary><c>rails_storage_proxy_path(blob)</c>: <c>/rails/active_storage/blobs/proxy/:signed_id/*filename</c>.</summary>
    public string BlobProxyPath(Blob blob, string? disposition = null) => BlobPath("proxy", blob, disposition);

    /// <summary>
    /// <c>url_for(variant)</c> / <c>url_for(preview)</c>:
    /// <c>/rails/active_storage/representations/redirect/:signed_blob_id/:variation_key/*filename</c>.
    /// <paramref name="blob"/> is the original blob (the video, for a preview); the variation key is S02's.
    /// </summary>
    public string RepresentationRedirectPath(Blob blob, string variationKey) => RepresentationPath("redirect", blob, variationKey);

    /// <summary><c>rails_storage_proxy_path(representation)</c>.</summary>
    public string RepresentationProxyPath(Blob blob, string variationKey) => RepresentationPath("proxy", blob, variationKey);

    string BlobPath(string kind, Blob blob, string? disposition)
    {
        ArgumentNullException.ThrowIfNull(blob);
        var path = $"{Prefix}/blobs/{kind}/{RouteEscaping.EscapeSegment(SignedId(blob.Id))}/{RouteEscaping.EscapePath(blob.Filename.Sanitized)}";
        // Extra route options become the query string through Hash#to_query (CGI.escape).
        return disposition is null ? path : $"{path}?disposition={RubyEscape.CgiEscape(disposition)}";
    }

    string RepresentationPath(string kind, Blob blob, string variationKey)
    {
        ArgumentNullException.ThrowIfNull(blob);
        return $"{Prefix}/representations/{kind}/{RouteEscaping.EscapeSegment(SignedId(blob.Id))}/" +
            $"{RouteEscaping.EscapeSegment(variationKey)}/{RouteEscaping.EscapePath(blob.Filename.Sanitized)}";
    }
}
