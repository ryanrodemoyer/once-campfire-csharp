using System.Text.Json.Nodes;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.Formatting;
using Microsoft.Data.Sqlite;

namespace Campfire.Storage.Blobs;

/// <summary>
/// SQL for <c>active_storage_blobs</c> and <c>active_storage_attachments</c>. Every method takes the
/// caller's session, so the caller decides the transaction, as the Rails models' callbacks do.
/// </summary>
public static class BlobRecords
{
    const string blobColumns = "b.id, b.key, b.filename, b.content_type, b.metadata, b.service_name, b.byte_size, b.checksum, b.created_at";
    const string attachmentColumns = "a.id, a.name, a.record_type, a.record_id, a.blob_id, a.created_at";

    /// <summary>
    /// <c>INSERT INTO active_storage_blobs</c>, as <c>save!</c> writes a new blob. <c>metadata</c> is
    /// dumped with <c>ActiveSupport::JSON.encode</c> (<c>ActiveRecord::Coders::JSON</c>).
    /// </summary>
    public static Blob InsertBlob(SqliteSession session, NewBlob blob, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(blob);
        var createdAt = ActiveRecordTime.ToDb(now);
        var id = session.Scalar<long>(
            """
            INSERT INTO "active_storage_blobs" ("key", "filename", "content_type", "metadata", "service_name", "byte_size", "checksum", "created_at")
            VALUES (@key, @filename, @content_type, @metadata, @service_name, @byte_size, @checksum, @created_at) RETURNING "id"
            """,
            ("@key", blob.Key), ("@filename", blob.Filename.Value), ("@content_type", blob.ContentType),
            ("@metadata", RailsJson.Encode(blob.Metadata)), ("@service_name", blob.ServiceName), ("@byte_size", blob.ByteSize),
            ("@checksum", blob.Checksum), ("@created_at", createdAt));
        return new Blob(id, blob.Key, blob.Filename, blob.ContentType, (JsonObject)blob.Metadata.DeepClone(), blob.ServiceName,
            blob.ByteSize, blob.Checksum, ActiveRecordTime.FromDb(createdAt)!.Value);
    }

    /// <summary><c>update!(metadata:)</c>.</summary>
    public static void UpdateMetadata(SqliteSession session, long blobId, JsonObject metadata)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Execute("""UPDATE "active_storage_blobs" SET "metadata" = @metadata WHERE "active_storage_blobs"."id" = @id""",
            ("@metadata", RailsJson.Encode(metadata)), ("@id", blobId));
    }

    public static Blob? FindBlob(SqliteSession session, long id) =>
        SingleBlob(session, $"SELECT {blobColumns} FROM active_storage_blobs b WHERE b.id = @id LIMIT 1", ("@id", id));

    public static Blob? FindBlobByKey(SqliteSession session, string key) =>
        SingleBlob(session, $"SELECT {blobColumns} FROM active_storage_blobs b WHERE b.key = @key LIMIT 1", ("@key", key));

    /// <summary><c>record.&lt;name&gt;_blob</c> for <c>has_one_attached</c>.</summary>
    public static Blob? FindAttachedBlob(SqliteSession session, string recordType, long recordId, string name) =>
        SingleBlob(session,
            $"SELECT {blobColumns} FROM active_storage_blobs b JOIN active_storage_attachments a ON a.blob_id = b.id " +
            "WHERE a.record_type = @record_type AND a.record_id = @record_id AND a.name = @name LIMIT 1",
            ("@record_type", recordType), ("@record_id", recordId), ("@name", name));

    /// <summary>Batch loads attached blobs for multiple records.</summary>
    public static Dictionary<long, Blob> FindAttachedBlobs(SqliteSession session, string recordType, IReadOnlyList<long> recordIds, string name)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(recordIds);
        if (recordIds.Count == 0) return [];
        var placeholders = string.Join(",", recordIds.Select((_, i) => $"@id{i}"));
        (string Name, object? Value)[] parameters = [.. recordIds.Select((id, i) => ($"@id{i}", (object?)id)), ("@record_type", recordType), ("@name", name)];
        return session.Query(
            $"SELECT a.record_id, {blobColumns} FROM active_storage_blobs b JOIN active_storage_attachments a ON a.blob_id = b.id " +
            $"WHERE a.record_type = @record_type AND a.name = @name AND a.record_id IN ({placeholders})",
            reader => (RecordId: reader.GetInt64(0), Blob: ReadBlobAt(reader, 1)),
            parameters).ToDictionary(p => p.RecordId, p => p.Blob);
    }

    /// <summary>Finds attached blob IDs for multiple records.</summary>
    public static Dictionary<long, long> FindAttachmentBlobIds(SqliteSession session, string recordType, IReadOnlyList<long> recordIds, string name)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(recordIds);
        if (recordIds.Count == 0) return [];
        var placeholders = string.Join(",", recordIds.Select((_, i) => $"@id{i}"));
        (string Name, object? Value)[] parameters = [.. recordIds.Select((id, i) => ($"@id{i}", (object?)id)), ("@record_type", recordType), ("@name", name)];
        return session.Query(
            $"SELECT a.record_id, a.blob_id FROM active_storage_attachments a " +
            $"WHERE a.record_type = @record_type AND a.name = @name AND a.record_id IN ({placeholders})",
            reader => (RecordId: reader.GetInt64(0), BlobId: reader.GetInt64(1)),
            parameters).ToDictionary(p => p.RecordId, p => p.BlobId);
    }

    /// <summary><c>record.&lt;name&gt;_attachment</c> for <c>has_one_attached</c>.</summary>
    public static Attachment? FindAttachment(SqliteSession session, string recordType, long recordId, string name)
    {
        ArgumentNullException.ThrowIfNull(session);
        var rows = session.Query(
            $"SELECT {attachmentColumns} FROM active_storage_attachments a " +
            "WHERE a.record_type = @record_type AND a.record_id = @record_id AND a.name = @name LIMIT 1",
            ReadAttachment, ("@record_type", recordType), ("@record_id", recordId), ("@name", name));
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>A blob's attachments, oldest first (<c>blob.attachments</c>).</summary>
    public static List<Attachment> AttachmentsOf(SqliteSession session, long blobId)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Query($"SELECT {attachmentColumns} FROM active_storage_attachments a WHERE a.blob_id = @blob_id ORDER BY a.id",
            ReadAttachment, ("@blob_id", blobId));
    }

    /// <summary><c>INSERT INTO active_storage_attachments</c>.</summary>
    public static Attachment InsertAttachment(SqliteSession session, string name, string recordType, long recordId, long blobId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        var createdAt = ActiveRecordTime.ToDb(now);
        var id = session.Scalar<long>(
            """
            INSERT INTO "active_storage_attachments" ("name", "record_type", "record_id", "blob_id", "created_at")
            VALUES (@name, @record_type, @record_id, @blob_id, @created_at) RETURNING "id"
            """,
            ("@name", name), ("@record_type", recordType), ("@record_id", recordId), ("@blob_id", blobId), ("@created_at", createdAt));
        return new Attachment(id, name, recordType, recordId, blobId, ActiveRecordTime.FromDb(createdAt)!.Value);
    }

    public static void DeleteAttachment(SqliteSession session, long attachmentId)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Execute("""DELETE FROM "active_storage_attachments" WHERE "active_storage_attachments"."id" = @id""", ("@id", attachmentId));
    }

    /// <summary>
    /// The row half of <c>blob.purge</c>: deletes the blob's row unless an attachment still refers to
    /// it (<c>before_destroy</c> raises <c>InvalidForeignKey</c>, which <c>purge</c> rescues).
    /// Returns whether it was deleted; the caller then deletes its files after commit.
    /// </summary>
    public static bool DeleteBlobIfUnattached(SqliteSession session, long blobId)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Scalar<long>("SELECT 1 FROM active_storage_attachments WHERE blob_id = @id LIMIT 1", ("@id", blobId)) == 1)
        {
            return false;
        }
        return session.Execute("""DELETE FROM "active_storage_blobs" WHERE "active_storage_blobs"."id" = @id""", ("@id", blobId)) > 0;
    }

    static Blob? SingleBlob(SqliteSession session, string sql, params ReadOnlySpan<(string Name, object? Value)> parameters)
    {
        ArgumentNullException.ThrowIfNull(session);
        var rows = session.Query(sql, ReadBlob, parameters);
        return rows.Count > 0 ? rows[0] : null;
    }

    static Blob ReadBlob(SqliteDataReader row) => ReadBlobAt(row, 0);

    static Blob ReadBlobAt(SqliteDataReader row, int offset) => new(
        row.GetInt64(offset + 0),
        row.GetString(offset + 1),
        new Filename(row.GetString(offset + 2)),
        row.IsDBNull(offset + 3) ? null : row.GetString(offset + 3),
        row.IsDBNull(offset + 4) ? [] : ParseMetadata(row.GetString(offset + 4)),
        row.GetString(offset + 5),
        row.GetInt64(offset + 6),
        row.IsDBNull(offset + 7) ? null : row.GetString(offset + 7),
        ReadTime(row, offset + 8));

    static Attachment ReadAttachment(SqliteDataReader row) =>
        new(row.GetInt64(0), row.GetString(1), row.GetString(2), row.GetInt64(3), row.GetInt64(4), ReadTime(row, 5));

    static DateTimeOffset ReadTime(SqliteDataReader row, int ordinal) =>
        ActiveRecordTime.FromDb(row.GetString(ordinal)) ?? throw new FormatException($"Unreadable timestamp {row.GetString(ordinal)}");

    /// <summary><c>IndifferentCoder#load</c>: anything that isn't a JSON object (or is blank) is <c>{}</c>.</summary>
    static JsonObject ParseMetadata(string json) =>
        !string.IsNullOrWhiteSpace(json) && RailsJson.TryParse(json, out var node) && node is JsonObject metadata ? metadata : [];
}
