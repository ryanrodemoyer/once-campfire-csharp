using System.Text;

namespace Campfire.Cable.Server;

/// <summary>
/// Broadcasting names: <c>Channel::Naming#channel_name</c> and
/// <c>Channel::Broadcasting#broadcasting_for</c>. Callers pass each broadcastable already
/// converted: records with <c>to_gid_param</c>, symbols and strings as themselves.
/// </summary>
public static class ChannelNaming
{
    /// <summary>
    /// <c>ActionCable::Channel::Naming.channel_name</c>:
    /// <c>name.delete_suffix("Channel").gsub("::", ":").underscore</c>.
    /// </summary>
    public static string ChannelName(string className)
    {
        ArgumentNullException.ThrowIfNull(className);
        var name = className.EndsWith("Channel", StringComparison.Ordinal) ? className[..^"Channel".Length] : className;
        return Underscore(name.Replace("::", ":", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>broadcasting_for</c>: the channel name and each broadcastable, joined with <c>:</c>.
    /// <c>stream_for @room</c> in <c>RoomChannel</c> streams from <c>room:&lt;gid param&gt;</c>.
    /// </summary>
    public static string BroadcastingFor(string className, params IEnumerable<string> broadcastables) =>
        string.Join(':', broadcastables.Prepend(ChannelName(className)));

    /// <summary><c>ActiveSupport::Inflector.underscore</c> without acronyms (Campfire defines none).</summary>
    static string Underscore(string word)
    {
        var chars = word.Replace("::", "/", StringComparison.Ordinal);
        var result = new StringBuilder(chars.Length + 4);
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (char.IsAsciiLetterUpper(c) && i > 0)
            {
                var previous = chars[i - 1];
                var next = i + 1 < chars.Length ? chars[i + 1] : '\0';
                // ([a-z\d])([A-Z]) and ([A-Z\d]+)([A-Z][a-z])
                var lowerBefore = char.IsAsciiLetterLower(previous) || char.IsAsciiDigit(previous);
                var acronymEnd = (char.IsAsciiLetterUpper(previous) || char.IsAsciiDigit(previous)) && char.IsAsciiLetterLower(next);
                if (lowerBefore || acronymEnd)
                {
                    result.Append('_');
                }
            }
            result.Append(c == '-' ? '_' : char.ToLowerInvariant(c));
        }
        return result.ToString();
    }
}
