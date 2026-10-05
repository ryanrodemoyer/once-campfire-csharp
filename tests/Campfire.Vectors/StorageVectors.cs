using System.Text.Json;

namespace Campfire.Vectors;

// vectors/storage.json and vectors/storage/*, written by reference-tools/storage/generate.rb:
// Active Storage verifiers, variation keys, Marcel content types, filename sanitizing, and the
// blobs, attachments and variant files Rails creates for message attachments, avatars and logos.

public sealed record StorageFile(
    StorageVerifierVectors Verifier,
    string GeneratedBy,
    StorageToolVersions Versions,
    string VideoPreviewArguments,
    string SecretKeyBasePrefix,
    IReadOnlyList<VariationCase> Variations,
    IReadOnlyList<MarcelCase> Marcel,
    IReadOnlyList<FilenameCase> Filenames,
    IReadOnlyList<StoredMessageAttachment> Messages,
    IReadOnlyList<StoredImage> Avatars,
    IReadOnlyList<StoredImage> Logos);

public sealed record StorageVerifierVectors(
    string Expiring, string DiskUrlPath, string DiskUrlPathNilType, string DirectUploadPath);

public sealed record StorageToolVersions(
    string Libvips, string RubyVips, string ImageProcessing, string Marcel, string Ffmpeg, string Ffprobe, string Rails, string Ruby);

/// <summary>
/// An ActiveStorage::Variation. <c>Typed</c> and <c>DecodedTyped</c> spell out the Ruby hash with
/// symbol and string keys tagged (<c>{"hash": [[key, value], ...]}</c>, <c>{"sym": ...}</c>).
/// </summary>
public sealed record VariationCase(
    string Inspect,
    JsonElement Typed,
    JsonElement DecodedTyped,
    string MarshalHex,
    string Digest,
    string Key,
    string DecodedInspect,
    string DecodedMarshalHex,
    string DecodedDigest);

/// <summary>Marcel's content type for a name and either inline bytes or a fixture file.</summary>
public sealed record MarcelCase(string Name, string? DataHex, string? Fixture, string? DeclaredType, string ContentType);

public sealed record FilenameCase(
    string InputHex, string Sanitized, string BaseHex, string ExtensionHex, string Inline, string Attachment, string EscapedPath);

public sealed record StoredBlob(
    long Id, string Key, string Filename, string ContentType, string Metadata, string ServiceName, long ByteSize, string Checksum, string Path);

public sealed record StoredAttachment(string Name, string RecordType, long RecordId, long BlobId);

/// <summary>
/// A processed variant. <c>VariantRecord</c> is the [blob_id, variation_digest] row; <c>File</c>
/// names its bytes under vectors/storage/.
/// </summary>
public sealed record StoredVariant(
    string Label,
    long SourceBlobId,
    string TransformationsInspect,
    JsonElement TransformationsTyped,
    string VariationDigest,
    JsonElement VariantRecord,
    StoredBlob Blob,
    StoredAttachment Attachment,
    string File);

public sealed record StoredPreviewImage(StoredBlob Blob, StoredAttachment Attachment, string File);

public sealed record StoredMessageAttachment(
    string Fixture,
    string DeclaredType,
    StoredBlob Blob,
    StoredAttachment Attachment,
    bool Variable,
    bool Previewable,
    string RailsBlobPath,
    string RailsBlobDownloadPath,
    string RailsBlobProxyPath,
    string ServiceUrl,
    string ServiceUrlAttachment,
    string? ThumbPath = null,
    string? ThumbProxyPath = null,
    IReadOnlyList<StoredVariant>? Variants = null,
    StoredPreviewImage? PreviewImage = null,
    string? PosterPath = null,
    string? PosterProxyPath = null);

/// <summary>An avatar or account logo and its variants.</summary>
public sealed record StoredImage(
    string Fixture, StoredBlob Blob, IReadOnlyList<StoredVariant> Variants, string? Path = null);

public static class StorageVectors
{
    static readonly Lazy<StorageFile> Data = new(() => VectorFiles.Load<StorageFile>("storage.json"));

    public static StorageFile File => Data.Value;

    public static TheoryData<VariationCase> Variations() => VectorFiles.Rows(File.Variations, c => c.Inspect);
    public static TheoryData<MarcelCase> Marcel() => VectorFiles.Rows(File.Marcel, c => c.Name);
    public static TheoryData<FilenameCase> Filenames() => VectorFiles.Rows(File.Filenames, c => c.Sanitized);
    public static TheoryData<StoredMessageAttachment> Messages() => VectorFiles.Rows(File.Messages, c => c.Fixture);
    public static TheoryData<StoredImage> Avatars() => VectorFiles.Rows(File.Avatars, c => c.Fixture);
    public static TheoryData<StoredImage> Logos() => VectorFiles.Rows(File.Logos, c => c.Fixture);

    /// <summary>Every variant (and preview image) file, keyed by its name under vectors/storage/.</summary>
    public static TheoryData<StoredVariant> Variants() => VectorFiles.Rows(
        File.Messages.SelectMany(m => m.Variants ?? []).Concat(File.Avatars.Concat(File.Logos).SelectMany(i => i.Variants)),
        v => v.Label);

    /// <summary>The bytes Rails wrote for a variant or preview, from vectors/storage/.</summary>
    public static byte[] ReadFile(string name) => VectorFiles.ReadBytes(System.IO.Path.Combine("storage", name));
}
