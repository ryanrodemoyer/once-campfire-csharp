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
/// The corpus, with each case's editor value taken from
/// <c>tests/Campfire.RichText.Tests/Editing/Vectors/editable.json</c>: the corpus comes byte for byte
/// from reference-rust, at the Rust port's older pin, and the editor value changed with Rails main
/// (U05, plans/upstream-sync.md). Every other output is the corpus's.
/// </summary>
public static class RichTextVectors
{
    /// <summary>The editor values at this repository's pin, under the repository root.</summary>
    public const string EditablePath = "tests/Campfire.RichText.Tests/Editing/Vectors/editable.json";

    static readonly Lazy<RichTextFile> Data = new(Load);

    public static RichTextFile File => Data.Value;

    public static TheoryData<RichTextCase> Cases() => VectorFiles.Rows(File.Cases, c => c.Name);
    public static TheoryData<RichTextWebUrlCase> WebUrls() => VectorFiles.Rows(File.WebUrls, c => $"\"{c.Value}\" on {c.Host}");

    static RichTextFile Load()
    {
        var corpus = VectorFiles.Load<RichTextFile>("richtext/expected.json");
        var editable = VectorFiles.Parse<RichTextEditableFile>(System.IO.File.ReadAllBytes(Path.Combine(VectorFiles.Root, EditablePath)));
        if (!editable.Cases.Select(c => c.Name).SequenceEqual(corpus.Cases.Select(c => c.Name)))
        {
            throw new InvalidOperationException($"{EditablePath} doesn't list the corpus's cases: run its generate.sh");
        }
        return corpus with { Cases = [.. corpus.Cases.Zip(editable.Cases, (c, e) => c with { Editable = e.Editable })] };
    }

    /// <summary>
    /// Why the port doesn't give the reference's editor value for a case yet, or null. On Rails main
    /// only the Trix editor's <c>as_canonical</c> converts <c>figure[data-trix-attachment]</c>
    /// markup, and Campfire's editor is Lexxy's adapter, which keeps it. The port still converts it
    /// wherever a body is canonicalized (posting, pushing, presenting, editing).
    /// </summary>
    public static string? KnownDifference(string body) =>
        body.Contains("data-trix-attachment", StringComparison.Ordinal)
            ? "Q11: Rails main keeps Trix figure markup when canonicalizing (plans/upstream-sync.md)"
            : null;
}
