using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.Params;

namespace Campfire.Storage.Blobs;

/// <summary>
/// <c>Marcel::MimeType.for(io, name:, declared_type:)</c>, as <c>Blob#extract_content_type</c> calls
/// it. <paramref name="content"/> is positioned at the start of the bytes. S03 ports Marcel.
/// </summary>
public delegate string ContentTypeIdentifier(Stream content, string filename, string? declaredType);

/// <summary>What <c>BlobStorage.AttachOne</c> wrote.</summary>
/// <param name="Blob">The attached blob.</param>
/// <param name="Attachment">The new (or unchanged) attachment row.</param>
/// <param name="ReplacedBlobId">
/// The blob of the attachment this one replaced. Its attachment row is gone; Rails then purges the
/// blob later (<c>has_one_attached dependent: :purge_later</c>), which is the caller's job to enqueue.
/// </param>
public sealed record AttachedBlob(Blob Blob, Attachment Attachment, long? ReplacedBlobId);

/// <summary>
/// The Active Storage pieces Campfire uses over one disk service and <c>ActiveStorage.verifier</c>:
/// uploads, attaching, signed ids and URLs.
/// <para>
/// Uploads are split so the file work never holds the writer. <see cref="Stage(Stream, Filename, string?, bool)"/>
/// copies the file into the service, computing its checksum, size and content type as
/// <c>Blob#unfurl</c> does. The rows are then written inside the caller's write transaction
/// (<c>AttachOne</c>, <see cref="Create"/>), and the staged file is kept once it commits.
/// Rails writes the rows first and uploads after commit; the committed result is the same, and a
/// failed upload here leaves neither a file nor a row.
/// </para>
/// </summary>
public sealed class BlobStorage
{
    readonly ContentTypeIdentifier identify;

    public BlobStorage(DiskService service, MessageVerifier verifier, ContentTypeIdentifier? identify = null)
    {
        Service = service;
        Verifier = verifier;
        Urls = new BlobUrls(verifier);
        this.identify = identify ?? DeclaredContentType;
    }

    /// <summary>
    /// Campfire's <c>local</c> service under <paramref name="root"/> (<c>storage/files</c>), signed with
    /// <c>ActiveStorage.verifier</c>: <c>Rails.application.message_verifier("ActiveStorage")</c>.
    /// </summary>
    public static BlobStorage Local(string root, KeyGenerator keys, ContentTypeIdentifier? identify = null)
    {
        var verifier = MessageVerifier.ForApp(keys, "ActiveStorage");
        return new BlobStorage(new DiskService(root, verifier), verifier, identify);
    }

    public DiskService Service { get; }

    /// <summary><c>ActiveStorage.verifier</c>: signs blob ids, variation keys, disk URLs and upload tokens.</summary>
    public MessageVerifier Verifier { get; }

    public BlobUrls Urls { get; }

    /// <summary>
    /// The file half of <c>Blob.build_after_unfurling(io:, filename:, content_type:, identify:)</c>
    /// plus <c>upload_without_unfurling</c>: a new key, the bytes copied to <c>path_for(key)</c>,
    /// their MD5 and size, the content type (identified unless a declared type is given with
    /// <paramref name="identify"/> false) and <c>metadata: {identified: true}</c>.
    /// </summary>
    public StagedBlob Stage(Stream source, Filename filename, string? declaredType, bool identify = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(filename);
        var key = BlobKey.Generate();
        var path = Service.PathFor(key);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        string checksum;
        long byteSize;
        string? contentType;
        try
        {
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = source.Read(buffer)) > 0)
                {
                    md5.AppendData(buffer, 0, read);
                    file.Write(buffer, 0, read);
                }
                checksum = Convert.ToBase64String(md5.GetHashAndReset());
                byteSize = file.Length;
            }
            contentType = declaredType is null || identify ? Identify(path, filename, declaredType) : declaredType;
        }
        catch
        {
            Service.Delete(key);
            throw;
        }
        var metadata = new JsonObject { ["identified"] = true };
        return new StagedBlob(Service, new NewBlob(key, filename, contentType, metadata, Service.Name, byteSize, checksum));
    }

    /// <summary><see cref="Stage(Stream, Filename, string?, bool)"/> for an uploaded file, as <c>attach(uploaded_file)</c> builds it.</summary>
    public StagedBlob Stage(UploadedFile upload)
    {
        ArgumentNullException.ThrowIfNull(upload);
        using var source = File.OpenRead(upload.Path);
        return Stage(source, new Filename(upload.OriginalFilename), upload.ContentType);
    }

    /// <summary>
    /// The row half of <c>Blob.create_and_upload!</c>: inserts the staged blob's row and keeps its
    /// file once the transaction commits.
    /// </summary>
    public static Blob Create(WriteTransaction tx, StagedBlob staged, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(staged);
        var blob = BlobRecords.InsertBlob(tx.Session, staged.Blob, now);
        tx.AfterCommit(_ => staged.Keep());
        return blob;
    }

    /// <summary>
    /// <c>record.&lt;name&gt; = uploaded_file</c> for <c>has_one_attached</c>, as saved in the record's
    /// transaction (<c>Attached::Changes::CreateOne#save</c> and the <c>has_one</c> replace): the
    /// current attachment row, if any, is destroyed, then the blob row and the new attachment row are
    /// inserted. <paramref name="touchRecord"/> runs after each attachment change, for the
    /// attachment's <c>belongs_to :record, touch: true</c>, which needs the record's model.
    /// </summary>
    public static AttachedBlob AttachOne(
        WriteTransaction tx, StagedBlob staged, string recordType, long recordId, string name, DateTimeOffset now,
        Action<WriteTransaction>? touchRecord = null)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(staged);
        var replaced = Detach(tx, recordType, recordId, name, touchRecord);
        var blob = Create(tx, staged, now);
        var attachment = BlobRecords.InsertAttachment(tx.Session, name, recordType, recordId, blob.Id, now);
        touchRecord?.Invoke(tx);
        return new AttachedBlob(blob, attachment, replaced);
    }

    /// <summary>
    /// <c>record.&lt;name&gt; = blob</c> (or its signed id) for an existing blob, as a direct upload
    /// attaches. Attaching the blob that is already attached changes nothing.
    /// </summary>
    public static AttachedBlob AttachOne(
        WriteTransaction tx, Blob blob, string recordType, long recordId, string name, DateTimeOffset now,
        Action<WriteTransaction>? touchRecord = null)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(blob);
        if (BlobRecords.FindAttachment(tx.Session, recordType, recordId, name) is { } current && current.BlobId == blob.Id)
        {
            return new AttachedBlob(blob, current, null);
        }
        var replaced = Detach(tx, recordType, recordId, name, touchRecord);
        var attachment = BlobRecords.InsertAttachment(tx.Session, name, recordType, recordId, blob.Id, now);
        touchRecord?.Invoke(tx);
        return new AttachedBlob(blob, attachment, replaced);
    }

    /// <summary>
    /// <c>blob.url(expires_in:, disposition:)</c> on the disk service: <paramref name="baseUrl"/>
    /// (<c>ActiveStorage::Current.url_options</c>, e.g. <c>http://campfire.test</c>) and the signed disk
    /// path. Content types that must not render are served as binary attachments. Rails' default
    /// expiry is <c>service_urls_expire_in</c>, 5 minutes from now.
    /// </summary>
    public string Url(Blob blob, string baseUrl, DateTimeOffset? expiresAt, string? disposition = "inline")
    {
        ArgumentNullException.ThrowIfNull(blob);
        ArgumentNullException.ThrowIfNull(baseUrl);
        var path = Service.UrlPath(blob.Key, expiresAt, blob.Filename, blob.ContentTypeForServing,
            blob.ForcedDispositionForServing ?? disposition);
        return baseUrl.TrimEnd('/') + path;
    }

    /// <summary>
    /// <c>Blob#delete</c>: the file, and for images any legacy untracked variants under
    /// <c>variants/&lt;key&gt;/</c>. Rows are the caller's (<see cref="BlobRecords.DeleteBlobIfUnattached"/>).
    /// </summary>
    public void DeleteFiles(Blob blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        Service.Delete(blob.Key);
        if (blob.IsImage)
        {
            Service.DeletePrefixed($"variants/{blob.Key}/");
        }
    }

    /// <summary>
    /// <c>blob.open</c>'s check: the file's MD5 is the blob's checksum (composed blobs aren't
    /// verified). Throws <see cref="BlobIntegrityException"/> or <see cref="BlobFileNotFoundException"/>.
    /// </summary>
    public void VerifyIntegrity(Blob blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (blob.IsComposed)
        {
            return;
        }
        using var file = Service.OpenRead(blob.Key);
        if (BlobKey.Checksum(file) != blob.Checksum)
        {
            throw new BlobIntegrityException(blob.Key);
        }
    }

    static long? Detach(WriteTransaction tx, string recordType, long recordId, string name, Action<WriteTransaction>? touchRecord)
    {
        if (BlobRecords.FindAttachment(tx.Session, recordType, recordId, name) is not { } current)
        {
            return null;
        }
        BlobRecords.DeleteAttachment(tx.Session, current.Id);
        touchRecord?.Invoke(tx);
        return current.BlobId;
    }

    string Identify(string path, Filename filename, string? declaredType)
    {
        using var content = File.OpenRead(path);
        return identify(content, filename.Sanitized, declaredType);
    }

    /// <summary>Until S03 ports Marcel: the declared type, or Marcel's fallback for none.</summary>
    static string DeclaredContentType(Stream content, string filename, string? declaredType) => declaredType ?? BlobContentTypes.Binary;
}
