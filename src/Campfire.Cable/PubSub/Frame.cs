using System.Text;

namespace Campfire.Cable.PubSub;

/// <summary>
/// A text frame ready for the socket: its UTF-8 bytes, encoded once and shared by every
/// connection that receives it (one broadcast to a room is one <see cref="Frame"/> per channel
/// identifier, however many members are listening).
/// </summary>
public sealed class Frame
{
    public Frame(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
        Utf8 = Encoding.UTF8.GetBytes(text);
    }

    public string Text { get; }

    public ReadOnlyMemory<byte> Utf8 { get; }

    public override string ToString() => Text;
}
