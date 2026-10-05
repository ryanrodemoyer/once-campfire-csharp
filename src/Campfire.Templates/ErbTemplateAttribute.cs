namespace Campfire.Templates;

/// <summary>
/// Marks a <c>partial void</c> method whose body Campfire.Templates.Generator compiles from an ERB
/// template with C# code. <paramref name="path"/> is matched against the end of an
/// <c>AdditionalFiles</c> path, e.g. <c>"rooms/show.html.erb.cs"</c>. The method must take exactly
/// one <see cref="HtmlWriter"/> parameter; every other parameter is in scope for the template code.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ErbTemplateAttribute(string path) : Attribute
{
    public string Path { get; } = path;
}
