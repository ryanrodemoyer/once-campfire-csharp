using System.Text;

namespace Campfire.Web.Helpers.Rails;

/// <summary>
/// <c>ActionView::Helpers::OutputSafetyHelper</c> and <c>SafeBuffer</c> concatenation.
/// reference: actionview/lib/action_view/helpers/output_safety_helper.rb
/// </summary>
public static class OutputSafety
{
    /// <summary><c>raw(value)</c>.</summary>
    public static SafeString Raw(object? value) => new(RubyValues.ToS(value));

    /// <summary>
    /// <c>safe_join(array, sep)</c>: flattens, escapes each item and the separator unless
    /// HTML-safe, and joins.
    /// </summary>
    public static SafeString SafeJoin(IEnumerable<object?> items, object? separator = null)
    {
        var sep = RubyValues.UnwrappedHtmlEscape(separator);
        var output = new StringBuilder();
        var first = true;
        foreach (var item in Flatten(items))
        {
            if (!first)
            {
                output.Append(sep);
            }
            output.Append(RubyValues.UnwrappedHtmlEscape(item));
            first = false;
        }
        return new SafeString(output.ToString());
    }

    /// <summary><c>safe_buffer + other</c> (and <c>concat</c>): appends, escaping unless HTML-safe.</summary>
    public static SafeString Concat(params object?[] parts)
    {
        var output = new StringBuilder();
        foreach (var part in parts)
        {
            output.Append(RubyValues.UnwrappedHtmlEscape(part));
        }
        return new SafeString(output.ToString());
    }

    static IEnumerable<object?> Flatten(IEnumerable<object?> items)
    {
        foreach (var item in items)
        {
            if (RubyValues.AsArray(item) is { } nested)
            {
                foreach (var inner in Flatten(nested))
                {
                    yield return inner;
                }
            }
            else
            {
                yield return item;
            }
        }
    }
}
