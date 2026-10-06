using System.Net;

namespace Campfire.Jobs.WebPush;

/// <summary>
/// <c>WebPush::ResponseError</c> (web-push 3.1.0 lib/web_push/errors.rb): the push service
/// answered with something other than 2xx. <see cref="RubyClass"/> names the gem's subclass.
/// </summary>
public class WebPushResponseException(string rubyClass, HttpStatusCode status, string? host)
    : Exception($"host: {host}, status: {(int)status}")
{
    public string RubyClass { get; } = rubyClass;

    public HttpStatusCode Status { get; } = status;

    public string? Host { get; } = host;
}

/// <summary>
/// <c>WebPush::ExpiredSubscription</c> (410 Gone), the one response <c>WebPush::Pool#deliver</c>
/// destroys the subscription for. A 404 is <c>WebPush::InvalidSubscription</c>, a sibling class
/// the pool doesn't rescue, so it's only logged.
/// </summary>
public sealed class WebPushExpiredSubscriptionException(HttpStatusCode status, string? host)
    : WebPushResponseException("WebPush::ExpiredSubscription", status, host);

/// <summary>
/// An <c>OpenSSL::OpenSSLError</c>: a subscription key that isn't a P-256 point
/// (<c>OpenSSL::PKey::EC::Point::Error</c>), a VAPID key OpenSSL refuses, or a failed TLS session
/// (<c>OpenSSL::SSL::SSLError</c>). <c>WebPush::Pool#deliver</c> destroys the subscription for
/// any of them.
/// </summary>
public sealed class WebPushOpenSslException(string rubyClass, string message, Exception? inner = null) : Exception(message, inner)
{
    public string RubyClass { get; } = rubyClass;
}

/// <summary>Ruby's <c>ArgumentError</c> from the gem: a blank key, bad Base64 or an oversized payload.</summary>
public sealed class WebPushArgumentException(string message) : Exception(message);
