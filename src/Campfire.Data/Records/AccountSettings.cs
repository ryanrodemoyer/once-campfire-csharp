using System.Text.Json;
using System.Text.Json.Nodes;
using Campfire.RailsCompat.Crypto;

namespace Campfire.Data.Records;

// `has_json :settings, restrict_room_creation_to_administrators: false` on Account
// (activemodel's active_model/schematized_json.rb): the `settings` JSON hash, with the schema's
// defaults filled in for keys it doesn't have. Values assigned through it are cast to the
// default's type, so a form's "true"/"false" is stored as a JSON boolean.
public sealed class AccountSettings
{
    public const string RestrictRoomCreationToAdministratorsKey = "restrict_room_creation_to_administrators";

    static readonly string[] BooleanFalseValues = ["0", "f", "F", "false", "FALSE", "off", "OFF"];

    readonly JsonObject data;

    AccountSettings(JsonObject data) => this.data = data;

    // The column's JSON (`ActiveSupport::JSON.decode`, nil when it doesn't parse) with the
    // defaults reverse-merged in: default keys first, then the stored ones, stored values winning.
    // JSON that isn't an object or null can't be merged into, and raises in Rails too.
    public static AccountSettings Parse(string? json)
    {
        JsonNode? node = null;
        if (json is not null && !RailsJson.TryParse(json, out node))
        {
            node = null;
        }
        var stored = node switch
        {
            null => [],
            JsonObject storedObject => storedObject,
            _ => throw new InvalidDataException($"accounts.settings is not a JSON object: {json}"),
        };
        var data = new JsonObject { [RestrictRoomCreationToAdministratorsKey] = false };
        foreach (var (key, value) in stored)
        {
            data[key] = value?.DeepClone();
        }
        return new AccountSettings(data);
    }

    // `restrict_room_creation_to_administrators?`: whether the stored value is `present?`.
    public bool RestrictRoomCreationToAdministrators => IsPresent(data[RestrictRoomCreationToAdministratorsKey]);

    public JsonNode? this[string key] => data[key];

    // `settings = { key => value }`: each key must be in the schema (anything else raises
    // NoMethodError in Rails), and each value is cast with `ActiveModel::Type::Boolean`.
    public AccountSettings Assign(IEnumerable<KeyValuePair<string, JsonNode?>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var assigned = (JsonObject)data.DeepClone();
        foreach (var (key, value) in values)
        {
            if (key != RestrictRoomCreationToAdministratorsKey)
            {
                throw new ArgumentException($"undefined method '{key}=' for account settings", nameof(values));
            }
            assigned[key] = CastBoolean(value) is { } boolean ? JsonValue.Create(boolean) : null;
        }
        return new AccountSettings(assigned);
    }

    // Whether the column's JSON is this hash, which is how Active Record decides the JSON
    // attribute changed in place (`Type::Json#changed_in_place?`): key order and spelling don't
    // matter.
    public bool IsStoredAs(string? json) =>
        json is not null && RailsJson.TryParse(json, out var stored) && JsonNode.DeepEquals(stored, data);

    // What the column holds once saved: `ActiveSupport::JSON.encode` of the hash.
    public string ToJson() => RailsJson.Encode(data);

    // `ActiveModel::Type::Boolean#cast`: nil and "" are nil, the false values are false, and
    // anything else is true.
    static bool? CastBoolean(JsonNode? value)
    {
        if (value is null)
        {
            return null;
        }
        return value.GetValueKind() switch
        {
            JsonValueKind.String when value.GetValue<string>() is var text => text.Length == 0 ? null : !BooleanFalseValues.Contains(text),
            JsonValueKind.False => false,
            JsonValueKind.Number => value.ToJsonString() != "0",
            _ => true,
        };
    }

    // `Object#present?` of a JSON value.
    static bool IsPresent(JsonNode? value) => value switch
    {
        null => false,
        JsonObject obj => obj.Count > 0,
        JsonArray array => array.Count > 0,
        _ => value.GetValueKind() switch
        {
            JsonValueKind.False or JsonValueKind.Null => false,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetValue<string>()),
            _ => true,
        },
    };
}
