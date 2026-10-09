using System.Globalization;
using System.Text;
using Campfire.RailsCompat.Ruby;
using Campfire.Web.Pipeline;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>ActiveStorage::FileServer#serve_file</c> over <c>Rack::Files#serving</c> (rack 3.2.6).
/// Disk keys have no extension, so Rack's mime lookup is its default <c>text/plain</c>; the multipart
/// headings keep that, and FileServer then overwrites the response's <c>Content-Type</c> and
/// <c>Content-Disposition</c>. A 416 drops Rack's <c>X-Cascade</c> before that overwrite.
/// </summary>
static class DiskFileServer
{
    public const string MultipartBoundary = "AaB03x";

    const string defaultContentType = "application/octet-stream";
    const string defaultDisposition = "attachment";
    const string partMime = "text/plain";

    /// <summary>Serves <paramref name="path"/> into <paramref name="controller"/>'s response.</summary>
    public static void Serve(Controller controller, string path, string? contentType, string? disposition)
    {
        ArgumentNullException.ThrowIfNull(controller);
        var type = contentType ?? defaultContentType;
        var shown = disposition ?? defaultDisposition;
        if (controller.Request.Method == "OPTIONS")
        {
            controller.Headers["Allow"] = "GET, HEAD, OPTIONS";
            controller.Headers["Content-Type"] = type;
            controller.Headers["Content-Disposition"] = shown;
            controller.Headers["Content-Length"] = "0";
            controller.Render(ReadOnlyMemory<byte>.Empty, type);
            return;
        }

        var info = new FileInfo(path);
        var httpDate = HttpDate(info.LastWriteTimeUtc);
        if (controller.Header("If-Modified-Since") == httpDate)
        {
            // Rack::Files returns no headers; FileServer still sets type and disposition, and the
            // 304 commit drops the type (and any length).
            controller.Headers["Content-Type"] = type;
            controller.Headers["Content-Disposition"] = shown;
            controller.Head(304);
            return;
        }

        var size = info.Length;
        var ranges = RackByteRanges.Parse(controller.Header("Range"), size);
        if (ranges is { Count: 0 })
        {
            var message = "Byte range unsatisfiable\n"u8.ToArray();
            controller.Headers["Content-Type"] = type;
            controller.Headers["Content-Disposition"] = shown;
            controller.Headers["Content-Length"] = message.Length.ToString(CultureInfo.InvariantCulture);
            controller.Headers["Content-Range"] = $"bytes */{size.ToString(CultureInfo.InvariantCulture)}";
            controller.Render(message, type, 416);
            return;
        }

        byte[] body;
        var status = 200;
        if (ranges is null)
        {
            body = size == 0 ? [] : ReadRange(path, 0, size - 1);
        }
        else if (ranges.Count == 1)
        {
            var (start, end) = ranges[0];
            controller.Headers["Content-Range"] = $"bytes {start}-{end}/{size.ToString(CultureInfo.InvariantCulture)}";
            body = ReadRange(path, start, end);
            status = 206;
        }
        else
        {
            // Rack::Files names the type multipart/byteranges. FileServer then overwrites it with
            // the blob's type; the body stays the multipart payload.
            body = Multipart(path, size, ranges);
            status = 206;
        }

        controller.Headers["Last-Modified"] = httpDate;
        controller.Headers["Content-Type"] = type;
        controller.Headers["Content-Disposition"] = shown;
        controller.Headers["Content-Length"] = body.Length.ToString(CultureInfo.InvariantCulture);
        // Buffered, like Rack::Files' body: Rack::ConditionalGet can still turn a fresh 200 into a 304.
        // The type was set above, so Render leaves it (no charset).
        controller.Render(body, type, status);
    }

    static byte[] Multipart(string path, long size, IReadOnlyList<(long First, long Last)> ranges)
    {
        using var body = new MemoryStream();
        foreach (var (start, end) in ranges)
        {
            var heading = $"\r\n--{MultipartBoundary}\r\ncontent-type: {partMime}\r\ncontent-range: bytes {start}-{end}/{size.ToString(CultureInfo.InvariantCulture)}\r\n\r\n";
            body.Write(Encoding.ASCII.GetBytes(heading));
            body.Write(ReadRange(path, start, end));
        }
        body.Write(Encoding.ASCII.GetBytes($"\r\n--{MultipartBoundary}--\r\n"));
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

    /// <summary><c>File.mtime.httpdate</c>, which has no fraction of a second.</summary>
    static string HttpDate(DateTime utc)
    {
        var truncated = new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, utc.Second, DateTimeKind.Utc);
        return Controller.HttpDate(new DateTimeOffset(truncated));
    }
}
