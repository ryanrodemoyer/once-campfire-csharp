using System.Text.Json.Nodes;

namespace Campfire.Storage.Blobs;

/// <summary>
/// An <c>active_storage_blobs</c> row. <see cref="Metadata"/> is the <c>metadata</c> column
/// (<c>store :metadata, coder: JSON</c>) with its key order kept.
/// </summary>
public sealed record Blob(
    long Id,
    string Key,
    Filename Filename,
    string? ContentType,
    JsonObject Metadata,
    string ServiceName,
    long ByteSize,
    string? Checksum,
    DateTimeOffset CreatedAt)
{
    string Type => ContentType ?? "";

    public bool IsImage => Type.StartsWith("image", StringComparison.Ordinal);

    public bool IsVideo => Type.StartsWith("video", StringComparison.Ordinal);

    public bool IsAudio => Type.StartsWith("audio", StringComparison.Ordinal);

    public bool IsText => Type.StartsWith("text", StringComparison.Ordinal);

    /// <summary><c>variable?</c></summary>
    public bool IsVariable => BlobContentTypes.Variable.Contains(Type);

    /// <summary><c>web_image?</c></summary>
    public bool IsWebImage => BlobContentTypes.WebImage.Contains(Type);

    public bool IsAnalyzed => IsSet("analyzed");

    public bool IsIdentified => IsSet("identified");

    /// <summary><c>composed</c>: a composed blob has no checksum to verify.</summary>
    public bool IsComposed => IsSet("composed");

    /// <summary><c>content_type_for_serving</c> (Blob::Servable).</summary>
    public string? ContentTypeForServing => ForciblyServeAsBinary ? BlobContentTypes.Binary : ContentType;

    /// <summary><c>forced_disposition_for_serving</c>: <c>attachment</c>, or null to keep the asked one.</summary>
    public string? ForcedDispositionForServing => ForciblyServeAsBinary || !BlobContentTypes.AllowedInline.Contains(Type) ? "attachment" : null;

    bool ForciblyServeAsBinary => BlobContentTypes.ServeAsBinary.Contains(Type);

    // A store accessor's truthiness: anything but nil and false.
    bool IsSet(string name) => Metadata[name] is { } value && !(value is JsonValue v && v.TryGetValue<bool>(out var b) && !b);
}

/// <summary>An <c>active_storage_attachments</c> row.</summary>
public sealed record Attachment(long Id, string Name, string RecordType, long RecordId, long BlobId, DateTimeOffset CreatedAt);
