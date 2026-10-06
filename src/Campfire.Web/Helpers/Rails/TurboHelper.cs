using Campfire.RailsCompat.Signing;

namespace Campfire.Web.Helpers;

// turbo-rails' view helpers at the revision reference/Gemfile.lock pins (30cd8fc).
// reference: turbo-rails app/helpers/turbo/frames_helper.rb, streams_helper.rb,
// streams/action_helper.rb, app/models/turbo/streams/tag_builder.rb
public partial class View
{
    /// <summary>
    /// <c>turbo_frame_tag(id, src:, target:, **attributes)</c>: an empty frame. Pass record ids
    /// through <see cref="RecordIdentifier.DomId"/>, as <c>turbo_frame_tag(record, :prefix)</c> does.
    /// </summary>
    public static SafeString TurboFrameTag(string id, HtmlOptions? attributes = null, string? src = null, string? target = null) =>
        Tag.Element("turbo-frame", null, FrameAttributes(id, attributes, src, target));

    /// <summary><c>turbo_frame_tag(id, ...) do ... end</c>.</summary>
    public static IHtml TurboFrameTag(string id, HtmlOptions? attributes, Action body, string? src = null, string? target = null) =>
        Tag.Element("turbo-frame", FrameAttributes(id, attributes, src, target), body);

    /// <summary>
    /// <c>turbo_stream_from(*streamables, channel:, **attributes)</c>: a
    /// <c>&lt;turbo-cable-stream-source&gt;</c> with the signed stream name. A streamable is its
    /// <c>to_gid_param</c> for a record (<see cref="RecordIdentifier.GidParam"/>) or its
    /// <c>to_param</c> otherwise (<c>:rooms</c> is "rooms").
    /// </summary>
    public SafeString TurboStreamFrom(IReadOnlyList<string> streamables, string? channel = null, HtmlOptions? attributes = null)
    {
        if (!streamables.Any(RubyValues.IsPresent))
        {
            throw new ArgumentException("streamables can't be blank", nameof(streamables));
        }
        var keys = StreamKeys ?? throw new InvalidOperationException("turbo_stream_from needs View.StreamKeys");

        var options = (attributes ?? []).Clone();
        options["channel"] = channel ?? "Turbo::StreamsChannel";
        options["signed-stream-name"] = TurboStreamName.SignedStreamName(keys, streamables);
        return Tag.Element("turbo-cable-stream-source", null, options);
    }

    static HtmlOptions FrameAttributes(string id, HtmlOptions? attributes, string? src, string? target)
    {
        var merged = (attributes ?? []).Merge(new HtmlOptions { { "id", id }, { "src", src }, { "target", target } });
        var compacted = new HtmlOptions();
        foreach (var (key, value) in merged)
        {
            if (value is not null)
            {
                compacted[key] = value;
            }
        }
        return compacted;
    }
}

/// <summary>
/// <c>turbo_stream</c>, the <c>Turbo::Streams::TagBuilder</c>: <c>&lt;turbo-stream&gt;</c>
/// elements wrapping their content in a <c>&lt;template&gt;</c>. Targets are DOM ids (use
/// <see cref="RecordIdentifier.DomId"/> for a record). Content is rendered HTML; Rails' inferred
/// rendering of a record's partial is the caller's job.
/// </summary>
public static class TurboStreamTags
{
    public static SafeString Append(string target, object? content) => Action("append", target, content);
    public static IHtml Append(string target, Action body) => Action("append", target, body);
    public static SafeString Prepend(string target, object? content) => Action("prepend", target, content);
    public static IHtml Prepend(string target, Action body) => Action("prepend", target, body);
    public static SafeString Replace(string target, object? content, string? method = null) => Action("replace", target, content, method);
    public static IHtml Replace(string target, Action body, string? method = null) => Action("replace", target, body, method);
    public static SafeString Update(string target, object? content, string? method = null) => Action("update", target, content, method);
    public static IHtml Update(string target, Action body, string? method = null) => Action("update", target, body, method);
    public static SafeString Before(string target, object? content) => Action("before", target, content);
    public static SafeString After(string target, object? content) => Action("after", target, content);
    public static SafeString Remove(string target) => ActionTag("remove", target, null, null);

    /// <summary><c>turbo_stream.action(name, target, content)</c>.</summary>
    public static SafeString Action(string name, string target, object? content, string? method = null) =>
        ActionTag(name, target, content, method);

    /// <summary><c>turbo_stream.action(name, target) do ... end</c>.</summary>
    public static IHtml Action(string name, string target, Action body, string? method = null) =>
        new ActionBlock(name, target, method, body);

    /// <summary>
    /// <c>turbo_stream_action_tag(action, target:, template:, method:)</c>. <c>remove</c> and
    /// <c>refresh</c> carry no template.
    /// </summary>
    public static SafeString ActionTag(string action, string? target, object? template, string? method, string? targets = null)
    {
        var templateTag = action is "remove" or "refresh"
            ? SafeString.Empty
            : Tag.Template(new SafeString(RubyValues.ToS(template)));
        return Tag.Element("turbo-stream", templateTag, StreamAttributes(action, target, targets, method));
    }

    static HtmlOptions StreamAttributes(string action, string? target, string? targets, string? method)
    {
        var options = new HtmlOptions { { "method", method }, { "action", action } };
        if (target is not null)
        {
            options["target"] = target;
        }
        else if (targets is not null)
        {
            options["targets"] = targets;
        }
        return options;
    }

    // A template block written straight into the stream tag.
    sealed class ActionBlock(string action, string target, string? method, Action body) : IHtml
    {
        public void WriteTo(HtmlWriter writer)
        {
            writer.AppendRaw($"<turbo-stream{TagHelper.TagOptions(StreamAttributes(action, target, null, method))}><template>");
            body();
            writer.AppendRaw("</template></turbo-stream>");
        }
    }
}
