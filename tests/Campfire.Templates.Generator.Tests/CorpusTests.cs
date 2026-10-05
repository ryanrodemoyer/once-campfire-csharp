using System.Reflection;
using System.Text;

namespace Campfire.Templates.Generator.Tests;

// Each Corpus/<case>.rb.erb is a Ruby ERB template and <case>.cs.erb the same template with its
// code translated to C#. <case>.html is the Ruby one rendered by ActionView (Erubi/generate.rb).
// The C# one, compiled by the generator, must render to exactly those bytes.
public sealed class CorpusTests
{
    static readonly string CorpusDirectory = Repository.PathTo("tests/Campfire.Templates.Generator.Tests/Corpus");

    static readonly Lazy<(TemplateCompiler Compiler, Assembly Assembly)> Compiled = new(Compile);

    public static TheoryData<string> Cases() =>
        [.. Directory.GetFiles(CorpusDirectory, "*.cs.erb").Select(CaseName).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Cases))]
    public void Renders_byte_identical_to_ActionView(string name)
    {
        var expected = File.ReadAllBytes(Path.Combine(CorpusDirectory, name + ".html"));

        var actual = Render(name);

        Assert.Equal(Encoding.UTF8.GetString(expected), Encoding.UTF8.GetString(actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Translation_changes_only_the_code_inside_tags(string name)
    {
        var ruby = ErbScannerTests.Ops(File.ReadAllText(Path.Combine(CorpusDirectory, name + ".rb.erb")));
        var csharp = ErbScannerTests.Ops(File.ReadAllText(Path.Combine(CorpusDirectory, name + ".cs.erb")));

        Assert.Equal(Markup(ruby), Markup(csharp));
    }

    [Fact]
    public void Compiles_without_warnings()
    {
        Assert.Empty(Compiled.Value.Compiler.Problems);
    }

    [Fact]
    public void Generated_code_uses_no_reflection()
    {
        var generated = Compiled.Value.Compiler.GeneratedTrees;

        Assert.Equal(Cases().Count, generated.Length);
        foreach (var tree in generated)
        {
            var text = tree.ToString();
            Assert.DoesNotContain("System.Reflection", text, StringComparison.Ordinal);
            Assert.DoesNotContain("typeof(", text, StringComparison.Ordinal);
            Assert.DoesNotContain("GetType(", text, StringComparison.Ordinal);
            Assert.DoesNotContain("dynamic", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Activator", text, StringComparison.Ordinal);
        }
    }

    // Kinds in order plus the exact text: what a translation must leave untouched.
    static List<(string, string)> Markup(List<(string Kind, string Value)> ops) =>
        [.. ops.Select(op => op.Kind == "text" ? op : (op.Kind, ""))];

    static byte[] Render(string name)
    {
        var render = Compiled.Value.Assembly.GetType("Corpus.Templates")!.GetMethod("Render")!;
        return (byte[])render.Invoke(null, [name])!;
    }

    static string CaseName(string path) => Path.GetFileName(path)[..^".cs.erb".Length];

    static (TemplateCompiler, Assembly) Compile()
    {
        var names = Cases().Select(row => (string)row.Data).ToList();
        var declarations = string.Join("\n", names.Select((name, i) =>
            $"    [ErbTemplate(\"{name}.cs.erb\")] static partial void Case{i}(HtmlWriter w);"));
        var dispatch = string.Join("\n", names.Select((name, i) =>
            $"            case \"{name}\": Case{i}(w); break;"));
        var source = $$"""
            using System.Buffers;
            using Campfire.Templates;

            namespace Corpus;

            public static partial class Templates
            {
            {{declarations}}

                public static byte[] Render(string name)
                {
                    var buffer = new ArrayBufferWriter<byte>();
                    var w = new HtmlWriter(buffer);
                    switch (name)
                    {
            {{dispatch}}
                        default: throw new ArgumentException(name);
                    }
                    return buffer.WrittenSpan.ToArray();
                }

                // The same values as CorpusContext in Erubi/generate.rb.
                static string name => "<b>\"Tom\" & 'Jerry'</b>";
                static SafeString safe => new("<i>safe</i>");
                static int[] items => [1, 2];
                static int count => 3;
                static string? nothing => null;
                static bool flag => true;
                static bool off => false;
                static string tag_list(int a, int b) => $"{a},{b}";
                static IHtml wrap(Action body) => new Wrapped("<div>", "</div>", body);
                static IHtml labelled(Action<string> body) => new Wrapped("<p>", "</p>", () => body("label"));

                sealed class Wrapped(string open, string close, Action body) : IHtml
                {
                    public void WriteTo(HtmlWriter writer)
                    {
                        writer.AppendRaw(open);
                        body();
                        writer.AppendRaw(close);
                    }
                }
            }
            """;

        var templates = names
            .Select(name => (Path.Combine(CorpusDirectory, name + ".cs.erb"), File.ReadAllText(Path.Combine(CorpusDirectory, name + ".cs.erb"))))
            .ToArray();
        var compiler = TemplateCompiler.Run(source, templates);
        return (compiler, compiler.Load());
    }
}
