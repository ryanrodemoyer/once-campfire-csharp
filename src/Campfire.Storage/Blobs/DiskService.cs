using System.Text.Json.Nodes;
using Campfire.RailsCompat.Crypto;

namespace Campfire.Storage.Blobs;

/// <summary>
/// <c>ActiveStorage::Service::DiskService</c> (activestorage/lib/active_storage/service/disk_service.rb)
/// for Campfire's <c>local</c> service (<c>root: storage/files</c>, reference/config/storage.yml):
/// files at <c>&lt;root&gt;/&lt;key[0..1]&gt;/&lt;key[2..3]&gt;/&lt;key&gt;</c>, and the signed disk URLs
/// <c>GET /rails/active_storage/disk/:encoded_key/*filename</c> and <c>PUT /rails/active_storage/disk/:encoded_token</c>.
/// </summary>
public sealed class DiskService(string root, MessageVerifier verifier, string name = DiskService.LocalName)
{
    public const string LocalName = "local";

    public string Root { get; } = root;

    /// <summary>The service name stored in <c>service_name</c> and signed into disk URLs.</summary>
    public string Name { get; } = name;

    /// <summary><c>path_for(key)</c>.</summary>
    public string PathFor(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return System.IO.Path.Combine(Root, FolderFor(key), key);
    }

    /// <summary>
    /// <c>upload(key, io, checksum:)</c>: copies the stream to <see cref="PathFor"/>, then, given a
    /// checksum, reads the file back and deletes it if the MD5 differs.
    /// </summary>
    public void Upload(string key, Stream source, string? checksum = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        using (var file = new FileStream(MakePathFor(key), FileMode.Create, FileAccess.Write, FileShare.None))
        {
            source.CopyTo(file);
        }
        if (checksum is not null)
        {
            EnsureIntegrityOf(key, checksum);
        }
    }

    /// <summary><c>download(key)</c>.</summary>
    public byte[] Download(string key)
    {
        try
        {
            return File.ReadAllBytes(PathFor(key));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new BlobFileNotFoundException(key, e);
        }
    }

    /// <summary>Opens the file for reading (<c>download</c> with a block streams it).</summary>
    public FileStream OpenRead(string key)
    {
        try
        {
            return new FileStream(PathFor(key), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new BlobFileNotFoundException(key, e);
        }
    }

    /// <summary><c>delete(key)</c>: a file that is already gone is fine.</summary>
    public void Delete(string key)
    {
        try
        {
            File.Delete(PathFor(key));
        }
        catch (DirectoryNotFoundException)
        {
            // Ignore files already deleted.
        }
    }

    /// <summary>
    /// <c>delete_prefixed(prefix)</c>: <c>rm_rf</c> each match of <c>Dir.glob(path_for("#{prefix}*"))</c>.
    /// Blob#delete uses it for legacy untracked variants under <c>variants/&lt;key&gt;/</c>.
    /// </summary>
    public void DeletePrefixed(string prefix)
    {
        var pattern = PathFor(prefix + "*");
        var directory = System.IO.Path.GetDirectoryName(pattern)!;
        var stem = System.IO.Path.GetFileName(pattern)[..^1];
        if (!Directory.Exists(directory))
        {
            return;
        }
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var entry = System.IO.Path.GetFileName(path);
            // Dir.glob's "*" skips dotfiles unless the pattern itself starts with a dot.
            if (!entry.StartsWith(stem, StringComparison.Ordinal) || (stem.Length == 0 && entry.StartsWith('.')))
            {
                continue;
            }
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                File.Delete(path);
            }
        }
    }

    /// <summary><c>exist?(key)</c>.</summary>
    public bool Exist(string key) => System.IO.Path.Exists(PathFor(key));

    /// <summary>
    /// The path of <c>url(key, expires_in:, filename:, content_type:, disposition:)</c>:
    /// <c>/rails/active_storage/disk/:encoded_key/*filename</c>, where the key is
    /// <c>ActiveStorage.verifier.generate({key:, disposition:, content_type:, service_name:}, purpose: :blob_key)</c>.
    /// </summary>
    public string UrlPath(string key, DateTimeOffset? expiresAt, Filename filename, string? contentType, string? disposition)
    {
        ArgumentNullException.ThrowIfNull(filename);
        var payload = new JsonObject
        {
            ["key"] = key,
            ["disposition"] = ContentDisposition.For(disposition, filename),
            ["content_type"] = contentType,
            ["service_name"] = Name,
        };
        var encodedKey = verifier.Generate(payload, "blob_key", expiresAt);
        return $"{BlobUrls.Prefix}/disk/{RouteEscaping.EscapeSegment(encodedKey)}/{RouteEscaping.EscapePath(filename.Sanitized)}";
    }

    /// <summary>
    /// The path of <c>url_for_direct_upload(key, expires_in:, content_type:, content_length:, checksum:)</c>:
    /// <c>/rails/active_storage/disk/:encoded_token</c>, purpose <c>blob_token</c>.
    /// </summary>
    public string DirectUploadPath(string key, DateTimeOffset expiresAt, string? contentType, long contentLength, string checksum)
    {
        var payload = new JsonObject
        {
            ["key"] = key,
            ["content_type"] = contentType,
            ["content_length"] = contentLength,
            ["checksum"] = checksum,
            ["service_name"] = Name,
        };
        return $"{BlobUrls.Prefix}/disk/{RouteEscaping.EscapeSegment(verifier.Generate(payload, "blob_token", expiresAt))}";
    }

    /// <summary><c>DiskController#decode_verified_key</c>: the signed payload of a disk URL, or null.</summary>
    public DiskKey? DecodeVerifiedKey(string encodedKey, DateTimeOffset now)
    {
        if (Verified(encodedKey, "blob_key", now) is not { } data
            || Text(data["key"]) is not { } key || Text(data["disposition"]) is not { } disposition || Text(data["service_name"]) is not { } service)
        {
            return null;
        }
        return new DiskKey(key, disposition, Text(data["content_type"]), service);
    }

    /// <summary><c>DiskController#decode_verified_token</c>: the signed payload of a direct upload URL, or null.</summary>
    public DiskToken? DecodeVerifiedToken(string encodedToken, DateTimeOffset now)
    {
        if (Verified(encodedToken, "blob_token", now) is not { } data
            || Text(data["key"]) is not { } key || Text(data["checksum"]) is not { } checksum || Text(data["service_name"]) is not { } service
            || data["content_length"] is not JsonValue length || !length.TryGetValue<long>(out var contentLength))
        {
            return null;
        }
        return new DiskToken(key, Text(data["content_type"]), contentLength, checksum, service);
    }

    JsonObject? Verified(string message, string purpose, DateTimeOffset now)
    {
        var result = verifier.Verify(message, purpose, now);
        return result.IsValid ? result.Value as JsonObject : null;
    }

    static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary><c>folder_for(key)</c>: <c>[key[0..1], key[2..3]].join("/")</c>.</summary>
    static string FolderFor(string key)
    {
        var first = key.Length >= 2 ? key[..2] : key;
        var second = key.Length > 2 ? key[2..Math.Min(4, key.Length)] : "";
        return $"{first}/{second}";
    }

    string MakePathFor(string key)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        return path;
    }

    void EnsureIntegrityOf(string key, string checksum)
    {
        if (BlobKey.ChecksumFile(PathFor(key)) != checksum)
        {
            Delete(key);
            throw new BlobIntegrityException(key);
        }
    }
}

/// <summary>What a disk URL's <c>encoded_key</c> signs.</summary>
public sealed record DiskKey(string Key, string Disposition, string? ContentType, string ServiceName);

/// <summary>What a direct upload's <c>encoded_token</c> signs.</summary>
public sealed record DiskToken(string Key, string? ContentType, long ContentLength, string Checksum, string ServiceName);

/// <summary><c>ActiveStorage::FileNotFoundError</c>.</summary>
public sealed class BlobFileNotFoundException(string key, Exception? inner = null)
    : Exception($"No file for blob key {key}", inner);

/// <summary><c>ActiveStorage::IntegrityError</c>: the file's MD5 isn't the blob's checksum.</summary>
public sealed class BlobIntegrityException(string key) : Exception($"Checksum mismatch for blob key {key}");
