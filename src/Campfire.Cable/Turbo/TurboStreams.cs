using System.Text.Json.Nodes;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.Signing;

namespace Campfire.Cable.Turbo;

/// <summary>
/// Class-level <c>Turbo::StreamsChannel</c> helpers shared by every user type: the name clients
/// put in an identifier, and <c>verified_stream_name_from_params</c>.
/// </summary>
public static class TurboStreams
{
    /// <summary>The class name clients put in the identifier's <c>channel</c>.</summary>
    public const string ClassName = "Turbo::StreamsChannel";

    /// <summary>
    /// <c>verified_stream_name_from_params</c>. A missing or JSON <c>null</c> name is unverified.
    /// Any other non-string makes <c>MessageVerifier#verified</c> raise (<c>valid_encoding?</c>).
    /// </summary>
    public static string? VerifiedStreamName(KeyGenerator keys, JsonObject parameters)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(parameters);
        if (!parameters.TryGetPropertyValue("signed_stream_name", out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var signed))
        {
            return TurboStreamName.VerifiedStreamName(keys, signed);
        }

        throw new InvalidOperationException($"undefined method 'valid_encoding?' for {node}");
    }
}
