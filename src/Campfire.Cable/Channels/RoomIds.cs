using System.Text.Json;
using System.Text.Json.Nodes;
using Campfire.RailsCompat.Ruby;

namespace Campfire.Cable.Channels;

/// <summary>
/// An integer primary key the way Active Record casts <c>find_by(id:)</c>
/// (reference/app/channels/room_channel.rb, activemodel's integer type).
/// Numbers truncate toward zero, booleans are 1 and 0, and strings go through
/// <c>to_i</c> unless they don't start like a number, which matches nothing.
/// Anything out of range matches nothing too.
/// </summary>
public static class RoomIds
{
    /// <summary>The cast id, or null when the value isn't one.</summary>
    public static long? Cast(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        return value.GetValueKind() switch
        {
            JsonValueKind.Number => CastNumber(value),
            JsonValueKind.String => ActiveModelInteger.Cast(value.GetValue<string>()),
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => null,
        };
    }

    static long? CastNumber(JsonValue value)
    {
        // RailsJson keeps a parsed number's text as a JsonElement. A number built in process
        // (a test, a broadcast) is a long or a double instead.
        if (value.TryGetValue<JsonElement>(out var element))
        {
            if (element.TryGetInt64(out var whole))
            {
                return whole;
            }

            return Truncate(element.GetDouble());
        }

        if (value.TryGetValue<long>(out var integer))
        {
            return integer;
        }

        if (value.TryGetValue<int>(out var narrow))
        {
            return narrow;
        }

        if (value.TryGetValue<double>(out var real))
        {
            return Truncate(real);
        }

        return null;
    }

    /// <summary>Ruby <c>Float#truncate</c> into an i64, or null past the range Active Record accepts.</summary>
    static long? Truncate(double value) =>
        double.IsFinite(value) && Math.Abs(value) < 9.2e18 ? (long)Math.Truncate(value) : null;
}
