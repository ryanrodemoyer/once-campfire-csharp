using System.Text.Json.Nodes;
using Campfire.Data.Events;
using Campfire.RailsCompat.Crypto;
using Campfire.Web.Helpers;

namespace Campfire.Web.Broadcasts;

/// <summary>
/// <c>Turbo::StreamsChannel.broadcast_append_to</c> / <c>broadcast_replace_to</c> /
/// <c>broadcast_remove_to</c> (turbo-rails <c>Turbo::Streams::Broadcasts</c>). Streamables are
/// the stream name parts (a record's GID param, or a symbol as its string). Blank parts are
/// dropped, and nothing is sent when none remain. The rendered <c>&lt;turbo-stream&gt;</c> tag
/// is JSON-encoded the way <c>ActionCable.server.broadcast</c>'s default coder encodes a string.
/// </summary>
public static class TurboBroadcasts
{
    /// <summary><c>broadcast_append_to</c>.</summary>
    public static void Append(IBroadcaster broadcaster, IEnumerable<string> streamables, string target, string html) =>
        Send(broadcaster, streamables, TurboStreamTags.Append(target, html).Value);

    /// <summary>
    /// <c>broadcast_replace_to</c>. Extra attributes (such as <c>maintain_scroll: true</c>) are
    /// written before <c>action</c>, as the tag helper's hash is built.
    /// </summary>
    public static void Replace(IBroadcaster broadcaster, IEnumerable<string> streamables, string target, string html, HtmlOptions? attributes = null) =>
        Send(broadcaster, streamables, attributes is null || attributes.Count == 0
            ? TurboStreamTags.Replace(target, html).Value
            : ActionTag("replace", target, html, attributes));

    /// <summary><c>broadcast_remove_to</c>. A remove carries no template.</summary>
    public static void Remove(IBroadcaster broadcaster, IEnumerable<string> streamables, string target) =>
        Send(broadcaster, streamables, TurboStreamTags.Remove(target).Value);

    /// <summary>Streamables joined with <c>:</c>, blank parts dropped.</summary>
    public static string StreamName(IEnumerable<string> streamables)
    {
        ArgumentNullException.ThrowIfNull(streamables);
        return string.Join(':', streamables.Where(static part => !string.IsNullOrWhiteSpace(part)));
    }

    /// <summary>
    /// <c>turbo_stream_action_tag</c> with caller attributes first, then <c>action</c>, then
    /// <c>target</c> or <c>targets</c>.
    /// </summary>
    public static string ActionTag(string action, string? target, string? html, HtmlOptions? attributes = null, string? targets = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        var options = new HtmlOptions();
        if (attributes is not null)
        {
            foreach (var (key, value) in attributes)
            {
                options[key] = value;
            }
        }

        options["action"] = action;
        if (target is not null)
        {
            options["target"] = target;
        }
        else if (targets is not null)
        {
            options["targets"] = targets;
        }

        object? template = action is "remove" or "refresh" ? null : Tag.Template(new SafeString(html));
        return Tag.Element("turbo-stream", template, options).Value;
    }

    static void Send(IBroadcaster broadcaster, IEnumerable<string> streamables, string html)
    {
        ArgumentNullException.ThrowIfNull(broadcaster);
        var stream = StreamName(streamables);
        if (stream.Length == 0)
        {
            return;
        }

        broadcaster.Broadcast(stream, RailsJson.Encode(JsonValue.Create(html)));
    }
}
