using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Campfire.Templates.Generator;

/// <summary>
/// Implements every <c>[ErbTemplate("path")] partial void M(HtmlWriter w, ...)</c> with the
/// compiled body of the <c>AdditionalFiles</c> template whose path ends with <c>path</c>.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class TemplateGenerator : IIncrementalGenerator
{
    static readonly string AttributeName = "Campfire.Templates.ErbTemplateAttribute";
    static readonly string WriterName = "Campfire.Templates.HtmlWriter";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var methods = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttributeName,
            static (node, _) => node is MethodDeclarationSyntax,
            static (ctx, _) => TemplateMethod.From(ctx));

        var files = context.AdditionalTextsProvider.Collect();

        context.RegisterSourceOutput(methods.Combine(files), static (spc, pair) => Execute(spc, pair.Left, pair.Right));
    }

    static void Execute(SourceProductionContext context, TemplateMethod method, ImmutableArray<AdditionalText> files)
    {
        if (method.Error is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.InvalidMethod, method.Location, method.Name, method.Error));
            return;
        }

        var suffix = method.TemplatePath.Replace('\\', '/').TrimStart('/');
        var matches = files.Where(file => Matches(file.Path, suffix)).ToList();
        if (matches.Count == 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.TemplateNotFound, method.Location, suffix));
            return;
        }
        if (matches.Count > 1)
        {
            var paths = string.Join(", ", matches.Select(file => file.Path));
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.TemplateAmbiguous, method.Location, suffix, paths));
            return;
        }

        var file = matches[0];
        var text = file.GetText(context.CancellationToken)?.ToString() ?? "";
        var segments = ErbScanner.MergeText(ErbScanner.Scan(text));
        var emitter = new TemplateEmitter(method.WriterParameter, file.Path, "        ");
        var body = emitter.Emit(segments);
        if (emitter.UnclosedBlockLine > 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.UnclosedBlock, method.Location, suffix, emitter.UnclosedBlockLine));
            return;
        }

        context.AddSource(method.HintName, SourceText.From(method.Wrap(body), Encoding.UTF8));
    }

    static bool Matches(string path, string suffix)
    {
        var normalized = path.Replace('\\', '/');
        return normalized == suffix || normalized.EndsWith("/" + suffix, StringComparison.Ordinal);
    }

    sealed class TemplateMethod
    {
        public string Name { get; private set; } = "";
        public string TemplatePath { get; private set; } = "";
        public string WriterParameter { get; private set; } = "";
        public string HintName { get; private set; } = "";
        public string? Error { get; private set; }
        public Location? Location { get; private set; }

        string usings = "";
        string? ns;
        readonly List<string> containingTypes = [];
        string signature = "";

        public static TemplateMethod From(GeneratorAttributeSyntaxContext ctx)
        {
            var syntax = (MethodDeclarationSyntax)ctx.TargetNode;
            var symbol = (IMethodSymbol)ctx.TargetSymbol;
            var method = new TemplateMethod
            {
                Name = symbol.Name,
                Location = syntax.Identifier.GetLocation(),
                TemplatePath = ctx.Attributes[0].ConstructorArguments.FirstOrDefault().Value as string ?? "",
            };

            var writers = symbol.Parameters.Where(p => p.Type.ToDisplayString() == WriterName).ToList();
            method.Error =
                !syntax.Modifiers.Any(SyntaxKind.PartialKeyword) || syntax.Body is not null || syntax.ExpressionBody is not null
                    ? "must be a partial method declaration without a body"
                : !symbol.ReturnsVoid ? "must return void"
                : symbol.IsGenericMethod ? "must not be generic"
                : writers.Count != 1 ? $"must take exactly one {WriterName} parameter"
                : method.TemplatePath.Length == 0 ? "needs a template path"
                : symbol.ContainingType.ContainingType is null && symbol.ContainingType.TypeKind is not (TypeKind.Class or TypeKind.Struct)
                    ? "must be declared in a class or struct"
                : null;
            if (method.Error is not null)
            {
                return method;
            }

            method.WriterParameter = Escape(writers[0].Name);
            method.usings = Usings(syntax);
            method.ns = symbol.ContainingNamespace.IsGlobalNamespace ? null : symbol.ContainingNamespace.ToDisplayString();
            for (var type = symbol.ContainingType; type is not null; type = type.ContainingType)
            {
                method.containingTypes.Insert(0, TypeDeclaration(type));
            }
            method.signature = Signature(syntax, symbol);
            method.HintName = HintNameFor(symbol);
            return method;
        }

        public string Wrap(string body)
        {
            var source = new StringBuilder();
            source.Append("// <auto-generated/>\n#nullable enable\n");
            source.Append(usings);
            if (ns is not null)
            {
                source.Append("namespace ").Append(ns).Append(";\n");
            }
            foreach (var type in containingTypes)
            {
                source.Append(type).Append("\n{\n");
            }
            source.Append("    ").Append(signature).Append("\n    {\n");
            source.Append(body);
            source.Append("    }\n");
            foreach (var _ in containingTypes)
            {
                source.Append("}\n");
            }
            return source.ToString();
        }

        // The template's code sees the same using directives as the file declaring the method.
        static string Usings(MethodDeclarationSyntax syntax)
        {
            var usings = new StringBuilder();
            foreach (var ancestor in syntax.Ancestors())
            {
                var list = ancestor switch
                {
                    CompilationUnitSyntax unit => unit.Usings,
                    BaseNamespaceDeclarationSyntax nsDecl => nsDecl.Usings,
                    _ => default,
                };
                foreach (var directive in list)
                {
                    if (directive.GlobalKeyword.IsKind(SyntaxKind.None))
                    {
                        usings.Append(directive.WithoutTrivia().ToFullString()).Append('\n');
                    }
                }
            }
            return usings.ToString();
        }

        static string TypeDeclaration(INamedTypeSymbol type)
        {
            var keyword = (type.IsRecord, type.TypeKind) switch
            {
                (true, TypeKind.Struct) => "record struct",
                (true, _) => "record",
                (_, TypeKind.Struct) => "struct",
                (_, TypeKind.Interface) => "interface",
                _ => "class",
            };
            var name = Escape(type.Name);
            if (type.TypeParameters.Length > 0)
            {
                name += "<" + string.Join(", ", type.TypeParameters.Select(p => Escape(p.Name))) + ">";
            }
            return $"partial {keyword} {name}";
        }

        static string Signature(MethodDeclarationSyntax syntax, IMethodSymbol symbol)
        {
            // Modifiers are copied from the declaration: partial methods must repeat them exactly.
            var modifiers = string.Join(" ", syntax.Modifiers.Select(m => m.Text));
            var parameters = string.Join(", ", symbol.Parameters.Select(p =>
            {
                var refKind = p.RefKind switch
                {
                    RefKind.Ref => "ref ",
                    RefKind.Out => "out ",
                    RefKind.In => "in ",
                    _ => "",
                };
                var scoped = p.ScopedKind == ScopedKind.ScopedRef || p.ScopedKind == ScopedKind.ScopedValue ? "scoped " : "";
                var paramsKeyword = p.IsParams ? "params " : "";
                return paramsKeyword + scoped + refKind + p.Type.ToDisplayString(TypeFormat) + " " + Escape(p.Name);
            }));
            return $"{modifiers} void {Escape(symbol.Name)}({parameters})";
        }

        static string HintNameFor(IMethodSymbol symbol)
        {
            var name = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "");
            var hint = new StringBuilder();
            foreach (var c in name)
            {
                hint.Append(char.IsLetterOrDigit(c) || c == '.' || c == '_' ? c : '_');
            }
            return hint.Append(".erb.g.cs").ToString();
        }

        static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat.AddMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

        static string Escape(string identifier) =>
            SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None ? "@" + identifier : identifier;
    }
}
