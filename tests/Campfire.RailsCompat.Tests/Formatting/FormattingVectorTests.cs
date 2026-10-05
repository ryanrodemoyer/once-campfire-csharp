using System.Globalization;
using System.Text;
using Campfire.RailsCompat.Formatting;
using Campfire.RailsCompat.Ruby;

namespace Campfire.RailsCompat.Tests.Formatting;

public sealed class FormattingVectorTests
{
    static DateTimeOffset FromNanoseconds(string nanoseconds)
    {
        var value = Int128.Parse(nanoseconds, CultureInfo.InvariantCulture);
        Assert.Equal(0, value % 100);
        return new DateTimeOffset(DateTimeOffset.UnixEpoch.UtcTicks + (long)(value / 100), TimeSpan.Zero);
    }

    [Theory]
    [MemberData(nameof(FormattingVectors.Times), MemberType = typeof(FormattingVectors))]
    public void TimesPrintAsRailsPrintsThem(TimeCase vector)
    {
        var time = FromNanoseconds(vector.Nanoseconds);
        Assert.Equal(vector.Db, ActiveRecordTime.ToDb(time));
        Assert.Equal(vector.Number, TimeFormats.ToFsNumber(time));
        Assert.Equal(vector.Epoch, TimeFormats.ToFsEpoch(time).ToString(CultureInfo.InvariantCulture));
        Assert.Equal(vector.Iso8601, TimeFormats.Iso8601(time));
        Assert.Equal(vector.Usec, TimeFormats.ToFsUsec(time));
        Assert.Equal(vector.FsDb, TimeFormats.ToFsDb(time));
        Assert.Equal(vector.AsJson, TimeFormats.AsJson(time));
    }

    [Theory]
    [MemberData(nameof(FormattingVectors.Times), MemberType = typeof(FormattingVectors))]
    public void WhatRailsWritesReadsBackAsTheTimeRailsHolds(TimeCase vector)
    {
        // Rails writes db; C# reads it, and holds what Rails' attribute holds (whole microseconds).
        var time = ActiveRecordTime.FromDb(vector.Db);
        Assert.Equal(ActiveRecordTime.TruncateToMicroseconds(FromNanoseconds(vector.Nanoseconds)), time);
        Assert.Equal(vector.Db, ActiveRecordTime.ToDb(time!.Value));
    }

    [Theory]
    [MemberData(nameof(FormattingVectors.DbTexts), MemberType = typeof(FormattingVectors))]
    public void DatetimeTextReadsAsActiveRecordReadsIt(DbTextCase vector)
    {
        var time = ActiveRecordTime.FromDb(vector.Input);
        if (vector.Strict)
        {
            Assert.Equal(vector.Nanoseconds, RubyTime.Parse(vector.Input)?.ToString(CultureInfo.InvariantCulture));
            Assert.NotNull(time);
            Assert.Equal(vector.Db, ActiveRecordTime.ToDb(time.Value));
        }
        else
        {
            // Date._parse spellings: nil in Rails too, or never written by Rails or SQLite.
            Assert.Null(time);
        }
    }

    [Fact]
    public void OnlyFallbackSpellingsDiffer()
    {
        var unread = FormattingVectors.File.DbTexts.Where(c => !c.Strict && c.Nanoseconds is not null).Select(c => c.Input);
        Assert.Equal(
            [
                "2026-01-01 12:00:00.", "2026-01-01 12:00:00.x", "2026-01-01 12:00", "2026-01-01 12:00:00 EST",
                "2026-01-01  12:00:00", "2026-01-01 12:00:00 J", "2026-01-01 12:00:00 x", "2026-01-01 12:00:00 UTC+1",
                "2026-01-01 12:00:00+24:00", "2026-01-01 12:00:00+01:60", "202-01-01 12:00:00", "2026-1-01 12:00:00",
                "2026-01-01 1:00:00", "2026-01-01t12:00:00", " 2026-01-01 12:00:00", "2026-01-01 12:00:00 ",
                "2026-01-01 12:00:00\n", "2026-01-01 12:00:00.-5", "2026-01-01 12:00:00z", "2026-01-01 12:00:00+1:00",
                "2026-01-01 12:00:00GMT", "2026-01-01 12:00:00 utc ",
            ],
            unread);
    }

    [Theory]
    [MemberData(nameof(FormattingVectors.Truncations), MemberType = typeof(FormattingVectors))]
    public void TruncatesAsActiveSupportDoes(TruncateCase vector) =>
        Assert.Equal(vector.Result, TextHelpers.Truncate(vector.Text, vector.Length, vector.Omission ?? "...", vector.Separator));

    [Theory]
    [MemberData(nameof(FormattingVectors.Sentences), MemberType = typeof(FormattingVectors))]
    public void BuildsSentencesAsActiveSupportDoes(SentenceCase vector)
    {
        var result = vector.TwoWordsConnector is null
            ? TextHelpers.ToSentence(vector.Items)
            : TextHelpers.ToSentence(vector.Items, twoWordsConnector: vector.TwoWordsConnector);
        Assert.Equal(vector.Result, result);
    }

    [Theory]
    [MemberData(nameof(FormattingVectors.Capitalizations), MemberType = typeof(FormattingVectors))]
    public void CapitalizesAsRubyDoes(StringCase vector) =>
        Assert.Equal(vector.Result, RubyCase.Capitalize(vector.Input));

    [Fact]
    public void EveryCodepointCapitalizesAndDowncasesAsInRuby()
    {
        var capitalize = FormattingVectors.File.CapitalizeCodepoints;
        var downcase = FormattingVectors.File.DowncaseCodepoints;
        var wrong = new List<string>();
        foreach (var rune in AllCodepoints())
        {
            var key = rune.Value.ToString(CultureInfo.InvariantCulture);
            var character = rune.ToString();
            if (RubyCase.Capitalize(character) != capitalize.GetValueOrDefault(key, character)
                || RubyCase.Downcase(character) != downcase.GetValueOrDefault(key, character))
            {
                wrong.Add($"U+{rune.Value:X4}");
            }
        }
        Assert.Empty(wrong);
    }

    [Fact]
    public void EveryCodepointIsAnEmojiOneExactlyWhenRubySaysSo()
    {
        var ranges = FormattingVectors.File.EmojiRanges;
        var wrong = AllCodepoints()
            .Where(rune => StringExtensions.AllEmoji(rune.ToString()) != ranges.Any(r => rune.Value >= r[0] && rune.Value <= r[1]))
            .Select(rune => $"U+{rune.Value:X4}");
        Assert.Empty(wrong);
    }

    [Theory]
    [MemberData(nameof(FormattingVectors.AllEmojiCases), MemberType = typeof(FormattingVectors))]
    public void AllEmojiMatchesTheApp(PredicateCase vector) =>
        Assert.Equal(vector.Result, StringExtensions.AllEmoji(vector.Input));

    [Fact]
    public void VectorsCameFromThePinnedRails()
    {
        Assert.Equal("8.2.0.alpha", FormattingVectors.File.Versions.Rails);
        Assert.Equal("15.0.0", FormattingVectors.File.Versions.Unicode);
    }

    static IEnumerable<Rune> AllCodepoints() =>
        Enumerable.Range(0, 0x110000).Where(c => c is < 0xD800 or > 0xDFFF).Select(c => new Rune(c));
}
