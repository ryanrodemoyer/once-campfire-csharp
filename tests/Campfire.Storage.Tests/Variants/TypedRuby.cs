using System.Text.Json;
using Campfire.Storage.Variants;

namespace Campfire.Storage.Tests.Variants;

/// <summary>
/// Reads generate.rb's typed Ruby values (<c>{"hash": [[key, value]]}</c>, <c>{"sym": "webp"}</c>,
/// <c>{"str": "jpg"}</c>) back into transformations, keeping symbols and strings apart.
/// </summary>
static class TypedRuby
{
    public static Transformations Transformations(JsonElement typed) => (Transformations)Value(typed)!;

    static object? Value(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => element.GetInt64(),
        JsonValueKind.Array => element.EnumerateArray().Select(Value).ToArray(),
        JsonValueKind.Object when element.TryGetProperty("sym", out var sym) => new RubySymbol(sym.GetString()!),
        JsonValueKind.Object when element.TryGetProperty("str", out var str) => str.GetString(),
        JsonValueKind.Object when element.TryGetProperty("hash", out var hash) => new Transformations(
            hash.EnumerateArray().Select(pair => KeyValuePair.Create(pair[0].GetString()!, Value(pair[1])))),
        _ => throw new FormatException(element.ToString()),
    };
}
