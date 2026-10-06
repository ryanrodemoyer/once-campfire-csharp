using System.Text;
using System.Text.RegularExpressions;

namespace Campfire.Web.Routing;

/// <summary>
/// A Journey path spec such as <c>/rooms/:room_id/messages(.:format)</c>, compiled the way Rails
/// compiles it (actionpack <c>journey/path/pattern.rb</c> and <c>journey/visitors.rb</c>):
/// <c>:name</c> matches <c>[^/.?]+</c>, <c>*name</c> matches lazily across slashes, and
/// <c>(...)</c> is optional. Recognition uses <see cref="Regex"/>; generation uses
/// <see cref="Format"/>, which drops a group whose parameters aren't all given.
/// </summary>
public sealed class RouteSpec
{
    readonly IReadOnlyList<Node> nodes;

    RouteSpec(string spec, IReadOnlyList<Node> nodes)
    {
        Spec = spec;
        this.nodes = nodes;
        var names = new List<string>();
        var optional = new List<string>();
        CollectNames(nodes, names, optional, inGroup: false);
        Names = names;
        RequiredNames = names.Where(name => !optional.Contains(name)).Distinct().ToArray();
        Globs = CollectGlobs(nodes);
        LiteralPrefix = Prefix(nodes);
        Regex = new Regex(@"\A" + ToRegex(nodes) + @"\Z", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    /// <summary>The spec as <c>bin/rails routes</c> prints it.</summary>
    public string Spec { get; }

    /// <summary>Every parameter in the order the regex captures it (<c>pattern.names</c>).</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>The parameters outside optional groups (<c>route.required_parts</c>).</summary>
    public IReadOnlyList<string> RequiredNames { get; }

    /// <summary>The <c>*glob</c> parameters, whose generation keeps slashes.</summary>
    public IReadOnlyList<string> Globs { get; }

    /// <summary>The literal text before the first parameter or group: a cheap pre-check.</summary>
    public string LiteralPrefix { get; }

    /// <summary>The anchored recognition regex (<c>AnchoredRegexp</c>).</summary>
    public Regex Regex { get; }

    public static RouteSpec Parse(string spec)
    {
        var position = 0;
        var nodes = ParseNodes(spec, ref position, inGroup: false);
        if (position != spec.Length)
        {
            throw new FormatException($"Unbalanced ')' in route spec {spec}");
        }
        return new RouteSpec(spec, nodes);
    }

    /// <summary>
    /// <c>Journey::Format#evaluate</c>: the path with each parameter escaped, a group left out unless
    /// every parameter in it has a value, or null when a required parameter has none.
    /// </summary>
    public string? Format(IReadOnlyDictionary<string, string> parts)
    {
        var path = new StringBuilder();
        return Evaluate(nodes, parts, path) ? path.ToString() : null;
    }

    public override string ToString() => Spec;

    static bool Evaluate(IReadOnlyList<Node> nodes, IReadOnlyDictionary<string, string> parts, StringBuilder path)
    {
        var start = path.Length;
        foreach (var node in nodes)
        {
            switch (node)
            {
                case Literal literal:
                    path.Append(literal.Text);
                    break;
                case Parameter parameter when parts.TryGetValue(parameter.Name, out var value):
                    path.Append(parameter.Glob ? PathEscaping.EscapePath(value) : PathEscaping.EscapeSegment(value));
                    break;
                case Parameter:
                    path.Length = start;
                    return false;
                case Group group:
                    Evaluate(group.Children, parts, path);
                    break;
            }
        }
        return true;
    }

    static string ToRegex(IReadOnlyList<Node> nodes)
    {
        var regex = new StringBuilder();
        foreach (var node in nodes)
        {
            regex.Append(node switch
            {
                Literal { Text: "/" } => "/",
                Literal literal => Regex.Escape(literal.Text),
                Parameter { Glob: true } => "((?s:.+?))",
                Parameter => "([^/.?]+)",
                Group group => "(?:" + ToRegex(group.Children) + ")?",
                _ => throw new InvalidOperationException(),
            });
        }
        return regex.ToString();
    }

    static string Prefix(IReadOnlyList<Node> nodes)
    {
        var prefix = new StringBuilder();
        foreach (var node in nodes)
        {
            if (node is not Literal literal)
            {
                break;
            }
            prefix.Append(literal.Text);
        }
        return prefix.ToString();
    }

    static void CollectNames(IReadOnlyList<Node> nodes, List<string> names, List<string> optional, bool inGroup)
    {
        foreach (var node in nodes)
        {
            if (node is Parameter parameter)
            {
                names.Add(parameter.Name);
                if (inGroup)
                {
                    optional.Add(parameter.Name);
                }
            }
            else if (node is Group group)
            {
                CollectNames(group.Children, names, optional, inGroup: true);
            }
        }
    }

    static string[] CollectGlobs(IReadOnlyList<Node> nodes) =>
        nodes.SelectMany(node => node switch
        {
            Parameter { Glob: true } parameter => [parameter.Name],
            Group group => CollectGlobs(group.Children),
            _ => [],
        }).ToArray();

    // journey/scanner.rb: '/', '.', '(', ')', ':name' and '*name' are tokens; anything else is literal.
    static List<Node> ParseNodes(string spec, ref int position, bool inGroup)
    {
        var nodes = new List<Node>();
        var literal = new StringBuilder();
        void FlushLiteral()
        {
            if (literal.Length > 0)
            {
                nodes.Add(new Literal(literal.ToString()));
                literal.Clear();
            }
        }

        while (position < spec.Length)
        {
            var c = spec[position];
            if (c == ')')
            {
                if (!inGroup)
                {
                    break;
                }
                position++;
                FlushLiteral();
                return nodes;
            }
            position++;
            if (c == '(')
            {
                FlushLiteral();
                nodes.Add(new Group(ParseNodes(spec, ref position, inGroup: true)));
            }
            else if (c is '/' or '.')
            {
                FlushLiteral();
                nodes.Add(new Literal(c.ToString()));
            }
            else if ((c is ':' or '*') && position < spec.Length && IsWordChar(spec[position]))
            {
                FlushLiteral();
                var nameStart = position;
                while (position < spec.Length && IsWordChar(spec[position]))
                {
                    position++;
                }
                nodes.Add(new Parameter(spec[nameStart..position], Glob: c == '*'));
            }
            else
            {
                literal.Append(c);
            }
        }
        if (inGroup)
        {
            throw new FormatException($"Unclosed '(' in route spec {spec}");
        }
        FlushLiteral();
        return nodes;
    }

    static bool IsWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    abstract record Node;

    sealed record Literal(string Text) : Node;

    sealed record Parameter(string Name, bool Glob) : Node;

    sealed record Group(IReadOnlyList<Node> Children) : Node;
}
