using System.Collections;

namespace Campfire.Web.Pipeline;

/// <summary>
/// <c>response.headers</c>: ordered, with case-insensitive names, one value per name. Setting a
/// name that's there replaces its value in place; setting null removes it, as Rack drops a nil
/// header.
/// </summary>
public sealed class ResponseHeaders : IEnumerable<KeyValuePair<string, string>>
{
    readonly List<KeyValuePair<string, string>> entries = [];

    public string? this[string name]
    {
        get
        {
            var index = IndexOf(name);
            return index < 0 ? null : entries[index].Value;
        }
        set
        {
            var index = IndexOf(name);
            if (value is null)
            {
                if (index >= 0)
                {
                    entries.RemoveAt(index);
                }
            }
            else if (index >= 0)
            {
                entries[index] = new(entries[index].Key, value);
            }
            else
            {
                entries.Add(new(name, value));
            }
        }
    }

    public int Count => entries.Count;

    public bool Contains(string name) => IndexOf(name) >= 0;

    public bool Remove(string name)
    {
        var index = IndexOf(name);
        if (index < 0)
        {
            return false;
        }
        entries.RemoveAt(index);
        return true;
    }

    public void Clear() => entries.Clear();

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    int IndexOf(string name) => entries.FindIndex(entry => entry.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
}
