using System.Collections;
using System.Globalization;
using Campfire.RailsCompat.Ruby;

namespace Campfire.Web.Routing;

/// <summary>
/// Active Support's <c>to_param</c> and <c>to_query</c> (<c>core_ext/object/to_query.rb</c>), which
/// build the query string of generated URLs: pairs sorted, arrays as <c>key[]</c>, hashes as
/// <c>key[sub]</c>, every key and value <c>CGI.escape</c>d.
/// </summary>
public static class UrlQuery
{
    /// <summary>
    /// <c>Hash#to_query</c> over top-level <paramref name="parameters"/>, after
    /// <c>ActionDispatch::Http::URL.add_params</c> drops those whose <c>to_param</c> is nil.
    /// </summary>
    public static string ToQuery(IEnumerable<KeyValuePair<string, object?>> parameters) =>
        HashToQuery(parameters.Where(pair => ToParamOrNull(pair.Value) is not null || pair.Value is IDictionary or IEnumerable and not string), null);

    /// <summary>
    /// <c>Object#to_param</c>: strings as they are, integers and floats as Ruby prints them,
    /// <c>true</c>/<c>false</c>, <see cref="IToParam"/> records, arrays joined with <c>/</c>, and null
    /// for nil.
    /// </summary>
    public static string? ToParamOrNull(object? value) => value switch
    {
        null => null,
        string text => text,
        bool flag => flag ? "true" : "false",
        IToParam record => record.ToParam(),
        IFormattable number and (int or long or short or byte or uint or ulong or ushort or sbyte or decimal or System.Numerics.BigInteger) =>
            number.ToString(null, CultureInfo.InvariantCulture),
        double or float => RubyFloat.ToS(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
        IDictionary dictionary => HashToQuery(Pairs(dictionary), null),
        IEnumerable list => string.Join('/', list.Cast<object?>().Select(item => ToParamOrNull(item) ?? "")),
        _ => value.ToString(),
    };

    static string HashToQuery(IEnumerable<KeyValuePair<string, object?>> hash, string? prefix)
    {
        var pairs = new List<string>();
        foreach (var (key, value) in hash)
        {
            if (value is IDictionary { Count: 0 } || (value is ICollection { Count: 0 } and not IDictionary))
            {
                continue;
            }
            pairs.Add(ToQuery(value, prefix is null ? key : $"{prefix}[{key}]"));
        }
        if (prefix is null || !prefix.Contains("[]", StringComparison.Ordinal))
        {
            pairs.Sort(StringComparer.Ordinal);
        }
        return string.Join('&', pairs);
    }

    static string ToQuery(object? value, string key) => value switch
    {
        null => RubyEscape.CgiEscape(key),
        string => $"{RubyEscape.CgiEscape(key)}={RubyEscape.CgiEscape(ToParamOrNull(value)!)}",
        IDictionary dictionary => HashToQuery(Pairs(dictionary), key),
        IEnumerable list => ArrayToQuery(list.Cast<object?>().ToList(), key),
        _ => $"{RubyEscape.CgiEscape(key)}={RubyEscape.CgiEscape(ToParamOrNull(value) ?? "")}",
    };

    static string ArrayToQuery(List<object?> list, string key)
    {
        var prefix = key + "[]";
        return list.Count == 0 ? RubyEscape.CgiEscape(prefix) : string.Join('&', list.Select(item => ToQuery(item, prefix)));
    }

    static IEnumerable<KeyValuePair<string, object?>> Pairs(IDictionary dictionary)
    {
        foreach (DictionaryEntry entry in dictionary)
        {
            yield return new(Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? "", entry.Value);
        }
    }
}
