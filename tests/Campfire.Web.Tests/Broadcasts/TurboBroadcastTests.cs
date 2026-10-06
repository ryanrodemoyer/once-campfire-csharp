using System.Text.Json.Nodes;
using Campfire.Data.Events;
using Campfire.RailsCompat.Crypto;
using Campfire.Web.Broadcasts;
using Campfire.Web.Helpers.Rails;

namespace Campfire.Web.Tests.Broadcasts;

/// <summary><c>broadcast_append_to</c>, <c>broadcast_replace_to</c> and <c>broadcast_remove_to</c>.</summary>
public sealed class TurboBroadcastTests
{
    readonly RecordingSeams seams = new();

    [Fact]
    public void Append_replace_and_remove_render_the_reference_tags()
    {
        TurboBroadcasts.Append(seams, ["gid", "messages"], "messages_rooms_open_1", "<div id=\"m\">Hi &amp; bye</div>");
        TurboBroadcasts.Replace(seams, ["gid", "messages"], "presentation_message_1", "x", new HtmlOptions { { "maintain_scroll", "true" } });
        TurboBroadcasts.Remove(seams, ["gid", "messages"], "message_1");
        TurboBroadcasts.Append(seams, ["", "  "], "nowhere", "nope");

        Assert.Equal(
        [
            "gid:messages " + Encode("<turbo-stream action=\"append\" target=\"messages_rooms_open_1\"><template><div id=\"m\">Hi &amp; bye</div></template></turbo-stream>"),
            "gid:messages " + Encode("<turbo-stream maintain_scroll=\"true\" action=\"replace\" target=\"presentation_message_1\"><template>x</template></turbo-stream>"),
            "gid:messages " + Encode("<turbo-stream action=\"remove\" target=\"message_1\"></turbo-stream>"),
        ],
        seams.Broadcasts.Select(broadcast => $"{broadcast.Stream} {broadcast.Payload}").ToList());
    }

    [Fact]
    public void Targets_are_escaped_and_come_after_action()
    {
        Assert.Equal(
            "<turbo-stream action=\"update\" targets=\"#a &gt; b[data-x=&#39;1&#39;]\"><template></template></turbo-stream>",
            TurboBroadcasts.ActionTag("update", null, null, targets: "#a > b[data-x='1']"));
    }

    static string Encode(string html) => RailsJson.Encode(JsonValue.Create(html));
}
