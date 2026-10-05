using System.Text.RegularExpressions;

namespace Campfire.RailsCompat.Params;

/// <summary>
/// One argument to <c>permit</c>. A plain string converts to <see cref="Key"/>, so
/// <c>params.RequireHash("user").Permit("name", "avatar", PermitFilter.AnyHash("settings"))</c> reads
/// like <c>params.require(:user).permit(:name, :avatar, settings: {})</c>.
/// </summary>
public abstract record PermitFilter
{
    /// <summary><c>:name</c>: a scalar, plus its multi-parameter keys like <c>name(1i)</c>.</summary>
    public static PermitFilter Key(string key) => new KeyFilter(key);

    /// <summary><c>name: []</c>: an array of scalars.</summary>
    public static PermitFilter ScalarArray(string key) => new ScalarArrayFilter(key);

    /// <summary><c>name: {}</c>: any hash, keeping scalars, arrays and hashes at every depth.</summary>
    public static PermitFilter AnyHash(string key) => new AnyHashFilter(key);

    /// <summary><c>name: [ ... ]</c>: a hash, an array of hashes, or a <c>fields_for</c>-style hash.</summary>
    public static PermitFilter Nested(string key, params PermitFilter[] filters) => new NestedFilter(key, filters);

    public static implicit operator PermitFilter(string key) => Key(key);

    internal abstract string Name { get; }

    internal sealed record KeyFilter(string Field) : PermitFilter
    {
        internal override string Name => Field;
    }

    internal sealed record ScalarArrayFilter(string Field) : PermitFilter
    {
        internal override string Name => Field;
    }

    internal sealed record AnyHashFilter(string Field) : PermitFilter
    {
        internal override string Name => Field;
    }

    internal sealed record NestedFilter(string Field, PermitFilter[] Filters) : PermitFilter
    {
        internal override string Name => Field;
    }
}

/// <summary>
/// <c>ActionController::Parameters#permit</c> (<c>action_controller/metal/strong_parameters.rb</c>),
/// with <c>action_on_unpermitted_parameters</c> unset as in Campfire's production config: keys that
/// aren't permitted are dropped silently.
/// </summary>
static partial class StrongParameters
{
    public static ParamHash Permit(ParamHash parameters, IEnumerable<PermitFilter> filters)
    {
        var permitted = new ParamHash();
        foreach (var filter in filters)
        {
            if (filter is PermitFilter.KeyFilter key)
            {
                PermittedScalarFilter(parameters, permitted, key.Field);
            }
            else
            {
                HashFilter(parameters, permitted, filter);
            }
        }
        return permitted;
    }

    // permitted_scalar_filter
    static void PermittedScalarFilter(ParamHash parameters, ParamHash permitted, string key)
    {
        if (parameters.TryGetValue(key, out var value) && ParamValues.IsPermittedScalar(value))
        {
            permitted[key] = value;
        }

        foreach (var (candidate, candidateValue) in parameters)
        {
            var match = MultiParameterSuffix().Match(candidate);
            if (match.Success && candidate.AsSpan(0, match.Index).SequenceEqual(key) && ParamValues.IsPermittedScalar(candidateValue))
            {
                permitted[candidate] = candidateValue;
            }
        }
    }

    // hash_filter and permit_value, with explicit_arrays false (permit, not expect).
    static void HashFilter(ParamHash parameters, ParamHash permitted, PermitFilter filter)
    {
        var key = filter.Name;
        if (!parameters.TryGetValue(key, out var value) || value is null or false)
        {
            return;
        }

        var result = filter switch
        {
            PermitFilter.ScalarArrayFilter => value is List<object?> list && list.All(ParamValues.IsPermittedScalar) ? list : null,
            PermitFilter.AnyHashFilter => value is ParamHash hash ? PermitAnyInParameters(hash) : null,
            PermitFilter.NestedFilter nested when value is List<object?> or ParamHash => PermitHashOrArray(value, nested.Filters),
            _ => null,
        };
        if (result is not null)
        {
            permitted[key] = result;
        }
    }

    // permit_hash_or_array: permit_array_of_hashes || permit_hash
    static object? PermitHashOrArray(object value, PermitFilter[] filters)
    {
        switch (value)
        {
            case List<object?> list:
                return list.OfType<ParamHash>().Select(element => (object?)Permit(element, filters)).ToList();
            case ParamHash hash when hash.Any(entry => IsNestedAttribute(entry.Key, entry.Value)):
                // each_nested_attribute: a fields_for-style hash, { "0" => {...}, "1" => {...} }.
                var each = new ParamHash();
                foreach (var (key, element) in hash)
                {
                    if (IsNestedAttribute(key, element))
                    {
                        each[key] = Permit((ParamHash)element!, filters);
                    }
                }
                return each;
            case ParamHash hash:
                return Permit(hash, filters);
            default:
                return null;
        }
    }

    // Parameters.nested_attribute?: /\A-?\d+\z/ keys with hash values.
    static bool IsNestedAttribute(string key, object? value) => value is ParamHash && NumericKey().IsMatch(key);

    static ParamHash PermitAnyInParameters(ParamHash parameters)
    {
        var sanitized = new ParamHash();
        foreach (var (key, value) in parameters)
        {
            if (ParamValues.IsPermittedScalar(value))
            {
                sanitized[key] = value;
            }
            else if (value is List<object?> list)
            {
                sanitized[key] = PermitAnyInArray(list);
            }
            else if (value is ParamHash hash)
            {
                sanitized[key] = PermitAnyInParameters(hash);
            }
        }
        return sanitized;
    }

    static List<object?> PermitAnyInArray(List<object?> array)
    {
        var sanitized = new List<object?>();
        foreach (var element in array)
        {
            if (ParamValues.IsPermittedScalar(element))
            {
                sanitized.Add(element);
            }
            else if (element is List<object?> list)
            {
                sanitized.Add(PermitAnyInArray(list));
            }
            else if (element is ParamHash hash)
            {
                sanitized.Add(PermitAnyInParameters(hash));
            }
        }
        return sanitized;
    }

    // Ruby's \d is ASCII-only and \z is the true end of the string.
    [GeneratedRegex(@"\([0-9]+[if]?\)\z")]
    private static partial Regex MultiParameterSuffix();

    [GeneratedRegex(@"\A-?[0-9]+\z")]
    private static partial Regex NumericKey();
}
