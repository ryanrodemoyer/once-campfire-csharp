using System.Collections;

namespace Campfire.Web.Helpers.Rails;

/// <summary>
/// The options hash a Rails helper takes (<c>class: "btn", data: { controller: "x" }</c>), with
/// Ruby's hash semantics: keys keep insertion order, assigning an existing key keeps its place,
/// and a new key goes last. Helpers rely on that order, since it is the attribute order Rails
/// writes. Symbol and string keys are the same key here, as they are after <c>stringify_keys</c>.
/// <para>
/// Values are <c>null</c> (skipped), <see cref="string"/> (escaped), <c>SafeString</c> (written as
/// is), <see cref="bool"/>, <see cref="int"/>, <see cref="long"/>, <see cref="double"/>, a nested
/// <see cref="HtmlOptions"/> (for <c>data</c> and <c>aria</c>) or an array of those.
/// </para>
/// </summary>
public sealed class HtmlOptions : IEnumerable<KeyValuePair<string, object?>>
{
    readonly List<KeyValuePair<string, object?>> entries;

    public HtmlOptions() => entries = [];

    HtmlOptions(IEnumerable<KeyValuePair<string, object?>> entries) => this.entries = [.. entries];

    public int Count => entries.Count;

    /// <summary>Reads a value (<c>nil</c> when missing) or assigns one like <c>hash[key] = value</c>.</summary>
    public object? this[string key]
    {
        get => IndexOf(key) is var index and >= 0 ? entries[index].Value : null;
        set
        {
            var index = IndexOf(key);
            if (index >= 0)
            {
                entries[index] = new(key, value);
            }
            else
            {
                entries.Add(new(key, value));
            }
        }
    }

    /// <summary>For collection initializers; a repeated key behaves as in a Ruby hash literal.</summary>
    public void Add(string key, object? value) => this[key] = value;

    public bool ContainsKey(string key) => IndexOf(key) >= 0;

    /// <summary><c>hash.delete(key)</c>: the removed value, or null.</summary>
    public object? Delete(string key)
    {
        var index = IndexOf(key);
        if (index < 0)
        {
            return null;
        }

        var value = entries[index].Value;
        entries.RemoveAt(index);
        return value;
    }

    /// <summary><c>hash.fetch(key) { fallback }</c>.</summary>
    public object? Fetch(string key, Func<object?> fallback) =>
        IndexOf(key) is var index and >= 0 ? entries[index].Value : fallback();

    /// <summary>A shallow copy, like <c>dup</c> or <c>stringify_keys</c>.</summary>
    public HtmlOptions Clone() => new(entries);

    /// <summary><c>merge(other)</c>: a copy with <paramref name="other"/>'s entries assigned in order.</summary>
    public HtmlOptions Merge(HtmlOptions? other)
    {
        var merged = Clone();
        if (other is not null)
        {
            foreach (var (key, value) in other.entries)
            {
                merged[key] = value;
            }
        }
        return merged;
    }

    /// <summary><c>slice(*keys)</c>: the present keys, in the order given.</summary>
    public HtmlOptions Slice(params string[] keys)
    {
        var sliced = new HtmlOptions();
        foreach (var key in keys)
        {
            if (IndexOf(key) is var index and >= 0)
            {
                sliced.entries.Add(entries[index]);
            }
        }
        return sliced;
    }

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    int IndexOf(string key) => entries.FindIndex(entry => entry.Key == key);
}
