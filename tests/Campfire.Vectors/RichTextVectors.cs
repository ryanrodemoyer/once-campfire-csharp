using System.Text.Json.Serialization;

namespace Campfire.Vectors;

// vectors/richtext/expected.json, written by reference-tools/richtext/generate.rb from
// vectors/richtext/inputs.yml (plus generated fuzz and mutation cases): what Campfire's rich text
// pipeline produces for each stored message body.

public sealed record RichTextFile(
    string GeneratedBy,
    string RequestHost,
    IReadOnlyList<RichTextUser> Users,
    IReadOnlyList<long> Rooms,
    [property: JsonPropertyName("signed")] IReadOnlyList<RichTextSignedRecord> SignedRecords,
    IReadOnlyList<RichTextCase> Cases,
    IReadOnlyList<RichTextWebUrlCase> WebUrls);

public sealed record RichTextUser(
    string Key, long Id, string Name, string Title, string AttachableSgid, string UserPath, string AvatarPath);

public sealed record RichTextSignedRecord(string Sgid, string Model, long Id, bool Exists);

/// <summary>
/// The generator's <c>outcome</c>: <c>{"ok": value}</c>, or the exception class and message when
/// the call raised.
/// </summary>
public sealed record Outcome<T>(T? Ok = default, string? Error = null, string? Message = null)
{
    public bool Raised => Error is not null;
}

/// <summary>
/// One stored body: <c>Presentation</c> is MessagesHelper#message_presentation, <c>PlainText</c>
/// body.to_plain_text, <c>Editable</c> the lexxy-editor value (at this repository's pin, see
/// <see cref="RichTextVectors"/>), <c>Mentioned</c> mentioned user ids and <c>Filtered</c> the
/// presentation filters' HTML.
/// </summary>
public sealed record RichTextCase(
    string Name,
    string Host,
    string Body,
    Outcome<string> Presentation,
    string? PresentationRaised,
    string? PresentationRaisedMessage,
    Outcome<string> PlainText,
    Outcome<string> Editable,
    Outcome<IReadOnlyList<long>> Mentioned,
    Outcome<string> Filtered);

public sealed record RichTextWebUrlCase(string Value, string Host, Outcome<string> Result);

public sealed record RichTextEditableFile(string GeneratedBy, string Reference, IReadOnlyList<RichTextEditableCase> Cases);

public sealed record RichTextEditableCase(string Name, Outcome<string> Editable);

/// <summary>
/// The outputs of the corpus at this repository's pin, other than the editor value (that one is
/// <see cref="RichTextEditableFile"/>). <c>vectors/richtext/expected.json</c> comes byte for byte
/// from reference-rust, at the Rust port's older pin.
/// </summary>
public sealed record RichTextPinFile(string GeneratedBy, string Reference, IReadOnlyList<RichTextPinCase> Cases);

public sealed record RichTextPinCase(
    string Name,
    Outcome<string> Presentation,
    string? PresentationRaised,
    string? PresentationRaisedMessage,
    Outcome<string> PlainText,
    Outcome<IReadOnlyList<long>> Mentioned,
    Outcome<string> Filtered);

/// <summary>
/// The corpus, with each case's outputs taken from the files recorded at this repository's pin:
/// the editor value from <c>editable.json</c> (U05) and the other outputs from <c>pin.json</c>
/// (Q11). The stored bodies stay the reference-rust corpus's.
/// </summary>
public static class RichTextVectors
{
    /// <summary>The editor values at this repository's pin, under the repository root.</summary>
    public const string EditablePath = "tests/Campfire.RichText.Tests/Editing/Vectors/editable.json";

    /// <summary>Presentation, plain text, mentions and filtered HTML at this repository's pin.</summary>
    public const string PinPath = "tests/Campfire.RichText.Tests/Vectors/pin.json";

    static readonly Lazy<RichTextFile> Data = new(Load);

    public static RichTextFile File => Data.Value;

    public static TheoryData<RichTextCase> Cases() => VectorFiles.Rows(File.Cases, c => c.Name);
    public static TheoryData<RichTextWebUrlCase> WebUrls() => VectorFiles.Rows(File.WebUrls, c => $"\"{c.Value}\" on {c.Host}");

    static RichTextFile Load()
    {
        var corpus = VectorFiles.Load<RichTextFile>("richtext/expected.json");
        var editable = VectorFiles.Parse<RichTextEditableFile>(System.IO.File.ReadAllBytes(Path.Combine(VectorFiles.Root, EditablePath)));
        var pin = VectorFiles.Parse<RichTextPinFile>(System.IO.File.ReadAllBytes(Path.Combine(VectorFiles.Root, PinPath)));
        if (!editable.Cases.Select(c => c.Name).SequenceEqual(corpus.Cases.Select(c => c.Name)))
        {
            throw new InvalidOperationException($"{EditablePath} doesn't list the corpus's cases: run its generate.sh");
        }
        if (!pin.Cases.Select(c => c.Name).SequenceEqual(corpus.Cases.Select(c => c.Name)))
        {
            throw new InvalidOperationException($"{PinPath} doesn't list the corpus's cases: run its generate.sh");
        }
        var cases = new List<RichTextCase>(corpus.Cases.Count);
        for (var i = 0; i < corpus.Cases.Count; i++)
        {
            var c = corpus.Cases[i];
            var e = editable.Cases[i];
            var p = pin.Cases[i];
            cases.Add(c with
            {
                Editable = e.Editable,
                Presentation = p.Presentation,
                PresentationRaised = p.PresentationRaised,
                PresentationRaisedMessage = p.PresentationRaisedMessage,
                PlainText = p.PlainText,
                Mentioned = p.Mentioned,
                Filtered = p.Filtered,
            });
        }
        return corpus with { Cases = cases };
    }
}
