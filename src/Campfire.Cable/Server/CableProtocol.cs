using System.Globalization;
using System.Text.Json.Nodes;
using Campfire.RailsCompat.Crypto;

namespace Campfire.Cable.Server;

/// <summary>
/// The Action Cable wire format (<c>ActionCable::INTERNAL</c>). Frames are Ruby hashes run through
/// <c>ActiveSupport::JSON.encode</c>, so key order is the order the hash literals are written in
/// <c>Connection::Base</c> and <c>Channel::Base</c>.
/// </summary>
public static class CableProtocol
{
    /// <summary>
    /// Offered in <c>Sec-WebSocket-Protocol</c>. The first one the client lists that's in here wins:
    /// websocket-driver's negotiation walks the client's list, not the server's.
    /// </summary>
    public static readonly IReadOnlyList<string> Protocols = ["actioncable-v1-json", "actioncable-unsupported"];

    public const string DefaultMountPath = "/cable";

    /// <summary><c>ActionCable::Server::Connections::BEAT_INTERVAL</c>.</summary>
    public static readonly TimeSpan BeatInterval = TimeSpan.FromSeconds(3);

    public static string Welcome() => """{"type":"welcome"}""";

    /// <summary><c>Connection::Base#beat</c>: <c>{"type":"ping","message":&lt;unix seconds&gt;}</c>.</summary>
    public static string Ping(long unixSeconds) =>
        string.Create(CultureInfo.InvariantCulture, $$"""{"type":"ping","message":{{unixSeconds}}}""");

    /// <summary>
    /// <c>Connection::Base#close</c>: <c>{"type":"disconnect","reason":…,"reconnect":…}</c>.
    /// <paramref name="reason"/> is <c>nil</c> when Rails closes without one, and
    /// <paramref name="reconnect"/> is whatever JSON a remote disconnect carried
    /// (<c>message.fetch("reconnect", true)</c>, unvalidated).
    /// </summary>
    public static string Disconnect(DisconnectReason? reason, JsonNode? reconnect) =>
        $$"""{"type":"disconnect","reason":{{(reason is { } r ? $"\"{Name(r)}\"" : "null")}},"reconnect":{{RailsJson.Encode(reconnect?.DeepClone())}}}""";

    public static string Confirmation(string identifier) =>
        $$"""{"identifier":{{EncodeString(identifier)}},"type":"confirm_subscription"}""";

    public static string Rejection(string identifier) =>
        $$"""{"identifier":{{EncodeString(identifier)}},"type":"reject_subscription"}""";

    /// <summary>
    /// <c>{"identifier":…,"message":…}</c> from an encoded identifier and an already encoded
    /// message. Rails decodes each broadcast and re-encodes it inside this hash; passing the encoded
    /// payload through is the same and saves the round trip per subscriber.
    /// </summary>
    public static string Message(string encodedIdentifier, string encodedMessage) =>
        $$"""{"identifier":{{encodedIdentifier}},"message":{{encodedMessage}}}""";

    /// <summary>A string as <c>ActiveSupport::JSON.encode</c> writes it.</summary>
    public static string EncodeString(string value) => RailsJson.Encode(JsonValue.Create(value));

    /// <summary><c>ActionCable::INTERNAL[:disconnect_reasons]</c>.</summary>
    public static string Name(DisconnectReason reason) => reason switch
    {
        DisconnectReason.Unauthorized => "unauthorized",
        DisconnectReason.InvalidRequest => "invalid_request",
        DisconnectReason.ServerRestart => "server_restart",
        DisconnectReason.Remote => "remote",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    /// <summary>
    /// The first protocol in the client's <c>Sec-WebSocket-Protocol</c> list that Action Cable
    /// supports.
    /// </summary>
    public static string? NegotiateProtocol(IEnumerable<string?> requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        foreach (var header in requested)
        {
            foreach (var protocol in (header ?? "").Split(','))
            {
                var name = protocol.Trim();
                if (Protocols.Contains(name, StringComparer.Ordinal))
                {
                    return name;
                }
            }
        }
        return null;
    }
}

/// <summary><c>ActionCable::INTERNAL[:disconnect_reasons]</c>.</summary>
public enum DisconnectReason
{
    Unauthorized,
    InvalidRequest,
    ServerRestart,
    Remote,
}
