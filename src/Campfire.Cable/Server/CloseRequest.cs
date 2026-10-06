using System.Net.WebSockets;
using System.Text.Json.Nodes;

namespace Campfire.Cable.Server;

/// <summary>Why a connection's socket is going away, once it's been decided.</summary>
internal sealed record CloseRequest
{
    /// <summary>Sent as <c>{"type":"disconnect",…}</c> before the close frame; null when the client closed.</summary>
    public string? DisconnectFrame { get; private init; }

    public WebSocketCloseStatus Status { get; private init; } = WebSocketCloseStatus.NormalClosure;

    /// <summary>The client sent a close frame first; ours answers it.</summary>
    public bool Reply { get; private init; }

    /// <summary><c>Connection::Base#close(reason:, reconnect:)</c>.</summary>
    public static CloseRequest Disconnect(DisconnectReason? reason, JsonNode? reconnect) =>
        new() { DisconnectFrame = CableProtocol.Disconnect(reason, reconnect) };

    /// <summary>
    /// The client stopped reading and its queue filled: <c>reason: nil</c>, as
    /// <c>Connection::Base#close</c> without one, and reconnect so it catches up from a fresh page
    /// load of state rather than with gaps.
    /// </summary>
    public static CloseRequest Lagged() => Disconnect(null, true);

    /// <summary>Completes the closing handshake the client started (RFC 6455 §5.5.1), with its code.</summary>
    public static CloseRequest Answer(WebSocketCloseStatus? status) =>
        new() { Reply = true, Status = status ?? WebSocketCloseStatus.Empty };

    /// <summary>A protocol failure: close with its code and no disconnect message.</summary>
    public static CloseRequest Fail(WebSocketCloseStatus status) => new() { Status = status };
}
