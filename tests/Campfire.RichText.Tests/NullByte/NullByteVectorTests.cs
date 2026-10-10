using System.Text.Json;
using Campfire.RichText.Attachments;
using Campfire.RichText.Html;

namespace Campfire.RichText.Tests.NullByte;

// What Nokogiri 1.19.4 does with a NUL, recorded by generate.rb in the reference image
// (null_byte.json): node[name] = value raises, and Nokogiri::HTML5 replaces a NUL in a name or an
// attribute value with U+FFFD and drops it from text.
public sealed class NullByteVectorTests
{
    static readonly string VectorPath = Path.Combine(AppContext.BaseDirectory, "../../../NullByte/null_byte.json");

    static JsonDocument Vectors() => JsonDocument.Parse(File.ReadAllText(VectorPath));

    public static TheoryData<string> AssignmentNames()
    {
        using var doc = Vectors();
        return Names(doc, "assignments");
    }

    public static TheoryData<string> ParseNames()
    {
        using var doc = Vectors();
        return Names(doc, "parse");
    }

    static TheoryData<string> Names(JsonDocument doc, string property)
    {
        var names = new TheoryData<string>();
        foreach (var item in doc.RootElement.GetProperty(property).EnumerateArray())
        {
            names.Add(item.GetProperty("name").GetString()!);
        }
        return names;
    }

    static JsonElement Case(JsonDocument doc, string property, string name) =>
        doc.RootElement.GetProperty(property).EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    [Theory]
    [MemberData(nameof(AssignmentNames))]
    public void SetAttribute_matches_nokogiris_assignment(string name)
    {
        using var doc = Vectors();
        var vector = Case(doc, "assignments", name);
        var element = HtmlParser.ParseFragment("<p class=\"a\">x</p>").Elements.Single();
        var attribute = vector.GetProperty("attribute").GetString()!;
        var value = vector.GetProperty("value").GetString()!;
        var result = vector.GetProperty("result");

        if (result.TryGetProperty("error", out var error))
        {
            var raised = Assert.Throws<RichTextRaisedException>(() => element.SetAttribute(attribute, value));
            Assert.Equal($"{error.GetString()}: {result.GetProperty("message").GetString()}", raised.Message);
        }
        else
        {
            element.SetAttribute(attribute, value);
        }

        var expected = vector.GetProperty("attributes_after").EnumerateArray()
            .Select(pair => (pair[0].GetString()!, pair[1].GetString()!))
            .ToList();
        var actual = element.Attributes.Select(a => (a.QualifiedName, a.Value)).ToList();
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(ParseNames))]
    public void Parsing_replaces_or_drops_a_nul_as_nokogiri_does(string name)
    {
        using var doc = Vectors();
        var vector = Case(doc, "parse", name);
        var fragment = HtmlParser.ParseFragment(vector.GetProperty("html").GetString()!);

        Assert.Equal(vector.GetProperty("to_html").GetString(), fragment.ToHtml());

        var expected = vector.GetProperty("attributes").EnumerateArray()
            .Select(pair => (pair[0].GetString()!, pair[1].GetString()!))
            .ToList();
        var actual = fragment.Elements.Single().Attributes.Select(a => (a.QualifiedName, a.Value)).ToList();
        Assert.Equal(expected, actual);
    }
}
