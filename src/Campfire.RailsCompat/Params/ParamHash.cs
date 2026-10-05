using System.Collections;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Campfire.RailsCompat.Params;

/// <summary>
/// A Rails params hash (<c>ActiveSupport::HashWithIndifferentAccess</c>, and the
/// <c>ActionController::Parameters</c> wrapped around it): string keys in insertion order.
/// </summary>
/// <remarks>
/// Values are Ruby-shaped: <c>null</c>, <see cref="string"/>, <see cref="bool"/>,
/// <see cref="long"/>, <see cref="BigInteger"/>, <see cref="double"/>, <see cref="UploadedFile"/>,
/// <see cref="List{T}"/> of values, or a nested <see cref="ParamHash"/>. Query strings and forms
/// only produce strings, nulls, files, arrays and hashes; numbers and booleans come from JSON.
/// </remarks>
public sealed class ParamHash : IEnumerable<KeyValuePair<string, object?>>
{
    readonly OrderedDictionary<string, object?> entries = new(StringComparer.Ordinal);

    public int Count => entries.Count;

    public IEnumerable<string> Keys => entries.Keys;

    /// <summary>Ruby's <c>hash[key]</c>: null when missing. Setting an existing key keeps its position.</summary>
    public object? this[string key]
    {
        get => entries.GetValueOrDefault(key);
        set => entries[key] = value;
    }

    public bool ContainsKey(string key) => entries.ContainsKey(key);

    public bool TryGetValue(string key, out object? value) => entries.TryGetValue(key, out value);

    public bool Remove(string key) => entries.Remove(key);

    public string? GetString(string key) => this[key] as string;

    public ParamHash? GetHash(string key) => this[key] as ParamHash;

    public List<object?>? GetArray(string key) => this[key] as List<object?>;

    public UploadedFile? GetFile(string key) => this[key] as UploadedFile;

    /// <summary>Ruby's <c>Hash#merge!</c>: later values win, existing keys keep their position.</summary>
    public void Merge(ParamHash other)
    {
        foreach (var (key, value) in other)
        {
            this[key] = value;
        }
    }

    /// <summary>
    /// <c>params.require(key)</c>: the value when it's present or <c>false</c>, else
    /// <see cref="ParameterMissingException"/> (<c>reference/app/controllers/*_controller.rb</c>).
    /// </summary>
    public object Require(string key)
    {
        var value = this[key];
        return ParamValues.IsPresent(value) || value is false ? value! : throw new ParameterMissingException(key);
    }

    /// <summary><c>params.require(key)</c> where the value must be a hash, as before a <c>permit</c>.</summary>
    public ParamHash RequireHash(string key) =>
        Require(key) as ParamHash ?? throw new InvalidOperationException($"undefined method 'permit' for param `{key}'");

    /// <summary><c>params.fetch(key)</c>: the value, even when nil, or <see cref="ParameterMissingException"/>.</summary>
    public object? Fetch(string key) => TryGetValue(key, out var value) ? value : throw new ParameterMissingException(key);

    /// <summary><c>params.fetch(key, default)</c>.</summary>
    public object? Fetch(string key, object? defaultValue) => TryGetValue(key, out var value) ? value : defaultValue;

    /// <summary><c>ActionController::Parameters#permit</c>; see <see cref="PermitFilter"/>.</summary>
    public ParamHash Permit(params PermitFilter[] filters) => StrongParameters.Permit(this, filters);

    /// <summary>The hash as JSON, for tests and logging. Files become their filename and content type.</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject();
        foreach (var (key, value) in entries)
        {
            json[key] = ParamValues.ToJson(value);
        }
        return json;
    }

    public override string ToString() => ToJson().ToJsonString(JsonOptions);

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = ParamBuilder.DepthLimit + 2,
    };

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Ruby predicates over param values.</summary>
public static class ParamValues
{
    /// <summary>ActiveSupport's <c>blank?</c>: nil, false, whitespace-only strings and empty collections.</summary>
    public static bool IsBlank(object? value) => value switch
    {
        null => true,
        bool b => !b,
        string s => s.All(char.IsWhiteSpace),
        ParamHash hash => hash.Count == 0,
        List<object?> list => list.Count == 0,
        _ => false,
    };

    public static bool IsPresent(object? value) => !IsBlank(value);

    /// <summary><c>ActionController::Parameters::PERMITTED_SCALAR_TYPES</c>.</summary>
    public static bool IsPermittedScalar(object? value) => value is null or string or bool or long or BigInteger or double or UploadedFile;

    /// <summary>The Ruby class name, as Rails' type errors print it.</summary>
    public static string RubyClassName(object? value) => value switch
    {
        null => "NilClass",
        true => "TrueClass",
        false => "FalseClass",
        string => "String",
        long or BigInteger => "Integer",
        double => "Float",
        UploadedFile => "ActionDispatch::Http::UploadedFile",
        List<object?> => "Array",
        ParamHash => "ActiveSupport::HashWithIndifferentAccess",
        _ => value.GetType().Name,
    };

    public static JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        long n => JsonValue.Create(n),
        BigInteger n => JsonNode.Parse(n.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        double d => JsonValue.Create(d),
        UploadedFile file => new JsonObject
        {
            ["original_filename"] = file.OriginalFilename,
            ["content_type"] = file.ContentType,
        },
        List<object?> list => new JsonArray([.. list.Select(ToJson)]),
        ParamHash hash => hash.ToJson(),
        _ => throw new ArgumentException($"Not a param value: {value.GetType()}", nameof(value)),
    };
}
