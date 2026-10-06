using System.Collections;

namespace Campfire.Web.Routing;

/// <summary>
/// A value with a Rails <c>to_param</c>: records implement it to stand in for their id in URL
/// helpers (<c>room_path(room)</c>). A null <see cref="ToParam"/> is a missing segment, like an
/// unsaved record's.
/// </summary>
public interface IToParam
{
    string? ToParam();
}

/// <summary>
/// The keyword options of a Rails URL helper, in the order given: path parameters by name,
/// <c>format</c>, <c>anchor</c>, the reserved URL options <c>host</c>, <c>protocol</c> and
/// <c>port</c>, and anything else, which becomes the query string. Use a collection initializer:
/// <c>new RouteOptions { { "q", query } }</c>.
/// </summary>
public sealed class RouteOptions : IEnumerable<KeyValuePair<string, object?>>
{
    readonly List<KeyValuePair<string, object?>> entries = [];

    public static RouteOptions Empty { get; } = [];

    public int Count => entries.Count;

    public void Add(string key, object? value)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Key == key)
            {
                entries[i] = new(key, value);
                return;
            }
        }
        entries.Add(new(key, value));
    }

    public bool ContainsKey(string key) => entries.Exists(entry => entry.Key == key);

    public object? this[string key] => entries.Find(entry => entry.Key == key).Value;

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Where <c>_url</c> helpers point: <c>SetCurrentRequest#default_url_options</c> gives the
/// request's host and protocol, and ActionController adds <c>request.optional_port</c>.
/// </summary>
/// <param name="Protocol"><c>http</c>, <c>https</c>, or with <c>://</c> as <c>request.protocol</c> spells it.</param>
/// <param name="Host">The host without its port.</param>
/// <param name="Port">The port, if the request used one; the scheme's default is left out.</param>
public sealed record UrlBase(string Protocol, string Host, int? Port = null);
