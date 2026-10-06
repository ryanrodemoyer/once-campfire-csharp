using System.Reflection;
using System.Runtime.Loader;
using Campfire.Vectors;
using Microsoft.AspNetCore.Http;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Campfire.Web.Tests.Routing;

/// <summary>
/// A feature binds its handler from its own file, as Routes.g.cs describes: these compile
/// Routes.g.cs with such a file against the routing types, as Campfire.Web builds.
/// </summary>
public class HandlerBindingTests
{
    const string feature = """
        using Microsoft.AspNetCore.Http;

        namespace Campfire.Web.Controllers;

        public static class RoomsController
        {
            public static Task Show(HttpContext context)
            {
                context.Response.StatusCode = 299;
                return Task.CompletedTask;
            }
        }
        """;

    [Fact]
    public async Task A_feature_file_binds_its_route_by_implementing_the_hook()
    {
        var binding = """
            namespace Campfire.Web;

            static partial class Routes
            {
                static partial void RoomsShow(ref Microsoft.AspNetCore.Http.RequestDelegate? handler) => handler = Controllers.RoomsController.Show;
            }
            """;
        var compilation = Compile(feature, binding);
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        var assembly = Load(compilation);
        var table = assembly.GetType("Campfire.Web.Routes")!.GetProperty("Table")!.GetValue(null)!;
        var routes = (System.Collections.IEnumerable)table.GetType().GetProperty("Routes")!.GetValue(table)!;
        foreach (dynamic route in routes)
        {
            var context = new DefaultHttpContext();
            var handler = (RequestDelegate)route.Handler;
            if ((string)route.Endpoint == "rooms#show")
            {
                await handler(context);
                Assert.Equal(299, context.Response.StatusCode);
            }
        }
    }

    [Fact]
    public void A_misspelled_hook_doesnt_compile()
    {
        var binding = """
            namespace Campfire.Web;

            static partial class Routes
            {
                static partial void RoomShow(ref Microsoft.AspNetCore.Http.RequestDelegate? handler) => handler = Controllers.RoomsController.Show;
            }
            """;
        Assert.Contains(Compile(feature, binding).GetDiagnostics(TestContext.Current.CancellationToken), diagnostic => diagnostic.Id == "CS0759");
    }

    static CSharpCompilation Compile(params string[] sources)
    {
        var parse = new CSharpParseOptions(LanguageVersion.Latest);
        var generated = Path.Combine(VectorFiles.Root, "src/Campfire.Web/Routes.g.cs");
        var trees = new[] { CSharpSyntaxTree.ParseText(File.ReadAllText(generated), parse, generated), CSharpSyntaxTree.ParseText(implicitUsings, parse) }
            .Concat(sources.Select(source => CSharpSyntaxTree.ParseText(source, parse)));
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => Path.GetFileName(path) is var name && (name.StartsWith("System.", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal) || name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)
                || name is "mscorlib.dll" or "netstandard.dll"))
            .Append(typeof(Campfire.Web.Routing.RouteTable).Assembly.Location)
            .Select(path => MetadataReference.CreateFromFile(path));
        return CSharpCompilation.Create(
            "Campfire.Web.Bound" + Guid.NewGuid().ToString("N"),
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
                // Campfire.Web.dll's own Routes is shadowed by the one compiled here.
                .WithSpecificDiagnosticOptions([new("CS0436", ReportDiagnostic.Suppress)]));
    }

    static Assembly Load(CSharpCompilation compilation)
    {
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        image.Position = 0;
        return new AssemblyLoadContext(compilation.AssemblyName, isCollectible: true).LoadFromStream(image);
    }

    const string implicitUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;
}
