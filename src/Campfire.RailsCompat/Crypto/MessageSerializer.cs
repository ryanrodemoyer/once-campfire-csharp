using System.Text;
using System.Text.Json.Nodes;

namespace Campfire.RailsCompat.Crypto;

/// <summary>How a verifier or encryptor turns a value into the bytes it signs or encrypts.</summary>
public enum MessageSerializer
{
    /// <summary>
    /// <c>ActiveSupport::MessageEncryptor::NullSerializer</c>: the value is a string of already
    /// serialized bytes (what the cookie jars sign and encrypt). Uses the legacy envelope.
    /// </summary>
    Null,

    /// <summary>The <c>::JSON</c> module (<c>JSON.dump</c>/<c>JSON.load</c>), as signed ids and Turbo stream names use.</summary>
    Json,

    /// <summary><c>SerializerWithFallback[:json]</c> with <c>ActiveSupport::JSON</c>: Marshal payloads are refused.</summary>
    JsonWithFallback,

    /// <summary>
    /// <c>SerializerWithFallback[:json_allow_marshal]</c>, the app's default <c>message_serializer</c>
    /// under <c>load_defaults</c> 7.1+: writes <c>ActiveSupport::JSON</c>, also reads a marshaled String.
    /// </summary>
    JsonAllowMarshal,
}

static class MessageSerializers
{
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static byte[] Dump(this MessageSerializer serializer, JsonNode? value) => Encoding.UTF8.GetBytes(serializer.DumpString(value));

    public static string DumpString(this MessageSerializer serializer, JsonNode? value) => serializer switch
    {
        MessageSerializer.Null => value is JsonValue v && v.TryGetValue<string>(out var s)
            ? s
            : throw new ArgumentException("The null serializer only signs strings", nameof(value)),
        MessageSerializer.Json => RailsJson.Generate(value),
        _ => RailsJson.Encode(value),
    };

    /// <summary>JSON for the envelope's own strings (<c>exp</c>, <c>pur</c>).</summary>
    public static string EncodeJson(this MessageSerializer serializer, JsonNode? value) =>
        serializer == MessageSerializer.Json ? RailsJson.Generate(value) : RailsJson.Encode(value);

    public static MessageResult<JsonNode> Load(this MessageSerializer serializer, ReadOnlySpan<byte> bytes)
    {
        switch (serializer)
        {
            case MessageSerializer.Null:
                try
                {
                    return MessageResult.Ok<JsonNode>(JsonValue.Create(StrictUtf8.GetString(bytes)));
                }
                catch (DecoderFallbackException)
                {
                    return MessageResult.Fail<JsonNode>(MessageError.InvalidMessage);
                }
            // JSON.load("") is nil.
            case MessageSerializer.Json when bytes.IsEmpty:
                return MessageResult.Ok<JsonNode>(null);
            case MessageSerializer.JsonWithFallback or MessageSerializer.JsonAllowMarshal when bytes.StartsWith(RubyMarshal.Signature):
                if (serializer != MessageSerializer.JsonAllowMarshal || RubyMarshal.LoadString(bytes) is not { } marshaled)
                {
                    return MessageResult.Fail<JsonNode>(MessageError.InvalidMessage);
                }
                return MessageResult.Ok<JsonNode>(JsonValue.Create(Encoding.UTF8.GetString(marshaled)));
            default:
                return RailsJson.TryParse(bytes, out var value)
                    ? MessageResult.Ok<JsonNode>(value)
                    : MessageResult.Fail<JsonNode>(MessageError.InvalidMessage);
        }
    }
}
