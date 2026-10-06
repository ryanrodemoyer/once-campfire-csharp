using Microsoft.Data.Sqlite;

namespace Campfire.Data.Records;

// A row of `active_storage_blobs`: a stored file. `metadata` is JSON text (analyzed, width,
// height, ...); `key` names the file under storage/.
public sealed record ActiveStorageBlob(
    long Id,
    string Key,
    string Filename,
    string? ContentType,
    string? Metadata,
    string ServiceName,
    long ByteSize,
    string? Checksum,
    DateTimeOffset CreatedAt)
{
    public const string ModelName = "ActiveStorage::Blob";

    internal const string Columns = """
        "active_storage_blobs"."id", "active_storage_blobs"."key", "active_storage_blobs"."filename", "active_storage_blobs"."content_type", "active_storage_blobs"."metadata", "active_storage_blobs"."service_name", "active_storage_blobs"."byte_size", "active_storage_blobs"."checksum", "active_storage_blobs"."created_at"
        """;

    internal static ActiveStorageBlob Read(SqliteDataReader reader) => ReadAt(reader, 0);

    internal static ActiveStorageBlob ReadAt(SqliteDataReader reader, int at) => new(
        reader.GetInt64(at + 0),
        reader.GetString(at + 1),
        reader.GetString(at + 2),
        Db.ReadNullableString(reader, at + 3),
        Db.ReadNullableString(reader, at + 4),
        reader.GetString(at + 5),
        reader.GetInt64(at + 6),
        Db.ReadNullableString(reader, at + 7),
        Db.ReadTime(reader, at + 8));
}

// A row of `active_storage_attachments`: joins a blob to the record it's attached to, under the
// attachment's name (see RecordTypes and AttachmentNames).
public sealed record ActiveStorageAttachment(
    long Id,
    string Name,
    string RecordType,
    long RecordId,
    long BlobId,
    DateTimeOffset CreatedAt)
{
    internal const string Columns = """
        "active_storage_attachments"."id", "active_storage_attachments"."name", "active_storage_attachments"."record_type", "active_storage_attachments"."record_id", "active_storage_attachments"."blob_id", "active_storage_attachments"."created_at"
        """;

    internal static ActiveStorageAttachment Read(SqliteDataReader reader) => ReadAt(reader, 0);

    internal static ActiveStorageAttachment ReadAt(SqliteDataReader reader, int at) => new(
        reader.GetInt64(at + 0),
        reader.GetString(at + 1),
        reader.GetString(at + 2),
        reader.GetInt64(at + 3),
        reader.GetInt64(at + 4),
        Db.ReadTime(reader, at + 5));
}

// A row of `active_storage_variant_records`: a processed variant of a blob, whose file is the
// variant record's `image` attachment.
public sealed record ActiveStorageVariantRecord(
    long Id,
    long BlobId,
    string VariationDigest)
{
    public const string ModelName = "ActiveStorage::VariantRecord";

    internal const string Columns = """
        "active_storage_variant_records"."id", "active_storage_variant_records"."blob_id", "active_storage_variant_records"."variation_digest"
        """;

    internal static ActiveStorageVariantRecord Read(SqliteDataReader reader) => ReadAt(reader, 0);

    internal static ActiveStorageVariantRecord ReadAt(SqliteDataReader reader, int at) => new(
        reader.GetInt64(at + 0),
        reader.GetInt64(at + 1),
        reader.GetString(at + 2));
}
