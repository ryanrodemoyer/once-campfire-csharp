using System.Collections;

namespace Campfire.Web.Routing;

/// <summary>
/// <c>request.path_parameters</c>: the route's defaults, then <c>controller</c> and
/// <c>action</c>, then the captured segments (a capture replaces a default in place, as
/// <c>Hash#merge</c> does).
/// </summary>
public sealed class RouteParameters : IReadOnlyList<KeyValuePair<string, string>>
{
    readonly List<KeyValuePair<string, string>> entries = [];

    public int Count => entries.Count;

    public KeyValuePair<string, string> this[int index] => entries[index];

    public string? this[string key]
    {
        get
        {
            var index = IndexOf(key);
            return index < 0 ? null : entries[index].Value;
        }
    }

    public bool TryGetValue(string key, out string value)
    {
        var index = IndexOf(key);
        value = index < 0 ? "" : entries[index].Value;
        return index >= 0;
    }

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal void Set(string key, string value)
    {
        var index = IndexOf(key);
        if (index < 0)
        {
            entries.Add(new(key, value));
        }
        else
        {
            entries[index] = new(key, value);
        }
    }

    int IndexOf(string key)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Key == key)
            {
                return i;
            }
        }
        return -1;
    }
}
