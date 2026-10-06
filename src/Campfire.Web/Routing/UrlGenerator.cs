using System.Text;

namespace Campfire.Web.Routing;

/// <summary><c>ActionController::UrlGenerationError</c>: a helper was missing a required segment.</summary>
public sealed class UrlGenerationException : Exception
{
    public UrlGenerationException() : base("No route matches")
    {
    }

    public UrlGenerationException(string message) : base(message)
    {
    }

    public UrlGenerationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// What a named route's <c>_path</c> and <c>_url</c> helpers do in a controller or view, where
/// <c>SetCurrentRequest#default_url_options</c> is never empty, so Rails always takes the
/// unoptimized path: <c>UrlHelper#handle_positional_args</c>, <c>Journey::Formatter#generate</c>,
/// then <c>ActionDispatch::Http::URL.path_for</c> (actionpack <c>routing/route_set.rb</c>,
/// <c>journey/formatter.rb</c>, <c>http/url.rb</c>).
/// </summary>
public static class UrlGenerator
{
    // RouteSet::RESERVED_OPTIONS that the port supports; the rest throw rather than be ignored.
    static readonly HashSet<string> UrlOptionKeys = ["host", "protocol", "port", "anchor"];
    static readonly HashSet<string> UnsupportedReservedKeys =
        ["subdomain", "domain", "tld_length", "trailing_slash", "params", "only_path", "script_name", "original_script_name", "user", "password"];

    /// <summary><c>name_path(*arguments, **options)</c>.</summary>
    public static string Path(Route route, IReadOnlyList<object?> arguments, RouteOptions? options = null)
    {
        var (path, _) = Generate(route, arguments, options ?? RouteOptions.Empty);
        return path;
    }

    /// <summary><c>name_url(*arguments, **options)</c>, from <paramref name="origin"/>'s host.</summary>
    public static string Url(UrlBase origin, Route route, IReadOnlyList<object?> arguments, RouteOptions? options = null)
    {
        options ??= RouteOptions.Empty;
        var (path, _) = Generate(route, arguments, options);
        return HostUrl(origin, options) + path;
    }

    /// <summary>
    /// The scheme, host and port <c>full_url_for</c> puts before a path: the helper's
    /// <c>host:</c>, <c>protocol:</c> and <c>port:</c> options over <paramref name="origin"/>.
    /// </summary>
    public static string HostUrl(UrlBase origin, RouteOptions? options = null)
    {
        options ??= RouteOptions.Empty;
        var protocol = NormalizeProtocol(options.ContainsKey("protocol") ? Convert.ToString(options["protocol"], System.Globalization.CultureInfo.InvariantCulture) : origin.Protocol);
        var host = options.ContainsKey("host") ? UrlQuery.ToParamOrNull(options["host"]) : origin.Host;
        if (string.IsNullOrEmpty(host))
        {
            throw new ArgumentException("Missing host to link to! Please provide the :host parameter, set default_url_options[:host], or set :only_path to true");
        }
        var port = options.ContainsKey("port") ? UrlQuery.ToParamOrNull(options["port"]) : origin.Port?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var url = new StringBuilder(protocol).Append(host);
        if (port is not null && !IsDefaultPort(port, protocol))
        {
            url.Append(':').Append(port);
        }
        return url.ToString();
    }

    /// <summary>The path a direct route's <c>_path</c> keeps of its URL (<c>CustomUrlHelper#call</c>).</summary>
    public static string PathOfUrl(string url)
    {
        // "/" + url.partition(%r{(?<!/)/(?!/)}).last: everything after the first lone slash.
        for (var i = 0; i < url.Length; i++)
        {
            if (url[i] == '/' && (i == 0 || url[i - 1] != '/') && (i + 1 == url.Length || url[i + 1] != '/'))
            {
                return "/" + url[(i + 1)..];
            }
        }
        return "/";
    }

    static (string Path, List<KeyValuePair<string, object?>> Query) Generate(Route route, IReadOnlyList<object?> arguments, RouteOptions inner)
    {
        foreach (var (key, _) in inner)
        {
            if (UnsupportedReservedKeys.Contains(key))
            {
                throw new NotSupportedException($"The {key}: URL option is not supported");
            }
        }

        // options = url_options.merge(route defaults); handle_positional_args; merge!(inner_options)
        var options = new RouteOptions
        {
            { "controller", route.Controller },
            { "action", route.Action },
        };
        foreach (var (key, value) in route.Defaults)
        {
            options.Add(key, value);
        }
        AssignPositionalArguments(route, arguments, inner, options);
        foreach (var (key, value) in inner)
        {
            options.Add(key, value);
        }

        var parts = ParameterizedParts(route, options);
        var missing = route.Spec.RequiredNames.Where(name => !IsPresentFor(route, name, parts)).ToList();
        if (missing.Count > 0)
        {
            throw new UrlGenerationException($"No route matches {route.Endpoint}, missing required keys: [{string.Join(", ", missing.Order(StringComparer.Ordinal).Select(name => ":" + name))}]");
        }

        var query = options
            .Where(pair => !parts.ContainsKey(pair.Key) && route.DefaultFor(pair.Key) is null
                && pair.Key is not ("controller" or "action") && !UrlOptionKeys.Contains(pair.Key))
            .ToList();
        DropTrailingDefaults(route, parts);

        var path = new StringBuilder(route.Spec.Format(parts));
        var queryString = UrlQuery.ToQuery(query);
        if (queryString.Length > 0)
        {
            path.Append('?').Append(queryString);
        }
        if (options.ContainsKey("anchor") && UrlQuery.ToParamOrNull(options["anchor"]) is { } anchor)
        {
            path.Append('#').Append(PathEscaping.EscapeFragment(anchor));
        }
        return (path.ToString(), query);
    }

    // UrlHelper#handle_positional_args: arguments fill the route's segment keys in order, except
    // that with fewer arguments than (non-format) segments, segments the route defaults (or the
    // options name) are skipped. One argument more than that fills :format.
    static void AssignPositionalArguments(Route route, IReadOnlyList<object?> arguments, RouteOptions inner, RouteOptions options)
    {
        if (arguments.Count == 0)
        {
            return;
        }
        var segmentKeys = route.Spec.Names.Distinct().ToList();
        var size = segmentKeys.Contains("format") ? segmentKeys.Count - 1 : segmentKeys.Count;
        if (arguments.Count < size)
        {
            segmentKeys.RemoveAll(key => UrlOptionKeys.Contains(key) || options.ContainsKey(key));
        }
        segmentKeys.RemoveAll(inner.ContainsKey);
        for (var i = 0; i < arguments.Count && i < segmentKeys.Count; i++)
        {
            options.Add(segmentKeys[i], arguments[i]);
        }
    }

    // Formatter#extract_parameterized_parts (no recall: typed helpers always pass required
    // segments): the trailing route parts not given are dropped, required parts are kept, values
    // become to_param, and nils go.
    static Dictionary<string, string> ParameterizedParts(Route route, RouteOptions options)
    {
        var parts = route.Spec.Names;
        var keep = new HashSet<string>(route.Spec.RequiredNames);
        var dropping = true;
        for (var i = parts.Count - 1; i >= 0; i--)
        {
            var part = parts[i];
            if (dropping && options.ContainsKey(part) && options[part] is not null)
            {
                dropping = false;
            }
            if (!dropping)
            {
                keep.Add(part);
            }
        }

        var parameterized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in options)
        {
            if (keep.Contains(key) && UrlQuery.ToParamOrNull(value) is { } param)
            {
                parameterized[key] = param;
            }
        }
        return parameterized;
    }

    // Formatter#missing_keys: a required part needs a value; a glob's must match /\A.+?\Z/m.
    static bool IsPresentFor(Route route, string name, Dictionary<string, string> parts) =>
        parts.TryGetValue(name, out var value) && (!route.Spec.Globs.Contains(name) || value.Length > 0);

    // Formatter#generate: walking the parts from the end, optional parts equal to the route's
    // default are dropped (format: json under the bot scope) until one isn't.
    static void DropTrailingDefaults(Route route, Dictionary<string, string> parts)
    {
        for (var i = route.Spec.Names.Count - 1; i >= 0; i--)
        {
            var key = route.Spec.Names[i];
            var defaultValue = route.DefaultFor(key);
            var present = parts.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);
            if (defaultValue is null && present)
            {
                break;
            }
            if ((value ?? "") != (defaultValue ?? ""))
            {
                continue;
            }
            if (route.Spec.RequiredNames.Contains(key))
            {
                break;
            }
            parts.Remove(key);
        }
    }

    static string NormalizeProtocol(string? protocol)
    {
        if (string.IsNullOrEmpty(protocol))
        {
            return "http://";
        }
        if (protocol == "//")
        {
            return "//";
        }
        var scheme = protocol.EndsWith("://", StringComparison.Ordinal) ? protocol[..^3] : protocol.TrimEnd(':');
        if (scheme.Length == 0 || scheme.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException($"Invalid :protocol option: {protocol}");
        }
        return scheme + "://";
    }

    static bool IsDefaultPort(string port, string protocol) =>
        protocol != "//" && int.TryParse(port, out var number) && number == (protocol == "https://" ? 443 : 80);
}
