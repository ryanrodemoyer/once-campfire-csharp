namespace Campfire.RailsCompat.Crypto;

/// <summary>Why a signed or encrypted message couldn't be read.</summary>
public enum MessageError
{
    None,

    /// <summary>Malformed message or bad signature (Ruby's <c>:invalid_message_format</c>).</summary>
    InvalidSignature,

    /// <summary>Authentic, but the payload doesn't deserialize (<c>:invalid_message_serialization</c>).</summary>
    InvalidMessage,

    Expired,

    /// <summary>Wrong purpose, or a purpose was expected and the message carries no metadata.</summary>
    PurposeMismatch,
}

/// <summary>A verified or decrypted value, or the <see cref="MessageError"/> that stopped it.</summary>
public readonly record struct MessageResult<T>(T? Value, MessageError Error)
{
    public bool IsValid => Error == MessageError.None;

    /// <summary>
    /// <c>ActiveSupport::Messages::Rotator</c> only falls back to the next rotation on format and
    /// serialization errors. An expired or mismatched message stops at the first one.
    /// </summary>
    internal bool Rotates => Error is MessageError.InvalidSignature or MessageError.InvalidMessage;

    internal MessageResult<TOther> Map<TOther>(Func<T?, TOther?> map) =>
        IsValid ? MessageResult.Ok(map(Value)) : MessageResult.Fail<TOther>(Error);
}

public static class MessageResult
{
    public static MessageResult<T> Ok<T>(T? value) => new(value, MessageError.None);

    public static MessageResult<T> Fail<T>(MessageError error) => new(default, error);
}
