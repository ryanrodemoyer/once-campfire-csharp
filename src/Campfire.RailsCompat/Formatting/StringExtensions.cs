using System.Text;

namespace Campfire.RailsCompat.Formatting;

/// <summary>The app's own <c>String</c> extensions (<c>reference/lib/rails_ext/string.rb</c>).</summary>
public static class StringExtensions
{
    /// <summary>
    /// Codepoints of <c>\p{Emoji_Presentation}|\p{Extended_Pictographic}|️</c> in Ruby 3.4's
    /// Onigmo (Unicode 15.0), as inclusive ranges. .NET's regexes have neither property.
    /// </summary>
    static readonly (int First, int Last)[] EmojiRanges =
    [
        (0xA9, 0xA9), (0xAE, 0xAE), (0x203C, 0x203C), (0x2049, 0x2049), (0x2122, 0x2122), (0x2139, 0x2139),
        (0x2194, 0x2199), (0x21A9, 0x21AA), (0x231A, 0x231B), (0x2328, 0x2328), (0x2388, 0x2388), (0x23CF, 0x23CF),
        (0x23E9, 0x23F3), (0x23F8, 0x23FA), (0x24C2, 0x24C2), (0x25AA, 0x25AB), (0x25B6, 0x25B6), (0x25C0, 0x25C0),
        (0x25FB, 0x25FE), (0x2600, 0x2605), (0x2607, 0x2612), (0x2614, 0x2685), (0x2690, 0x2705), (0x2708, 0x2712),
        (0x2714, 0x2714), (0x2716, 0x2716), (0x271D, 0x271D), (0x2721, 0x2721), (0x2728, 0x2728), (0x2733, 0x2734),
        (0x2744, 0x2744), (0x2747, 0x2747), (0x274C, 0x274C), (0x274E, 0x274E), (0x2753, 0x2755), (0x2757, 0x2757),
        (0x2763, 0x2767), (0x2795, 0x2797), (0x27A1, 0x27A1), (0x27B0, 0x27B0), (0x27BF, 0x27BF), (0x2934, 0x2935),
        (0x2B05, 0x2B07), (0x2B1B, 0x2B1C), (0x2B50, 0x2B50), (0x2B55, 0x2B55), (0x3030, 0x3030), (0x303D, 0x303D),
        (0x3297, 0x3297), (0x3299, 0x3299), (0xFE0F, 0xFE0F), (0x1F000, 0x1F0FF), (0x1F10D, 0x1F10F), (0x1F12F, 0x1F12F),
        (0x1F16C, 0x1F171), (0x1F17E, 0x1F17F), (0x1F18E, 0x1F18E), (0x1F191, 0x1F19A), (0x1F1AD, 0x1F1FF), (0x1F201, 0x1F20F),
        (0x1F21A, 0x1F21A), (0x1F22F, 0x1F22F), (0x1F232, 0x1F23A), (0x1F23C, 0x1F23F), (0x1F249, 0x1F53D), (0x1F546, 0x1F64F),
        (0x1F680, 0x1F6FF), (0x1F774, 0x1F77F), (0x1F7D5, 0x1F7FF), (0x1F80C, 0x1F80F), (0x1F848, 0x1F84F), (0x1F85A, 0x1F85F),
        (0x1F888, 0x1F88F), (0x1F8AE, 0x1F8FF), (0x1F90C, 0x1F93A), (0x1F93C, 0x1F945), (0x1F947, 0x1FAFF), (0x1FC00, 0x1FFFD),
    ];

    /// <summary>
    /// <c>String#all_emoji?</c>: <c>/\A(\p{Emoji_Presentation}|\p{Extended_Pictographic}|️)+\z/u</c>,
    /// every codepoint an emoji one (so a ZWJ sequence or a keycap isn't).
    /// </summary>
    public static bool AllEmoji(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }
        foreach (var rune in text.EnumerateRunes())
        {
            if (!IsEmojiCodepoint(rune))
            {
                return false;
            }
        }
        return true;
    }

    internal static bool IsEmojiCodepoint(Rune rune)
    {
        int low = 0, high = EmojiRanges.Length - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            var (first, last) = EmojiRanges[middle];
            if (rune.Value < first)
            {
                high = middle - 1;
            }
            else if (rune.Value > last)
            {
                low = middle + 1;
            }
            else
            {
                return true;
            }
        }
        return false;
    }
}
