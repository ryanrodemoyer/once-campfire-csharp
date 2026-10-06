namespace Campfire.Cable.Server;

/// <summary><c>config.action_cable.*</c> as the production reference runs it.</summary>
public sealed record CableConfig
{
    public bool DisableRequestForgeryProtection { get; init; }

    /// <summary>Exact <c>Origin</c> values accepted in addition to the same-origin rule.</summary>
    public IReadOnlyList<string> AllowedRequestOrigins { get; init; } = [];

    public bool AllowSameOriginAsHost { get; init; } = true;

    /// <summary>
    /// <c>config.assume_ssl</c> (on unless <c>DISABLE_SSL</c>): every request looks like HTTPS, so
    /// the same-origin check compares against <c>https://&lt;host&gt;</c>.
    /// </summary>
    public bool AssumeSsl { get; init; } = true;

    /// <summary>
    /// Frames a connection holds for its client before it counts as lagging and is disconnected
    /// with <c>reconnect: true</c>, instead of silently skipping messages.
    /// </summary>
    public int QueueCapacity { get; init; } = 256;

    /// <summary>
    /// Negotiates <c>permessage-deflate</c> when the browser offers it, as browsers do (Rails
    /// doesn't compress; the frames' text is the same either way).
    /// </summary>
    public bool EnableCompression { get; init; } = true;

    /// <summary>How long to wait for the client's close frame after the server closes.</summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The longest message a client may send; longer ones close the socket with 1009.
    /// websocket-driver takes up to 64 MiB; commands are a few hundred bytes.
    /// </summary>
    public int MaxMessageBytes { get; init; } = 1 << 20;

    /// <summary>The most subscriptions one connection may hold (Rails has no limit).</summary>
    public int MaxSubscriptions { get; init; } = 64;

    /// <summary>The longest identifier a connection may subscribe with (Rails has no limit).</summary>
    public int MaxIdentifierBytes { get; init; } = 4096;
}
