using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Campfire.RailsCompat.Ruby;
using Campfire.Storage.Blobs;
using Campfire.Web.Pipeline;
using Campfire.Web.Routing;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>ActiveStorage::Streaming</c> (activestorage/app/controllers/concerns/active_storage/streaming.rb):
/// <c>send_blob_stream</c> and <c>send_blob_byte_range_data</c>. The controllers include
/// <c>ActionController::Live</c>. A streamed body drops <c>Content-Length</c> on the first write and
/// leaves a <c>Cache-Control</c> that was already set; <c>send_data</c> (the range path) writes through
/// that buffer too, so its <c>Content-Length</c> does not survive and the buffer's <c>no-cache</c> does.
/// </summary>
static class BlobStreams
{
    const string inline = "inline";

    /// <summary>
    /// <c>http_cache_forever(public: true)</c> without reading the flash: the session cookie must not
    /// be rewritten (<c>ActiveStorage::DisableSession</c>). The etag is the path alone, which is what
    /// <c>fresh_when</c> digests when the flash is empty and the request isn't a turbo frame.
    /// True when the response is already a 304.
    /// </summary>
    public static bool FreshForever(Controller controller)
    {
        controller.ExpiresIn(TimeSpan.FromSeconds(3_155_695_200), isPublic: true, immutable: true);
        controller.Headers["ETag"] = $"W/\"{Controller.EtagDigest(controller.RequestUrl.FullPath)}\"";
        controller.Headers["Last-Modified"] = Controller.HttpDate(new DateTimeOffset(2011, 1, 1, 0, 0, 0, TimeSpan.Zero));
        if (!controller.IsFresh())
        {
            return false;
        }
        controller.Head(304);
        return true;
    }

    /// <summary>
    /// <c>send_blob_stream</c>. <paramref name="rememberLength"/> is the proxy controller setting
    /// <c>Content-Length</c> to <c>byte_size</c> before streaming: a missing file never writes, so the
    /// length stays, while a file that streams loses it to the live buffer.
    /// </summary>
    public static void SendBlobStream(Controller controller, Blob blob, string? disposition, bool rememberLength)
    {
        SetStreamHeaders(controller, blob, disposition);
        var path = StoragePath(controller, blob);
        if (!File.Exists(path))
        {
            if (rememberLength)
            {
                controller.Headers["Content-Length"] = blob.ByteSize.ToString(CultureInfo.InvariantCulture);
            }
            controller.ExpiresNow();
            controller.Head(404);
            return;
        }
        controller.SendStream(async (stream, cancellationToken) =>
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            await file.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        });
    }

    /// <summary><c>send_blob_byte_range_data</c>. A missing file raises, unlike <see cref="SendBlobStream"/>.</summary>
    public static void SendBlobByteRange(Controller controller, Blob blob, string rangeHeader, string? disposition)
    {
        var size = blob.ByteSize < 0 ? 0 : blob.ByteSize;
        var ranges = RackByteRanges.Parse(rangeHeader, size);
        if (ranges is null || ranges.Count == 0)
        {
            controller.Head(416);
            return;
        }
        var path = StoragePath(controller, blob);
        if (!File.Exists(path))
        {
            throw new BlobFileNotFoundException(blob.Key);
        }

        var serving = blob.ContentTypeForServing;
        byte[] data;
        string? contentType;
        if (ranges.Count == 1)
        {
            var (start, end) = ranges[0];
            contentType = serving;
            data = ReadRange(path, start, end);
            controller.Headers["Content-Range"] = $"bytes {start}-{end}/{size.ToString(CultureInfo.InvariantCulture)}";
        }
        else
        {
            var boundary = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            contentType = $"multipart/byteranges; boundary={boundary}";
            data = Multipart(path, serving, size, ranges, boundary);
        }
        if (contentType is null)
        {
            throw new ArgumentException(":type option required");
        }

        // send_file_headers! with sending_file, so no charset, then the live buffer's no-cache.
        // Content-Length is set and then deleted on write; the streamed body keeps it deleted.
        controller.Headers["Content-Type"] = contentType;
        controller.Headers["Content-Disposition"] = ContentDisposition.Format(ServingDisposition(blob, disposition), blob.Filename.Sanitized);
        controller.Headers["Content-Transfer-Encoding"] = "binary";
        controller.Headers["Accept-Ranges"] = "bytes";
        controller.ExpiresNow();
        controller.SendStream(async (stream, cancellationToken) =>
        {
            await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }, 206);
    }

    static void SetStreamHeaders(Controller controller, Blob blob, string? disposition)
    {
        controller.Headers["Content-Type"] = StreamContentType(blob);
        controller.Headers["Content-Disposition"] = ContentDisposition.Format(ServingDisposition(blob, disposition), blob.Filename.Sanitized);
    }

    /// <summary>
    /// <c>send_stream</c>'s type: the serving type, else <c>Mime::Type.lookup_by_extension</c>, else binary.
    /// </summary>
    static string StreamContentType(Blob blob)
    {
        if (!string.IsNullOrEmpty(blob.ContentTypeForServing))
        {
            return blob.ContentTypeForServing!;
        }
        var extension = blob.Filename.Extension.TrimStart('.');
        return extension.Length == 0 ? "application/octet-stream" : MimeType.LookupByExtension(extension)?.Value ?? "application/octet-stream";
    }

    static string ServingDisposition(Blob blob, string? disposition) =>
        blob.ForcedDispositionForServing ?? (string.IsNullOrEmpty(disposition) ? inline : disposition);

    static string StoragePath(Controller controller, Blob blob) =>
        ((ActiveStorageController)controller).App.RequireStorage().Service.PathFor(blob.Key);

    static byte[] Multipart(string path, string? serving, long size, IReadOnlyList<(long First, long Last)> ranges, string boundary)
    {
        using var body = new MemoryStream();
        var type = serving ?? "";
        foreach (var (start, end) in ranges)
        {
            var heading = $"\r\n--{boundary}\r\nContent-Type: {type}\r\nContent-Range: bytes {start}-{end}/{size.ToString(CultureInfo.InvariantCulture)}\r\n\r\n";
            body.Write(Encoding.ASCII.GetBytes(heading));
            body.Write(ReadRange(path, start, end));
        }
        body.Write(Encoding.ASCII.GetBytes($"\r\n--{boundary}--\r\n"));
        return body.ToArray();
    }

    static byte[] ReadRange(string path, long start, long end)
    {
        var length = checked((int)(end - start + 1));
        var bytes = new byte[length];
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        file.Seek(start, SeekOrigin.Begin);
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = file.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
            {
                throw new EndOfStreamException(path);
            }
            offset += read;
        }
        return bytes;
    }
}
