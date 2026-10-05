using System.Text.Json;

namespace Campfire.Templates.Generator.Tests;

// The scanner is checked against what ActionView's Erubi itself produces for every view in
// reference/app/views (Erubi/reference-views.json, written by Erubi/generate.rb). Equal text,
// code and expression streams mean equal output for any data, so this covers every trim and
// indentation pattern the reference templates use.
public sealed class ErbScannerTests
{
    static readonly string Views = Repository.PathTo("reference/app/views");

    public static TheoryData<string> ReferenceViews()
    {
        var golden = ReadGolden();
        return [.. golden.Keys];
    }

    [Fact]
    public void The_golden_file_covers_every_reference_view()
    {
        var views = Directory.GetFiles(Views, "*.erb", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(Views, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);

        Assert.Equal(views, ReadGolden().Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(ReferenceViews))]
    public void Splits_reference_views_exactly_like_Erubi(string view)
    {
        var expected = ReadGolden()[view];
        var actual = Ops(File.ReadAllText(Path.Combine(Views, view)));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Strips_a_leading_encoding_comment_like_ActionView()
    {
        var ops = Ops("<%# encoding: utf-8 %>  <p>x</p>\n");

        Assert.Equal([("text", "<p>x</p>\n")], ops);
    }

    [Fact]
    public void Leaves_an_unterminated_tag_as_text()
    {
        Assert.Equal([("text", "a <% b\n")], Ops("a <% b\n"));
    }

    internal static List<(string Kind, string Value)> Ops(string template) =>
        [.. ErbScanner.MergeText(ErbScanner.Scan(template)).Select(segment => (Kind(segment.Kind), segment.Value))];

    static string Kind(SegmentKind kind) => kind switch
    {
        SegmentKind.Text => "text",
        SegmentKind.Code => "code",
        SegmentKind.Expression => "expr",
        _ => "raw",
    };

    static Dictionary<string, List<(string, string)>> ReadGolden()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Repository.PathTo(
            "tests/Campfire.Templates.Generator.Tests/Erubi/reference-views.json")));
        return json.RootElement.EnumerateObject().ToDictionary(
            view => view.Name,
            view => view.Value.EnumerateArray()
                .Select(op => (op[0].GetString()!, op[1].GetString()!))
                .ToList());
    }
}
