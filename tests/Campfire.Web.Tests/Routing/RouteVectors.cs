using System.Text.Json;
using Campfire.Vectors;

namespace Campfire.Web.Tests.Routing;

/// <summary>Vectors/routes.json, written by Vectors/generate.rb from the reference's router.</summary>
public sealed record RouteVectorsFile(
    IReadOnlyList<NamedRoute> Routes,
    IReadOnlyList<GenerationCase> Generations,
    IReadOnlyList<DirectCase> Directs,
    IReadOnlyList<PublicExceptionCase> PublicExceptions,
    IReadOnlyList<FormatsCase> Formats);

/// <summary>A <c>bin/rails routes</c> row with its name (null when Rails gives it none).</summary>
public sealed record NamedRoute(string? Name, string Verb, string Path, string Endpoint, IReadOnlyDictionary<string, string> Defaults);

/// <summary><c>helper(*arguments, **options)</c> and the path it returned, or the error it raised.</summary>
public sealed record GenerationCase(string Helper, IReadOnlyList<JsonElement> Arguments, JsonElement Options, JsonElement Path)
{
    public override string ToString() => $"{Helper}({string.Join(", ", Arguments.Select(argument => argument.GetRawText()))}, {Options.GetRawText()})";
}

public sealed record DirectCase(
    string Helper, JsonElement Options, string Path, string? AccountUpdatedAt = null, JsonElement? User = null, IReadOnlyList<JsonElement>? Arguments = null)
{
    public override string ToString() => $"{Helper} {AccountUpdatedAt} {Options.GetRawText()}";
}

public sealed record PublicExceptionCase(int Status, string Json, string Xml)
{
    public override string ToString() => Status.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record FormatsCase(string? Accept, bool Xhr, string Path, string? Format, string? ContentType, IReadOnlyList<string> Formats)
{
    public override string ToString() => $"Accept={Accept ?? "-"} xhr={Xhr} {Path} format={Format ?? "-"} type={ContentType ?? "-"}";
}

public static class RouteVectors
{
    public static string PathOf(string relative) => System.IO.Path.Combine(VectorFiles.Root, "tests/Campfire.Web.Tests/Routing/Vectors", relative);

    static readonly Lazy<RouteVectorsFile> Data = new(() => VectorFiles.Parse<RouteVectorsFile>(System.IO.File.ReadAllBytes(PathOf("routes.json"))));

    public static RouteVectorsFile File => Data.Value;

    public static TheoryData<GenerationCase> Generations() => VectorFiles.Rows(File.Generations, c => c.ToString());

    public static TheoryData<DirectCase> Directs() => VectorFiles.Rows(File.Directs, c => c.ToString());

    public static TheoryData<PublicExceptionCase> PublicExceptions() => VectorFiles.Rows(File.PublicExceptions, c => c.ToString());

    public static TheoryData<FormatsCase> Formats() => VectorFiles.Rows(File.Formats, c => c.ToString());
}
