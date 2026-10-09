using System.Text.Json.Nodes;
using Campfire.Jobs.WebPush;
using Campfire.RichText.Attachments;
using Campfire.RichText.Editing;
using Campfire.RichText.PlainText;
using Campfire.RichText.Sanitize;
using Campfire.Vectors;
using Campfire.Web.Assets;
using Campfire.Web.Helpers;
using Campfire.Web.Routing;

namespace Campfire.RichText.Fuzz;

/// <summary>
/// One output of the pipeline: a value, or a note that it raised. Two outputs agree when both
/// raised or both gave the same value; what was raised is logged by Rails and not compared.
/// </summary>
public readonly record struct Output(bool Raised, string? Value, string? Error = null)
{
    public static Output Ok(string? value) => new(false, value);

    public static Output Failed(string error) => new(true, null, error);

    public bool Agrees(Output other) => Raised == other.Raised && Value == other.Value;

    /// <summary>The generator's <c>outcome</c>: <c>{"ok": value}</c> or <c>{"error": class, "message": text}</c>.</summary>
    public static Output FromOutcome(JsonNode? outcome) =>
        outcome?["error"] is { } error
            ? Failed($"{error.GetValue<string>()}: {outcome["message"]?.GetValue<string>()}")
            : Ok(Scalar(outcome?["ok"]));

    // Strings as they are, null as null, and anything else (mentioned ids) as compact JSON
    static string? Scalar(JsonNode? value) => value switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => value.ToJsonString(),
    };

    public override string ToString() => Raised ? $"raised {Error}" : Value is null ? "nil" : Value;
}

/// <summary>
/// The six outputs <c>reference-tools/richtext/generate.rb</c> records for a stored message body.
/// </summary>
/// <param name="Presentation"><c>message_presentation(message)</c>, raised only when the message is unrenderable.</param>
/// <param name="PresentationRaised">Whether the <c>auto_link(h(filters.apply(...)))</c> call inside it raised (and was rescued).</param>
/// <param name="PlainText"><c>message.body.to_plain_text</c>.</param>
/// <param name="Editable">The <c>&lt;lexxy-editor&gt;</c> value, nil when the editor gets none.</param>
/// <param name="Mentioned"><c>Message::Mentionee#mentioned_users</c>, as a JSON array of ids.</param>
/// <param name="Filtered"><c>ContentFilters::TextMessagePresentationFilters.apply(message.body.body).to_html</c>.</param>
public sealed record PipelineOutputs(
    Output Presentation, Output PresentationRaised, Output PlainText, Output Editable, Output Mentioned, Output Filtered)
{
    public static readonly string[] Names = ["presentation", "presentation_raised", "plain_text", "editable", "mentioned", "filtered"];

    public IEnumerable<(string Name, Output Output)> Each() =>
    [
        (Names[0], Presentation), (Names[1], PresentationRaised), (Names[2], PlainText),
        (Names[3], Editable), (Names[4], Mentioned), (Names[5], Filtered),
    ];

    /// <summary>The names of the outputs that differ from <paramref name="reference"/>'s.</summary>
    public List<string> Disagreements(PipelineOutputs reference) =>
        [.. Each().Zip(reference.Each()).Where(pair => !pair.First.Output.Agrees(pair.Second.Output)).Select(pair => pair.First.Name)];

    /// <summary>What the reference recorded, from a case object of <c>expected.json</c> or the oracle.</summary>
    public static PipelineOutputs FromReference(JsonNode outputs) => new(
        Output.FromOutcome(outputs["presentation"]),
        outputs["presentation_raised"] is { } raised
            ? Output.Failed($"{raised.GetValue<string>()}: {outputs["presentation_raised_message"]?.GetValue<string>()}")
            : Output.Ok(null),
        Output.FromOutcome(outputs["plain_text"]),
        Output.FromOutcome(outputs["editable"]),
        Output.FromOutcome(outputs["mentioned"]),
        Output.FromOutcome(outputs["filtered"]));

    static readonly Lazy<AssetBundle> Assets = new(() => AssetBundle.Build(AssetSources.InRepository(VectorFiles.Root), DateTimeOffset.UnixEpoch));

    /// <summary>What the port produces for <paramref name="body"/>, each output computed on its own as Rails does.</summary>
    public static PipelineOutputs FromPort(string body, RenderContext context) => new(
        Run(() => Present(body, context)),
        Run(() => { MessagePresentation.Render(body, context); return null; }),
        Run(() => RichTextPlainText.ToPlainText(body, context)),
        Run(() => EditableContent.EditorValue(body, context)),
        Run(() => new JsonArray([.. MessagePusher.MentionedUserIds(body, context).Select(id => JsonValue.Create(id))]).ToJsonString()),
        Run(() => ContentFilters.ApplyTextMessagePresentationFilters(body, context.RequestHost)));

    // `message_presentation(message)` as the app gets there: MessageViews computes plain_text_body
    // (which `content_type` reads, inside the helper's rescue in Rails), then View picks the sound
    // or the text branch
    static string Present(string body, RenderContext context)
    {
        var plainTextBody = RichTextPlainText.PlainTextBody(body, null, context);
        var view = new View { Assets = Assets.Value, Origin = new UrlBase("http", context.RequestHost!), RichTextContext = context };
        var message = new MessageView(1, "fuzz", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1, new MessageRoom(1, "Pets"), null, body, plainTextBody, null, []);
        return view.MessagePresentation(message) switch
        {
            Presentation.Html html => html.Value,
            _ => throw new RichTextRaisedException("unrenderable"),
        };
    }

    static Output Run(Func<string?> output)
    {
        try
        {
            return Output.Ok(output());
        }
#pragma warning disable CA1031 // Any exception is an outcome to compare with Rails'.
        catch (Exception e)
#pragma warning restore CA1031
        {
            return Output.Failed($"{e.GetType().Name}: {e.Message}");
        }
    }
}
