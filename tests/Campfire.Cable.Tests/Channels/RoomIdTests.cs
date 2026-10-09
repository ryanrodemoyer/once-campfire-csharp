using System.Text.Json.Nodes;
using Campfire.Cable.Channels;
using Campfire.RailsCompat.Crypto;

namespace Campfire.Cable.Tests.Channels;

/// <summary>
/// Active Record's integer cast for <c>params[:room_id]</c>, including the cases probed against
/// the reference (vertical tab is whitespace, <c>0d</c> is a Ruby integer prefix, nbsp is not).
/// </summary>
public class RoomIdTests
{
    [Fact]
    public void Casts_ids_like_active_record()
    {
        Assert.Equal(5, RoomIds.Cast(Parsed("5")));
        Assert.Equal(5, RoomIds.Cast(Parsed("5.9")));
        Assert.Equal(5, RoomIds.Cast(Parsed("\"5\"")));
        Assert.Equal(1, RoomIds.Cast(Parsed("true")));
        Assert.Equal(0, RoomIds.Cast(Parsed("false")));
        Assert.Null(RoomIds.Cast(Parsed("null")));
        Assert.Null(RoomIds.Cast(Parsed("[]")));
        Assert.Null(RoomIds.Cast(Parsed("{}")));

        Assert.Equal(5, RoomIds.Cast(JsonValue.Create(5.9)));
        Assert.Equal(1, RoomIds.Cast(JsonValue.Create(true)));
        Assert.Equal(12, RoomIds.Cast(JsonValue.Create(" 12abc")));
        Assert.Equal(1000, RoomIds.Cast(JsonValue.Create("1_000")));
        Assert.Equal(-1, RoomIds.Cast(JsonValue.Create("-1")));
        Assert.Null(RoomIds.Cast(JsonValue.Create("abc")));
        Assert.Null(RoomIds.Cast(JsonValue.Create("")));
        Assert.Null(RoomIds.Cast(JsonValue.Create("99999999999999999999")));
        Assert.Equal(5, RoomIds.Cast(JsonValue.Create("\v5")));
        Assert.Equal(12, RoomIds.Cast(JsonValue.Create("0d12")));
        Assert.Null(RoomIds.Cast(JsonValue.Create("\u00a05")));
        Assert.Equal(long.MinValue, RoomIds.Cast(JsonValue.Create("-9223372036854775808")));
    }

    static JsonNode? Parsed(string json)
    {
        Assert.True(RailsJson.TryParse(json, out var node));
        return node;
    }
}
