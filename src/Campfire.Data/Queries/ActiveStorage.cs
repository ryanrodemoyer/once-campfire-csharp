using Campfire.Data.Records;
using Campfire.Data.Sqlite;

namespace Campfire.Data.Queries;

// `active_storage_blobs` rows. Uploading, analyzing and serving them is the S lane's.
public static class Blobs
{
    const string select = $"SELECT {ActiveStorageBlob.Columns} FROM \"active_storage_blobs\"";

    public static ActiveStorageBlob? Find(SqliteSession session, long id) =>
        Sql.One(session, $"{select} WHERE \"active_storage_blobs\".\"id\" = @id LIMIT 1", ActiveStorageBlob.Read, ("@id", id));

    public static ActiveStorageBlob? FindByKey(SqliteSession session, string key) =>
        Sql.One(session, $"{select} WHERE \"active_storage_blobs\".\"key\" = @key LIMIT 1", ActiveStorageBlob.Read, ("@key", key));

    public static List<ActiveStorageBlob> WhereIds(SqliteSession session, IReadOnlyList<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return [];
        }
        var (placeholders, parameters) = Sql.List("id", ids);
        return session.Query($"{select} WHERE \"active_storage_blobs\".\"id\" IN ({placeholders})", ActiveStorageBlob.Read, parameters);
    }

    public static ActiveStorageBlob Create(
        SqliteSession session,
        string key,
        string filename,
        string? contentType,
        string? metadata,
        string serviceName,
        long byteSize,
        string? checksum,
        DateTimeOffset now)
    {
        var id = Sql.Insert(session, """
            INSERT INTO "active_storage_blobs" ("byte_size", "checksum", "content_type", "created_at", "filename", "key", "metadata", "service_name") VALUES (@byte_size, @checksum, @content_type, @now, @filename, @key, @metadata, @service_name) RETURNING "id"
            """,
            ("@byte_size", byteSize), ("@checksum", checksum), ("@content_type", contentType), ("@now", Db.Time(now)),
            ("@filename", filename), ("@key", key), ("@metadata", metadata), ("@service_name", serviceName));
        return Find(session, id)!;
    }

    // `update!(metadata:)` after analysis. Blobs have no `updated_at`.
    public static void UpdateMetadata(SqliteSession session, long id, string? metadata) =>
        session.Execute("UPDATE \"active_storage_blobs\" SET \"metadata\" = @metadata WHERE \"active_storage_blobs\".\"id\" = @id", ("@metadata", metadata), ("@id", id));

    public static void Delete(SqliteSession session, long id) => Sql.Delete(session, "active_storage_blobs", id);
}

// `active_storage_attachments` rows.
public static class Attachments
{
    const string select = $"SELECT {ActiveStorageAttachment.Columns} FROM \"active_storage_attachments\"";

    public static ActiveStorageAttachment? Find(SqliteSession session, long id) =>
        Sql.One(session, $"{select} WHERE \"active_storage_attachments\".\"id\" = @id LIMIT 1", ActiveStorageAttachment.Read, ("@id", id));

    // A `has_one_attached` attachment: `user.avatar_attachment`, `message.attachment_attachment`.
    public static ActiveStorageAttachment? For(SqliteSession session, string recordType, long recordId, string name) =>
        Sql.One(session,
            $"{select} WHERE \"active_storage_attachments\".\"record_id\" = @record_id AND \"active_storage_attachments\".\"record_type\" = @record_type AND \"active_storage_attachments\".\"name\" = @name LIMIT 1",
            ActiveStorageAttachment.Read,
            ("@record_id", recordId), ("@record_type", recordType), ("@name", name));

    // A `has_many_attached` attachment's rows (`rich_text.embeds_attachments`), in id order.
    public static List<ActiveStorageAttachment> AllFor(SqliteSession session, string recordType, long recordId, string name) =>
        session.Query(
            $"{select} WHERE \"active_storage_attachments\".\"record_id\" = @record_id AND \"active_storage_attachments\".\"record_type\" = @record_type AND \"active_storage_attachments\".\"name\" = @name",
            ActiveStorageAttachment.Read,
            ("@record_id", recordId), ("@record_type", recordType), ("@name", name));

    // The attachments of several records, as `with_attached_*` preloads them.
    public static List<ActiveStorageAttachment> ForRecords(SqliteSession session, string recordType, string name, IReadOnlyList<long> recordIds)
    {
        ArgumentNullException.ThrowIfNull(recordIds);
        if (recordIds.Count == 0)
        {
            return [];
        }
        var (placeholders, parameters) = Sql.List("record_id", recordIds);
        return session.Query(
            $"{select} WHERE \"active_storage_attachments\".\"record_type\" = @record_type AND \"active_storage_attachments\".\"name\" = @name AND \"active_storage_attachments\".\"record_id\" IN ({placeholders})",
            ActiveStorageAttachment.Read,
            [("@record_type", recordType), ("@name", name), .. parameters]);
    }

    // The attachments of a blob, which purging checks before deleting the file.
    public static List<ActiveStorageAttachment> ForBlob(SqliteSession session, long blobId) =>
        session.Query($"{select} WHERE \"active_storage_attachments\".\"blob_id\" = @blob_id", ActiveStorageAttachment.Read, ("@blob_id", blobId));

    public static ActiveStorageAttachment Create(SqliteSession session, string recordType, long recordId, string name, long blobId, DateTimeOffset now)
    {
        var id = Sql.Insert(session, """
            INSERT INTO "active_storage_attachments" ("blob_id", "created_at", "name", "record_id", "record_type") VALUES (@blob_id, @now, @name, @record_id, @record_type) RETURNING "id"
            """, ("@blob_id", blobId), ("@now", Db.Time(now)), ("@name", name), ("@record_id", recordId), ("@record_type", recordType));
        return Find(session, id)!;
    }

    public static void Delete(SqliteSession session, long id) => Sql.Delete(session, "active_storage_attachments", id);
}

// `active_storage_variant_records` rows: processed variants, found by blob and variation digest.
public static class VariantRecords
{
    const string select = $"SELECT {ActiveStorageVariantRecord.Columns} FROM \"active_storage_variant_records\"";

    public static ActiveStorageVariantRecord? Find(SqliteSession session, long blobId, string variationDigest) =>
        Sql.One(session,
            $"{select} WHERE \"active_storage_variant_records\".\"blob_id\" = @blob_id AND \"active_storage_variant_records\".\"variation_digest\" = @variation_digest LIMIT 1",
            ActiveStorageVariantRecord.Read,
            ("@blob_id", blobId), ("@variation_digest", variationDigest));

    // A blob's variants, as `includes(attachment_blob: :variant_records)` preloads them.
    public static List<ActiveStorageVariantRecord> ForBlobs(SqliteSession session, IReadOnlyList<long> blobIds)
    {
        ArgumentNullException.ThrowIfNull(blobIds);
        if (blobIds.Count == 0)
        {
            return [];
        }
        var (placeholders, parameters) = Sql.List("blob_id", blobIds);
        return session.Query($"{select} WHERE \"active_storage_variant_records\".\"blob_id\" IN ({placeholders})", ActiveStorageVariantRecord.Read, parameters);
    }

    public static ActiveStorageVariantRecord Create(SqliteSession session, long blobId, string variationDigest)
    {
        var id = Sql.Insert(session, """
            INSERT INTO "active_storage_variant_records" ("blob_id", "variation_digest") VALUES (@blob_id, @variation_digest) RETURNING "id"
            """, ("@blob_id", blobId), ("@variation_digest", variationDigest));
        return new ActiveStorageVariantRecord(id, blobId, variationDigest);
    }

    public static void Delete(SqliteSession session, long id) => Sql.Delete(session, "active_storage_variant_records", id);
}
