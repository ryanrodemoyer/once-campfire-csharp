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
/// body.to_plain_text, <c>Editable</c> the lexxy-editor value, <c>Mentioned</c> mentioned user ids
/// and <c>Filtered</c> the presentation filters' HTML.
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

public static class RichTextVectors
{
    static readonly Lazy<RichTextFile> Data = new(() => VectorFiles.Load<RichTextFile>("richtext/expected.json"));

    public static RichTextFile File => Data.Value;

    public static TheoryData<RichTextCase> Cases() => VectorFiles.Rows(File.Cases, c => c.Name);
    public static TheoryData<RichTextWebUrlCase> WebUrls() => VectorFiles.Rows(File.WebUrls, c => $"\"{c.Value}\" on {c.Host}");
}
