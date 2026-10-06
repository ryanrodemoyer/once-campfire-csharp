using System.Text;

namespace Campfire.Storage.Blobs;

/// <summary>
/// <c>ActionDispatch::Journey::Router::Utils</c>' escaping of route parameters, as the Active
/// Storage routes apply it to <c>:signed_id</c>-style segments and the <c>*filename</c> glob.
/// </summary>
public static class RouteEscaping
{
    const string subDelimsAndPchar = "-._~!$&'()*+,;=:@";

    /// <summary><c>escape_path</c>: keeps unreserved characters, sub-delims, <c>:</c>, <c>@</c> and <c>/</c>.</summary>
    public static string EscapePath(string value) => Escape(value, keepSlash: true);

    /// <summary><c>escape_segment</c>: <see cref="EscapePath"/>, with <c>/</c> escaped too.</summary>
    public static string EscapeSegment(string value) => Escape(value, keepSlash: false);

    static string Escape(string value, bool keepSlash)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || subDelimsAndPchar.Contains(c, StringComparison.Ordinal) || (keepSlash && c == '/')))
            {
                escaped.Append(c);
            }
            else
            {
                escaped.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        return escaped.ToString();
    }
}
