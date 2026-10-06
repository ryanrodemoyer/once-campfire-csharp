using System.Text;

namespace Campfire.Web.Helpers.Rails;

/// <summary>
/// What <c>dom_id</c> and form helpers read from an Active Model record: its Ruby class name
/// (<c>"Rooms::Open"</c>, an STI subclass keeps its own) and its <c>to_key</c> joined with
/// <c>_</c>, which is null for a new record. Most records' key is their id; Message's is its
/// <c>client_message_id</c> (reference/app/models/message.rb).
/// </summary>
public readonly record struct RecordKey(string ModelName, object? Key)
{
    /// <summary>A new, unsaved record of the model.</summary>
    public static RecordKey New(string modelName) => new(modelName, null);

    /// <summary><c>model_name.param_key</c>: <c>"Rooms::Open"</c> is <c>rooms_open</c>.</summary>
    public string ParamKey => RecordIdentifier.Underscore(ModelName).Replace('/', '_');
}

/// <summary>
/// <c>ActionView::RecordIdentifier</c>.
/// reference: actionview/lib/action_view/record_identifier.rb
/// </summary>
public static class RecordIdentifier
{
    /// <summary><c>dom_class(record, prefix)</c>.</summary>
    public static string DomClass(RecordKey record, string? prefix = null) =>
        prefix is null ? record.ParamKey : $"{prefix}_{record.ParamKey}";

    /// <summary><c>dom_id(record, prefix)</c>: <c>room_1</c>, <c>involvement_room_1</c>, <c>new_message</c>.</summary>
    public static string DomId(RecordKey record, string? prefix = null)
    {
        var key = record.Key is null ? null : RubyValues.ToS(record.Key);
        return key is null ? DomClass(record, prefix ?? "new") : $"{DomClass(record, prefix)}_{key}";
    }

    /// <summary>
    /// <c>record.to_gid_param</c>, what <c>turbo_stream_from</c> puts in a stream name for a record.
    /// </summary>
    public static string GidParam(string modelName, object id) =>
        Campfire.RailsCompat.GlobalId.GlobalId.Create(modelName, id).ToParam();

    /// <summary><c>ActiveSupport::Inflector.underscore</c> for model names (no acronyms are defined).</summary>
    public static string Underscore(string camelCased)
    {
        var word = camelCased.Replace("::", "/", StringComparison.Ordinal);
        var output = new StringBuilder(word.Length + 4);
        for (var i = 0; i < word.Length; i++)
        {
            var c = word[i];
            if (char.IsAsciiLetterUpper(c) && i > 0)
            {
                var previous = word[i - 1];
                var next = i + 1 < word.Length ? word[i + 1] : '\0';
                // ([A-Z\d]+)([A-Z][a-z]) and ([a-z\d])([A-Z]) both insert an underscore.
                if (char.IsAsciiLetterLower(previous) || char.IsAsciiDigit(previous) ||
                    (char.IsAsciiLetterUpper(previous) && char.IsAsciiLetterLower(next)))
                {
                    output.Append('_');
                }
            }
            output.Append(c == '-' ? '_' : char.ToLowerInvariant(c));
        }
        return output.ToString();
    }
}
