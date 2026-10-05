namespace Campfire.RailsCompat.Ruby;

/// <summary>
/// A string as Active Record binds it for an integer column (<c>find</c>, <c>find_by(id:)</c>,
/// <c>where</c>): <c>ActiveModel::Type::Integer#serialize</c> with the SQLite adapter's 8-byte limit
/// (activemodel's <c>type/integer.rb</c>).
/// </summary>
public static class ActiveModelInteger
{
    /// <summary>
    /// Null unless the string starts like a number (<c>/\A\s*[+-]?\d/</c>), then <c>to_i</c>; null too
    /// when that's out of range, where Rails raises.
    /// </summary>
    public static long? Cast(string s)
    {
        var unsigned = s.AsSpan().TrimStart(" \t\n\v\f\r");
        if (unsigned.Length > 0 && unsigned[0] is '+' or '-')
        {
            unsigned = unsigned[1..];
        }
        if (unsigned.Length == 0 || !char.IsAsciiDigit(unsigned[0]))
        {
            return null;
        }
        return RubyString.ToIChecked(s);
    }
}
