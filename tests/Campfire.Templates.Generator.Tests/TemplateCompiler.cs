using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Campfire.Templates.Generator.Tests;

/// <summary>
/// Runs the generator in memory over C# sources plus template AdditionalFiles, compiled together
/// with the Campfire.Templates runtime sources, the way a project referencing both builds.
/// </summary>
sealed class TemplateCompiler
{
    static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    static readonly ImmutableArray<MetadataReference> References = [..
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => Path.GetFileName(path).StartsWith("System.", StringComparison.Ordinal)
                || Path.GetFileName(path) is "mscorlib.dll" or "netstandard.dll")
            .Select(path => MetadataReference.CreateFromFile(path))];

    static readonly string ImplicitUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    public required CSharpCompilation Compilation { get; init; }
    public required ImmutableArray<Diagnostic> GeneratorDiagnostics { get; init; }
    public required ImmutableArray<SyntaxTree> GeneratedTrees { get; init; }

    public IEnumerable<Diagnostic> Problems =>
        GeneratorDiagnostics.Concat(Compilation.GetDiagnostics())
            .Where(d => d.Severity >= DiagnosticSeverity.Warning);

    public static TemplateCompiler Run(string source, params (string Path, string Text)[] templates)
    {
        var runtime = Directory.GetFiles(Repository.PathTo("src/Campfire.Templates"), "*.cs")
            .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), ParseOptions, path));
        var trees = runtime
            .Append(CSharpSyntaxTree.ParseText(ImplicitUsings, ParseOptions, "GlobalUsings.cs"))
            .Append(CSharpSyntaxTree.ParseText(source, ParseOptions, "Templates.cs"));
        var compilation = CSharpCompilation.Create(
            "Templates" + Guid.NewGuid().ToString("N"),
            trees,
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new TemplateGenerator().AsSourceGenerator()],
            [.. templates.Select(t => (AdditionalText)new InMemoryText(t.Path, t.Text))],
            ParseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);

        return new TemplateCompiler
        {
            Compilation = (CSharpCompilation)output,
            GeneratorDiagnostics = diagnostics,
            GeneratedTrees = driver.GetRunResult().GeneratedTrees,
        };
    }

    public Assembly Load()
    {
        using var stream = new MemoryStream();
        var result = Compilation.Emit(stream);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        stream.Position = 0;
        return new AssemblyLoadContext(Compilation.AssemblyName, isCollectible: true).LoadFromStream(stream);
    }

    sealed class InMemoryText(string path, string text) : AdditionalText
    {
        public override string Path => path;

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }
}
