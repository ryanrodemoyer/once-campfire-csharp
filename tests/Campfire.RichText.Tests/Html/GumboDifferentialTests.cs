using System.Text;
using System.Text.Json;
using Campfire.RichText.Html;
using Campfire.Vectors;

namespace Campfire.RichText.Tests.Html;

// Oracle/gumbo-trees.jsonl holds the trees Gumbo itself builds, compiled from the nokogiri 1.19.4
// gem as Nokogiri compiles it (Oracle/generate.py), for html5lib-tests' tree-construction inputs
// and seeded random markup. The port has to build the same tree for every one of them.
public sealed class GumboDifferentialTests
{
    sealed record OracleCase(string Context, string Input, string Tree);

    static readonly Lazy<List<OracleCase>> Cases = new(() =>
    {
        var path = Path.Combine(VectorFiles.Root, "tests", "Campfire.RichText.Tests", "Html", "Oracle", "gumbo-trees.jsonl");
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        return File.ReadLines(path).Select(line => JsonSerializer.Deserialize<OracleCase>(line, options)!).ToList();
    });

    [Fact]
    public void Builds_the_trees_gumbo_builds()
    {
        var mismatches = new List<string>();
        foreach (var oracle in Cases.Value)
        {
            var tree = Dump(oracle.Context, oracle.Input);
            if (tree != oracle.Tree)
            {
                mismatches.Add($"context {oracle.Context}, input {JsonSerializer.Serialize(oracle.Input)}:\n{oracle.Tree}---\n{tree}");
            }
        }
        Assert.True(Cases.Value.Count > 3000);
        Assert.True(mismatches.Count == 0, $"{mismatches.Count} trees differ from Gumbo's; the first:\n{mismatches.FirstOrDefault()}");
    }

    // gumbo_dump.c's format
    static string Dump(string context, string input)
    {
        var output = new StringBuilder();
        try
        {
            if (context.Length == 0)
            {
                DumpChildren(output, HtmlParser.ParseDocument(input), 0);
            }
            else
            {
                DumpChildren(output, HtmlParser.ParseFragment(input, Context(context)), 0);
            }
        }
        catch (HtmlParseException e)
        {
            return $"#error {e.Message}\n";
        }
        return output.ToString();
    }

    static FragmentContext Context(string context) => context.Split(':') switch
    {
        ["svg", var name] => new FragmentContext(name, HtmlNamespace.Svg),
        ["math", var name] => new FragmentContext(name, HtmlNamespace.MathMl),
        _ => new FragmentContext(context),
    };

    static void DumpChildren(StringBuilder output, HtmlParentNode node, int depth)
    {
        var indent = new string(' ', depth * 2);
        foreach (var child in node.Children)
        {
            switch (child)
            {
                case HtmlDoctype doctype:
                    output.Append("<!DOCTYPE ").Append(doctype.Name).Append(">\n");
                    break;
                case HtmlText text:
                    output.Append(indent).Append('"').Append(text.Data).Append("\"\n");
                    break;
                case HtmlCData cdata:
                    output.Append(indent).Append("<![CDATA[").Append(cdata.Data).Append("]]>\n");
                    break;
                case HtmlComment comment:
                    output.Append(indent).Append("<!-- ").Append(comment.Data).Append(" -->\n");
                    break;
                case HtmlElement element:
                    var ns = element.Namespace switch
                    {
                        HtmlNamespace.Svg => "svg ",
                        HtmlNamespace.MathMl => "math ",
                        _ => "",
                    };
                    output.Append(indent).Append('<').Append(ns).Append(element.Name).Append(">\n");
                    foreach (var attribute in element.Attributes)
                    {
                        var prefix = attribute.Prefix is null ? "" : attribute.Prefix + " ";
                        output.Append(indent).Append("  ").Append(prefix).Append(attribute.Name)
                            .Append("=\"").Append(attribute.Value).Append("\"\n");
                    }
                    DumpChildren(output, element, depth + 1);
                    break;
            }
        }
    }
}
