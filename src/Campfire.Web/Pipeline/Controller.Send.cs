using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Pipeline;

// Sending the response: what Rails does between the action returning and Puma writing it.
public abstract partial class Controller
{
    /// <summary>
    /// <c>request.commit_flash</c>, the response's <c>commit!</c>, then the middleware on the way
    /// out, innermost first: <c>Rack::ETag</c>, <c>Rack::ConditionalGet</c>, <c>Rack::Head</c>,
    /// the session store (CSRF token, then the session) and the cookie jar.
    /// </summary>
    internal async Task SendResponseAsync()
    {
        CommitFlash();
        CommitResponse();
        RackEtag();
        RackConditionalGet();
        var sendBody = !Request.IsHead;
        CommitSession();

        var response = HttpContext.Response;
        response.StatusCode = Status;
        foreach (var (name, value) in Headers)
        {
            response.Headers[name] = value;
        }
        var setCookies = Cookies.ToSetCookieHeaders(RequestUrl.IsSsl, RequestUrl.Host);
        if (setCookies.Count > 0)
        {
            response.Headers.SetCookie = setCookies.ToArray();
        }
        var output = HttpContext.Features.Get<ResponseOutput>();
        if (streamBody is not null && output is not null)
        {
            await StreamAsync(output, sendBody).ConfigureAwait(false);
            return;
        }
        if (output is not null)
        {
            output.Buffered = true;
        }
        if (streamBody is not null)
        {
            await streamBody(response.Body, RequestAborted).ConfigureAwait(false);
        }
        else if (sendBody && Body.Length > 0)
        {
            await response.Body.WriteAsync(Body, RequestAborted).ConfigureAwait(false);
        }
    }

    // A streamed body skips WebApp's buffer: the outer middleware's headers and the deflater are
    // applied here, before the first byte.
    async Task StreamAsync(ResponseOutput output, bool sendBody)
    {
        output.Streamed = true;
        output.AddHeaders();
        var gzip = RackDeflater.Prepare(HttpContext, rackContentLength: null) == RackDeflater.Decision.Gzip;
        if (!sendBody)
        {
            return;
        }
        if (gzip)
        {
            await using var stream = RackDeflater.GzipStream(output.Stream);
            await streamBody!(stream, RequestAborted).ConfigureAwait(false);
        }
        else
        {
            await streamBody!(output.Stream, RequestAborted).ConfigureAwait(false);
        }
    }

    // ActionDispatch::Flash's commit_flash: write a used flash back, and drop an emptied one.
    void CommitFlash()
    {
        if (flash is not null && (!flash.IsEmpty || Session.ContainsKey("flash")))
        {
            Session.SetNode("flash", flash.ToSessionValue());
        }
        if (Session.IsLoaded && Session.ContainsKey("flash") && Session.GetNode("flash") is null)
        {
            Session.Remove("flash");
        }
    }

    // Response#before_committed: the default content type, cache control, the default
    // Cache-Control for a validated response, and no content type for a status without content.
    void CommitResponse()
    {
        if (MediaType() is null)
        {
            SetContentType("text/html", charset: true);
        }
        if (CacheControl.ToHeader() is { } cacheControl)
        {
            Headers["Cache-Control"] = cacheControl;
        }
        if ((Headers.Contains("ETag") || Headers.Contains("Last-Modified")) && !Headers.Contains("Cache-Control"))
        {
            Headers["Cache-Control"] = "max-age=0, private, must-revalidate";
        }
        if (!IncludesContent(Status))
        {
            Headers.Remove("Content-Type");
            Headers.Remove("Content-Length");
        }
    }

    // Rack::ETag (installed with "no-cache"): a weak SHA-256 ETag for a 200 or 201 with a body and
    // no validators, and a default Cache-Control.
    void RackEtag()
    {
        var digested = false;
        if (Status is 200 or 201 && streamBody is null && !Headers.Contains("ETag") && !Headers.Contains("Last-Modified") && Body.Length > 0)
        {
            Headers["ETag"] = $"W/\"{Convert.ToHexStringLower(SHA256.HashData(Body.Span))[..32]}\"";
            digested = true;
        }
        if (!Headers.Contains("Cache-Control"))
        {
            Headers["Cache-Control"] = digested ? "max-age=0, private, must-revalidate" : "no-cache";
        }
    }

    // Rack::ConditionalGet: a GET or HEAD whose 200 the client already has becomes a 304. Rack
    // compares the whole If-None-Match with the ETag, unlike Rails' fresh?.
    void RackConditionalGet()
    {
        if (Request.Method is not ("GET" or "HEAD") || Status != 200 || streamBody is not null)
        {
            return;
        }
        bool fresh;
        if (Header("If-None-Match") is { } ifNoneMatch)
        {
            fresh = Headers["ETag"] == ifNoneMatch;
        }
        else if (Header("If-Modified-Since") is { Length: >= 16 } ifModifiedSince && ParseHttpDate(ifModifiedSince) is { } since)
        {
            fresh = Headers["Last-Modified"] is { Length: >= 16 } lastModified && ParseHttpDate(lastModified) is { } modified && since >= modified;
        }
        else
        {
            fresh = false;
        }
        if (fresh)
        {
            Status = 304;
            Headers.Remove("Content-Type");
            Headers.Remove("Content-Length");
            body = ReadOnlyMemory<byte>.Empty;
        }
    }

    // The session store's commit: commit_csrf_token, then commit_session.
    void CommitSession()
    {
        if (csrfToken is not null)
        {
            Session["_csrf_token"] = csrfToken;
        }
        Session.Commit(Now);
    }
}
