using System.Net;
using System.Security.Authentication;
using Campfire.Jobs.RestrictedHttp;
using Campfire.RichText.Sanitize;

namespace Campfire.Jobs.WebPush;

/// <summary>
/// <c>WebPush.payload_send</c> (web-push 3.1.0 lib/web_push/request.rb) through the
/// <c>endpoint_ip</c> branch of <c>WebPush::PersistentRequest</c>
/// (reference/config/initializers/web_push.rb): a POST to the endpoint over a fresh connection to
/// the pinned address, with no proxy, and the response checked by <c>verify_response</c>.
/// <para>
/// This is the pool's one <c>connection</c>. The pinned branch never uses the persistent pool, so
/// neither does this: <see cref="RestrictedHttpHandlers.WebPush"/> opens a connection per request.
/// </para>
/// </summary>
public sealed class WebPushClient : IDisposable
{
    /// <summary><c>WebPush::Request#default_options[:ttl]</c>: four weeks, in seconds.</summary>
    public const int Ttl = 60 * 60 * 24 * 7 * 4;

    /// <summary>
    /// Net::HTTP's 60-second open timeout (the handler's) plus its 60-second read timeout, which
    /// the gem leaves at their defaults; HttpClient has one deadline for the whole exchange.
    /// </summary>
    public static readonly TimeSpan Timeout = RestrictedHttpHandlers.NetHttpDefaultTimeout * 2;

    readonly HttpClient client;
    readonly VapidIdentification vapid;
    readonly TimeProvider clock;

    /// <param name="handler">
    /// <see cref="RestrictedHttpHandlers.WebPush"/> in the app. Tests pass one that dials a local
    /// server.
    /// </param>
    /// <param name="guard">Resolves each endpoint on the delivery worker (<see cref="PushEndpoint.ResolveAsync"/>).</param>
    /// <param name="vapid">The app's VAPID keys.</param>
    /// <param name="clock">Dates the VAPID JWT.</param>
    public WebPushClient(HttpMessageHandler handler, PrivateNetworkGuard guard, VapidIdentification vapid, TimeProvider clock)
    {
        client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout };
        Guard = guard;
        this.vapid = vapid;
        this.clock = clock;
    }

    /// <summary>The guard <see cref="WebPushNotification.DeliverAsync"/> resolves endpoints through.</summary>
    public PrivateNetworkGuard Guard { get; }

    /// <summary>
    /// <c>WebPush.payload_send(message:, endpoint:, endpoint_ip:, p256dh:, auth:, vapid:, urgency:)</c>:
    /// the push service's status when it's 2xx.
    /// </summary>
    /// <exception cref="WebPushResponseException">Any other status (<c>verify_response</c>).</exception>
    /// <exception cref="WebPushOpenSslException">A bad key, or the TLS session failed.</exception>
    /// <exception cref="WebPushArgumentException">A blank or malformed key, or a payload over 4096 bytes.</exception>
    public async Task<HttpStatusCode> PayloadSendAsync(
        string message,
        string endpoint,
        IPAddress endpointIp,
        string? p256dh,
        string? auth,
        string urgency,
        CancellationToken cancellationToken)
    {
        // WebPush::Request.new: `build_payload` encrypts unless the message is empty.
        var payload = string.IsNullOrEmpty(message) ? null : WebPushEncryption.Encrypt(message, p256dh, auth);
        var uri = RubyUri.Parse(endpoint);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint)) { Content = new ByteArrayContent(payload ?? []) };
        request.Options.Set(RestrictedHttpHandlers.PinnedAddress, endpointIp);
        foreach (var (name, value) in Headers(payload, vapid.Authorization(Audience(uri), clock.GetUtcNow()), urgency))
        {
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                request.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }
        // What Net::HTTP adds to every request.
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        request.Headers.TryAddWithoutValidation("User-Agent", "Ruby");

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException e) when (e.InnerException is AuthenticationException)
        {
            throw new WebPushOpenSslException("OpenSSL::SSL::SSLError", e.InnerException.Message, e);
        }
        using (response)
        {
            return VerifyResponse(response.StatusCode, response.ReasonPhrase, uri.Host);
        }
    }

    /// <summary><c>WebPush::Request#headers</c>, in the order the gem writes them.</summary>
    public static List<(string Name, string Value)> Headers(byte[]? payload, string authorization, string urgency)
    {
        List<(string, string)> headers =
        [
            ("Content-Type", "application/octet-stream"),
            ("Ttl", Ttl.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("Urgency", urgency),
        ];
        if (payload is not null)
        {
            headers.Add(("Content-Encoding", "aes128gcm"));
            headers.Add(("Content-Length", payload.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        headers.Add(("Authorization", authorization));
        return headers;
    }

    /// <summary><c>audience</c>: <c>uri.scheme + '://' + uri.host</c>.</summary>
    public static string Audience(RubyUri uri) => $"{uri.Scheme}://{uri.Host}";

    /// <summary><c>WebPush::Request#verify_response</c></summary>
    public static HttpStatusCode VerifyResponse(HttpStatusCode status, string? reason, string? host)
    {
        if (ResponseError(status, reason, host) is { } error)
        {
            throw error;
        }
        return status;
    }

    static WebPushResponseException? ResponseError(HttpStatusCode status, string? reason, string? host) => (int)status switch
    {
        410 => new WebPushExpiredSubscriptionException(status, host),
        404 => new WebPushResponseException("WebPush::InvalidSubscription", status, host),
        401 or 403 => new WebPushResponseException("WebPush::Unauthorized", status, host),
        400 when reason == "UnauthorizedRegistration" => new WebPushResponseException("WebPush::Unauthorized", status, host),
        413 => new WebPushResponseException("WebPush::PayloadTooLarge", status, host),
        429 => new WebPushResponseException("WebPush::TooManyRequests", status, host),
        >= 500 and <= 599 => new WebPushResponseException("WebPush::PushServiceError", status, host),
        >= 200 and <= 299 => null,
        _ => new WebPushResponseException("WebPush::ResponseError", status, host),
    };

    public void Dispose() => client.Dispose();
}
