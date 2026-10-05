using System.Globalization;
using System.Text;

namespace Campfire.Web.Assets;

// JSON string literals as Ruby writes them.
static class RubyJson
{
    // The json gem's generator (JSON.generate, JSON.pretty_generate): only quotes, backslashes and
    // control characters are escaped; "/" and non-ASCII pass through.
    public static string String(string value) => Quote(value, activeSupport: false);

    // ActiveSupport's Object#to_json, which also escapes the HTML-significant <, > and & and the
    // JavaScript line separators U+2028 and U+2029 (escape_html_entities_in_json is on by default).
    public static string ActiveSupportString(string value) => Quote(value, activeSupport: true);

    const char lineSeparator = (char)0x2028;
    const char paragraphSeparator = (char)0x2029;

    static string Quote(string value, bool activeSupport)
    {
        var json = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    json.Append("\\\"");
                    break;
                case '\\':
                    json.Append("\\\\");
                    break;
                case '\n':
                    json.Append("\\n");
                    break;
                case '\r':
                    json.Append("\\r");
                    break;
                case '\t':
                    json.Append("\\t");
                    break;
                case '\b':
                    json.Append("\\b");
                    break;
                case '\f':
                    json.Append("\\f");
                    break;
                case < ' ':
                case '<' or '>' or '&' or lineSeparator or paragraphSeparator when activeSupport:
                    json.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
                    break;
                default:
                    json.Append(c);
                    break;
            }
        }

        return json.Append('"').ToString();
    }
}
