using System.Text.Json;
using Campfire.Vectors;

namespace Campfire.RailsCompat.Tests.Formatting;

// Formatting/Vectors/formatting.json, written by Formatting/Vectors/generate.rb from the Rails
// revision the reference pins.

public sealed record FormattingFile(
    FormattingVersions Versions,
    IReadOnlyList<TimeCase> Times,
    IReadOnlyList<DbTextCase> DbTexts,
    IReadOnlyList<TruncateCase> Truncate,
    IReadOnlyList<SentenceCase> ToSentence,
    IReadOnlyList<StringCase> Capitalize,
    IReadOnlyDictionary<string, string> CapitalizeCodepoints,
    IReadOnlyDictionary<string, string> DowncaseCodepoints,
    IReadOnlyList<IReadOnlyList<int>> EmojiRanges,
    IReadOnlyList<PredicateCase> AllEmoji);

public sealed record FormattingVersions(string Ruby, string Rails, string Unicode);

/// <summary>A time as nanoseconds since the epoch, and how Rails prints it.</summary>
public sealed record TimeCase(string Nanoseconds, string Db, string Number, string Epoch, string Iso8601, string Usec, string FsDb, string AsJson);

/// <summary>
/// Datetime column text, what Rails reads from it (null for nil) and writes back. <c>Strict</c> is
/// whether <c>Time.new(text, in: "UTC")</c> read it, rather than the <c>Date._parse</c> fallback.
/// </summary>
public sealed record DbTextCase(string Input, bool Strict, string? Nanoseconds, string? Db);

public sealed record TruncateCase(string Text, int Length, string? Omission, string? Separator, string Result);

public sealed record SentenceCase(IReadOnlyList<string> Items, string? TwoWordsConnector, string Result);

public sealed record StringCase(string Input, string Result);

public sealed record PredicateCase(string Input, bool Result);

public static class FormattingVectors
{
    static readonly Lazy<FormattingFile> Data = new(() =>
        VectorFiles.Parse<FormattingFile>(System.IO.File.ReadAllBytes(Path.Combine(VectorFiles.Root, "tests/Campfire.RailsCompat.Tests/Formatting/Vectors/formatting.json"))));

    public static FormattingFile File => Data.Value;

    public static TheoryData<TimeCase> Times() => VectorFiles.Rows(File.Times, c => c.Nanoseconds);

    public static TheoryData<DbTextCase> DbTexts() => VectorFiles.Rows(File.DbTexts, c => JsonSerializer.Serialize(c.Input));

    public static TheoryData<TruncateCase> Truncations() => VectorFiles.Rows(File.Truncate);

    public static TheoryData<SentenceCase> Sentences() => VectorFiles.Rows(File.ToSentence);

    public static TheoryData<StringCase> Capitalizations() => VectorFiles.Rows(File.Capitalize, c => JsonSerializer.Serialize(c.Input));

    public static TheoryData<PredicateCase> AllEmojiCases() => VectorFiles.Rows(File.AllEmoji, c => JsonSerializer.Serialize(c.Input));
}
