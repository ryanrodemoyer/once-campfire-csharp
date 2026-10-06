using System.IO.Compression;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Pipeline;

/// <summary>
/// <c>Rack::Deflater</c>, which reference/config.ru puts around the whole app (rack 3.2.6
/// <c>deflater.rb</c>): every response with a body says <c>Vary: Accept-Encoding</c>, and is
/// gzipped when the client prefers gzip to identity. A response it skips (no-content statuses,
/// <c>no-transform</c>, an encoding already set, or a <c>Content-Length</c> of 0) is left alone.
/// </summary>
public static class RackDeflater
{
    static readonly string[] Available = ["gzip", "identity"];

    /// <summary>What the deflater does with one response.</summary>
    public enum Decision
    {
        /// <summary>Left alone, without a <c>Vary</c>.</summary>
        Skip,
        Identity,
        Gzip,
        /// <summary>No acceptable encoding: a 406 in place of the response.</summary>
        NotAcceptable,
    }

    /// <summary>
    /// Decides for a response whose status and headers are set, and adds <c>Vary</c> and, for gzip,
    /// <c>Content-Encoding</c> (dropping <c>Content-Length</c>). <paramref name="rackContentLength"/>
    /// is the <c>Content-Length</c> the app itself set, if any.
    /// </summary>
    public static Decision Prepare(HttpContext context, long? rackContentLength)
    {
        ArgumentNullException.ThrowIfNull(context);
        var response = context.Response;
        if (!ShouldDeflate(response.StatusCode, response.Headers, rackContentLength))
        {
            return Decision.Skip;
        }
        var vary = response.Headers.Vary.ToString().Split(',').Select(value => value.Trim()).Where(value => value.Length > 0).ToList();
        if (!vary.Contains("*") && !vary.Any(value => value.Equals("accept-encoding", StringComparison.OrdinalIgnoreCase)))
        {
            vary.Add("Accept-Encoding");
            response.Headers.Vary = string.Join(',', vary);
        }
        switch (SelectBestEncoding(context.Request.Headers.AcceptEncoding.ToString()))
        {
            case "gzip":
                response.Headers.ContentEncoding = "gzip";
                response.Headers.ContentLength = null;
                return Decision.Gzip;
            case "identity":
                return Decision.Identity;
            default:
                return Decision.NotAcceptable;
        }
    }

    /// <summary>
    /// Sends a buffered response through the deflater: as it is, gzipped, or replaced by the 406.
    /// </summary>
    public static async Task SendAsync(HttpContext context, ReadOnlyMemory<byte> body, long? rackContentLength, Stream output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);
        var head = HttpMethods.IsHead(context.Request.Method);
        switch (Prepare(context, rackContentLength))
        {
            case Decision.Gzip:
                var compressed = Gzip(body);
                if (!head)
                {
                    await output.WriteAsync(compressed, context.RequestAborted).ConfigureAwait(false);
                }
                break;
            case Decision.NotAcceptable:
                var message = Encoding8($"An acceptable encoding for the requested resource {new RequestUrl(context.Request).FullPath} could not be found.");
                context.Response.StatusCode = 406;
                context.Response.Headers.Clear();
                context.Response.ContentType = "text/plain";
                context.Response.ContentLength = message.Length;
                if (!head)
                {
                    await output.WriteAsync(message, context.RequestAborted).ConfigureAwait(false);
                }
                break;
            default:
                if (context.Response.StatusCode is not ((>= 100 and <= 199) or 204 or 304))
                {
                    context.Response.ContentLength ??= body.Length;
                }
                if (!head && body.Length > 0)
                {
                    await output.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
                }
                break;
        }
    }

    /// <summary><c>should_deflate?</c></summary>
    public static bool ShouldDeflate(int status, IHeaderDictionary headers, long? rackContentLength)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (status is (>= 100 and <= 199) or 204 or 304)
        {
            return false;
        }
        if (headers.CacheControl.ToString().Contains("no-transform", StringComparison.Ordinal))
        {
            return false;
        }
        var contentEncoding = headers.ContentEncoding.ToString();
        if (contentEncoding.Length > 0 && !contentEncoding.Contains("identity", StringComparison.Ordinal))
        {
            return false;
        }
        return rackContentLength != 0;
    }

    /// <summary>
    /// <c>Rack::Utils.select_best_encoding(%w(gzip identity), request.accept_encoding)</c>: the
    /// most preferred of gzip and identity, identity when nothing is said, null when both are
    /// refused.
    /// </summary>
    public static string? SelectBestEncoding(string? acceptEncoding)
    {
        var accepted = ParseAccept(acceptEncoding);
        var expanded = new List<(string Encoding, double Quality, int Preference)>();
        foreach (var (encoding, quality) in accepted)
        {
            var index = Array.IndexOf(Available, encoding);
            var preference = index < 0 ? Available.Length : index;
            if (encoding == "*")
            {
                foreach (var other in Available.Where(available => !accepted.Any(pair => pair.Encoding == available)))
                {
                    expanded.Add((other, quality, preference));
                }
            }
            else
            {
                expanded.Add((encoding, quality, preference));
            }
        }
        var candidates = expanded.OrderBy(entry => -entry.Quality).ThenBy(entry => entry.Preference).Select(entry => entry.Encoding).ToList();
        if (!candidates.Contains("identity"))
        {
            candidates.Add("identity");
        }
        foreach (var (encoding, quality, _) in expanded)
        {
            if (quality == 0.0)
            {
                candidates.RemoveAll(candidate => candidate == encoding);
            }
        }
        return candidates.Distinct().FirstOrDefault(candidate => Available.Contains(candidate));
    }

    // parse_http_accept_header: "gzip;q=0.5, identity" as (attribute, quality) pairs.
    static List<(string Encoding, double Quality)> ParseAccept(string? header)
    {
        var pairs = new List<(string, double)>();
        foreach (var raw in (header ?? "").Split(','))
        {
            var part = raw.Trim();
            if (part.Length == 0)
            {
                continue;
            }
            var semicolon = part.IndexOf(';', StringComparison.Ordinal);
            var attribute = (semicolon < 0 ? part : part[..semicolon]).Trim();
            var parameters = semicolon < 0 ? null : part[(semicolon + 1)..].Trim();
            var quality = 1.0;
            if (parameters is not null && parameters.StartsWith("q=", StringComparison.Ordinal))
            {
                var digits = new string(parameters[2..].TakeWhile(c => char.IsAsciiDigit(c) || c == '.').ToArray());
                if (digits.Length > 0)
                {
                    quality = RailsCompat.Ruby.RubyFloat.ToF(digits);
                }
            }
            pairs.Add((attribute, quality));
        }
        return pairs;
    }

    /// <summary>A stream that gzips what's written to <paramref name="output"/> (for streamed bodies).</summary>
    public static Stream GzipStream(Stream output) => new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true);

    static byte[] Gzip(ReadOnlyMemory<byte> body)
    {
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(body.Span);
        }
        return compressed.ToArray();
    }

    static byte[] Encoding8(string text) => System.Text.Encoding.UTF8.GetBytes(text);
}
