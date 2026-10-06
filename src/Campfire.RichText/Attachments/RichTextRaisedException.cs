namespace Campfire.RichText.Attachments;

/// <summary>
/// Something Ruby raises while rendering rich text. <c>message_presentation</c> rescues it and
/// renders nothing, unless the exception's message can't be logged (<see cref="Unloggable"/>), in
/// which case the whole message renders as <c>messages/_unrenderable</c>.
/// </summary>
public sealed class RichTextRaisedException : Exception
{
    public RichTextRaisedException(string message, bool unloggable = false) : base(message)
    {
        Unloggable = unloggable;
    }

    public RichTextRaisedException()
    {
    }

    public RichTextRaisedException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>The message holds invalid UTF-8, so the rescue's own logging raises.</summary>
    public bool Unloggable { get; }
}
