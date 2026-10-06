using System.Text.Json.Nodes;

namespace Campfire.Storage.Blobs;

/// <summary>A blob built but not yet saved (<c>Blob.build_after_unfurling</c>): no id or <c>created_at</c> yet.</summary>
public sealed record NewBlob(
    string Key,
    Filename Filename,
    string? ContentType,
    JsonObject Metadata,
    string ServiceName,
    long ByteSize,
    string? Checksum);
