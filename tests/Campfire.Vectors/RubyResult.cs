using System.Text.Json;
using System.Text.Json.Serialization;

namespace Campfire.Vectors;

/// <summary>
/// What a Ruby call returned: its value, or the exception class it raised, which the generators
/// write as <c>{"error": "NoMethodError"}</c> in place of the value.
/// </summary>
[JsonConverter(typeof(RubyResultConverterFactory))]
public readonly record struct RubyResult<T>(T Value, string? Error)
{
    public bool Raised => Error is not null;
}

sealed class RubyResultConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(RubyResult<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(RubyResultConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;
}

sealed class RubyResultConverter<T> : JsonConverter<RubyResult<T>>
{
    public override RubyResult<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            return new RubyResult<T>(JsonSerializer.Deserialize<T>(ref reader, options)!, null);
        }
        var raised = JsonSerializer.Deserialize<RaisedError>(ref reader, options)
            ?? throw new JsonException("Expected {\"error\": ...}");
        return new RubyResult<T>(default!, raised.Error);
    }

    public override void Write(Utf8JsonWriter writer, RubyResult<T> value, JsonSerializerOptions options) =>
        throw new NotSupportedException("Vectors are read-only");

    sealed record RaisedError(string Error);
}
