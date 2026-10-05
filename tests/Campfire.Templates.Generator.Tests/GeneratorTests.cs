using Microsoft.CodeAnalysis;

namespace Campfire.Templates.Generator.Tests;

public sealed class GeneratorTests
{
    [Fact]
    public void Implements_methods_in_nested_generic_types_with_the_declaring_file_usings()
    {
        var compiler = TemplateCompiler.Run("""
            using System.Buffers;
            using System.Text;
            using Campfire.Templates;
            using Up = System.String;

            namespace App.Views
            {
                public static partial class Outer<T>
                {
                    internal static partial class Rooms
                    {
                        [ErbTemplate("rooms/show.html.erb.cs")]
                        public static partial void Show(HtmlWriter @out, string @class, int? count, List<string> names);

                        public static string Run()
                        {
                            var buffer = new ArrayBufferWriter<byte>();
                            Show(new HtmlWriter(buffer), "<x>", null, ["a", "b"]);
                            return Encoding.UTF8.GetString(buffer.WrittenSpan);
                        }
                    }
                }
            }
            """,
            ("/app/Views/rooms/show.html.erb.cs", """
                <p class="<%= @class %>"><%= ((Up)"up").ToUpperInvariant() %></p>
                <% foreach (var name in names) { %>
                  <%= name %>:<%= count ?? 0 %>
                <% } %>
                """));

        Assert.Empty(compiler.Problems);
        var run = compiler.Load().GetType("App.Views.Outer`1+Rooms")!.MakeGenericType(typeof(int)) is { } closed
            ? closed.GetMethod("Run")!
            : null;
        Assert.Equal("<p class=\"&lt;x&gt;\">UP</p>\n  a:0\n  b:0\n", (string)run!.Invoke(null, null)!);
    }

    [Fact]
    public void Matches_the_template_path_on_whole_directory_names()
    {
        var compiler = TemplateCompiler.Run(Declaration("show.html.erb.cs"),
            ("/app/Views/rooms/show.html.erb.cs", "rooms"),
            ("/app/Views/rooms/reshow.html.erb.cs", "reshow"));

        Assert.Empty(compiler.Problems);
    }

    [Fact]
    public void Reports_a_missing_template()
    {
        var compiler = TemplateCompiler.Run(Declaration("missing.html.erb.cs"), ("/app/other.html.erb.cs", ""));

        Assert.Contains(compiler.GeneratorDiagnostics, d => d.Id == "CFT001");
    }

    [Fact]
    public void Reports_an_ambiguous_template_path()
    {
        var compiler = TemplateCompiler.Run(Declaration("show.html.erb.cs"),
            ("/a/show.html.erb.cs", ""), ("/b/show.html.erb.cs", ""));

        Assert.Contains(compiler.GeneratorDiagnostics, d => d.Id == "CFT002");
    }

    [Theory]
    [InlineData("static partial void Render(string notAWriter);", "must take exactly one")]
    [InlineData("static partial void Render(HtmlWriter a, HtmlWriter b);", "must take exactly one")]
    [InlineData("public static partial int Render(HtmlWriter w);", "must return void")]
    [InlineData("static partial void Render<T>(HtmlWriter w);", "must not be generic")]
    public void Reports_an_invalid_method(string declaration, string message)
    {
        var compiler = TemplateCompiler.Run($$"""
            using Campfire.Templates;
            static partial class Views
            {
                [ErbTemplate("x.html.erb.cs")] {{declaration}}
            }
            """, ("/x.html.erb.cs", ""));

        var diagnostic = Assert.Single(compiler.GeneratorDiagnostics, d => d.Id == "CFT003");
        Assert.Contains(message, diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void Reports_an_unclosed_block_expression()
    {
        var compiler = TemplateCompiler.Run(Declaration("x.html.erb.cs"),
            ("/x.html.erb.cs", "a\n<%= Wrap(() => { %>\nb\n"));

        var diagnostic = Assert.Single(compiler.GeneratorDiagnostics, d => d.Id == "CFT004");
        Assert.Contains("line 2", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void Maps_template_code_errors_to_the_template_line()
    {
        var compiler = TemplateCompiler.Run(Declaration("x.html.erb.cs"),
            ("/views/x.html.erb.cs", "<p>\n\n<%= undefinedName %>\n</p>\n"));

        var error = Assert.Single(compiler.Problems, d => d.Severity == DiagnosticSeverity.Error);
        var span = error.Location.GetMappedLineSpan();
        Assert.Equal("CS0103", error.Id);
        Assert.Equal("/views/x.html.erb.cs", span.Path);
        Assert.Equal(2, span.StartLinePosition.Line);
    }

    static string Declaration(string path) => $$"""
        using Campfire.Templates;
        static partial class Views
        {
            [ErbTemplate("{{path}}")] static partial void Render(HtmlWriter w);
        }
        """;
}
