using System.Net;
using System.Text.Json.Nodes;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Crypto;

namespace Campfire.Jobs.WebPush;

/// <summary>The <c>title:</c>, <c>body:</c> and <c>path:</c> of a push (<c>Room::MessagePusher#build_payload</c>).</summary>
public sealed record PushPayload(string? Title, string Body, string Path);

/// <summary>
/// <c>WebPush::Notification</c> (reference/lib/web_push/notification.rb): what one subscription is
/// sent. It's built on the enqueuing side with the badge counted there, and the endpoint is only
/// resolved when it's delivered.
/// </summary>
public sealed record WebPushNotification(string? Title, string Body, string Path, long Badge, string? Endpoint, string? P256dhKey, string? AuthKey)
{
    /// <summary><c>Rails.application.routes.url_helpers.account_logo_path</c></summary>
    public const string IconPath = "/account/logo";

    /// <summary>The <c>urgency:</c> <c>deliver</c> sends.</summary>
    public const string Urgency = "high";

    /// <summary>
    /// <c>subscription.notification(**params)</c> (push/subscription.rb): the badge is
    /// <c>user.memberships.unread.count</c>.
    /// </summary>
    public static WebPushNotification For(SqliteSession session, PushSubscription subscription, PushPayload payload)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(payload);
        return new(payload.Title, payload.Body, payload.Path, Memberships.CountUnreadForUser(session, subscription.UserId),
            subscription.Endpoint, subscription.P256dhKey, subscription.AuthKey);
    }

    /// <summary>
    /// <c>encoded_message</c>: <c>JSON.generate title:, options: { body:, icon:, data: { path:, badge: } }</c>.
    /// </summary>
    public string EncodedMessage() => RailsJson.Generate(new JsonObject
    {
        ["title"] = Title,
        ["options"] = new JsonObject
        {
            ["body"] = Body,
            ["icon"] = IconPath,
            ["data"] = new JsonObject { ["path"] = Path, ["badge"] = Badge },
        },
    });

    /// <summary>
    /// <c>deliver(connection:)</c>: nothing (null) when the endpoint isn't a permitted push service
    /// or no longer resolves to a public address; otherwise the push service's 2xx status.
    /// </summary>
    /// <exception cref="WebPushResponseException">The push service refused it.</exception>
    /// <exception cref="WebPushOpenSslException">A bad key, or the TLS session failed.</exception>
    /// <exception cref="WebPushArgumentException">A blank or malformed key, or too big a payload.</exception>
    public async Task<HttpStatusCode?> DeliverAsync(WebPushClient connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (await PushEndpoint.ResolveAsync(connection.Guard, Endpoint, cancellationToken).ConfigureAwait(false) is not { } endpointIp)
        {
            return null;
        }
        return await connection.PayloadSendAsync(EncodedMessage(), Endpoint!, endpointIp, P256dhKey, AuthKey, Urgency, cancellationToken).ConfigureAwait(false);
    }
}
