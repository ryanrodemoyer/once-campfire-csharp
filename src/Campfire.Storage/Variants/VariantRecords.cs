using Campfire.Data.Sqlite;
using Campfire.Storage.Blobs;

namespace Campfire.Storage.Variants;

/// <summary>An <c>active_storage_variant_records</c> row.</summary>
public sealed record VariantRecord(long Id, long BlobId, string VariationDigest);

/// <summary>
/// SQL for <c>active_storage_variant_records</c> (<c>ActiveStorage::VariantRecord</c>, whose processed
/// file is its <c>has_one_attached :image</c>). Every method takes the caller's session.
/// </summary>
public static class VariantRecords
{
    public const string RecordType = "ActiveStorage::VariantRecord";
    public const string ImageName = "image";

    /// <summary><c>blob.variant_records.find_by(variation_digest:)</c>.</summary>
    public static VariantRecord? Find(SqliteSession session, long blobId, string variationDigest)
    {
        ArgumentNullException.ThrowIfNull(session);
        var rows = session.Query(
            """
            SELECT "id", "blob_id", "variation_digest" FROM "active_storage_variant_records"
            WHERE "active_storage_variant_records"."blob_id" = @blob_id AND "active_storage_variant_records"."variation_digest" = @digest LIMIT 1
            """,
            Read, ("@blob_id", blobId), ("@digest", variationDigest));
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary><c>blob.variant_records</c>, oldest first.</summary>
    public static List<VariantRecord> ForBlob(SqliteSession session, long blobId)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Query(
            """SELECT "id", "blob_id", "variation_digest" FROM "active_storage_variant_records" WHERE "blob_id" = @blob_id ORDER BY "id" """,
            Read, ("@blob_id", blobId));
    }

    /// <summary><c>record.image</c>: the variant's file, or null if its attachment is gone.</summary>
    public static Blob? Image(SqliteSession session, long recordId) =>
        BlobRecords.FindAttachedBlob(session, RecordType, recordId, ImageName);

    /// <summary>
    /// <c>blob.variant_records.create_or_find_by!(variation_digest:) { |record| record.image.attach(image) }</c>:
    /// inserts the record and attaches <paramref name="image"/> to it, or, when a record for this
    /// digest already exists (the unique index), leaves everything as it is and returns that one. The
    /// image's file is then not kept: disposing <paramref name="image"/> deletes it.
    /// </summary>
    public static VariantRecord CreateOrFind(WriteTransaction tx, long blobId, string variationDigest, StagedBlob image, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(image);
        var inserted = tx.Session.Query(
            """
            INSERT INTO "active_storage_variant_records" ("blob_id", "variation_digest") VALUES (@blob_id, @digest)
            ON CONFLICT ("blob_id", "variation_digest") DO NOTHING RETURNING "id"
            """,
            row => row.GetInt64(0), ("@blob_id", blobId), ("@digest", variationDigest));
        if (inserted.Count == 0)
        {
            return Find(tx.Session, blobId, variationDigest) ?? throw new InvalidOperationException("Variant record vanished");
        }
        BlobStorage.AttachOne(tx, image, RecordType, inserted[0], ImageName, now);
        return new VariantRecord(inserted[0], blobId, variationDigest);
    }

    /// <summary>
    /// The row half of <c>variant_record.destroy</c>. Its image attachment and blob are the caller's to
    /// purge (<c>has_one_attached :image</c>, <c>dependent: :purge_later</c>).
    /// </summary>
    public static void Delete(SqliteSession session, long recordId)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Execute("""DELETE FROM "active_storage_variant_records" WHERE "active_storage_variant_records"."id" = @id""", ("@id", recordId));
    }

    static VariantRecord Read(Microsoft.Data.Sqlite.SqliteDataReader row) => new(row.GetInt64(0), row.GetInt64(1), row.GetString(2));
}
