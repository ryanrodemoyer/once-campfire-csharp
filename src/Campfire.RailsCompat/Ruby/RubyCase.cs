using System.Text;

namespace Campfire.RailsCompat.Ruby;

/// <summary>
/// Ruby's full Unicode case mapping (Unicode 15.0, Ruby 3.4's) where it differs from .NET's invariant
/// simple mapping: special casings that expand (<c>"ß".capitalize</c> is "Ss"), titlecase digraphs
/// (<c>"ǆ"</c> becomes <c>"ǅ"</c>), Georgian, which titlecases to itself, and the letters Unicode 16
/// gave case pairs, which Ruby leaves alone. Checked over every codepoint against Ruby itself.
/// </summary>
public static class RubyCase
{
    static readonly Dictionary<int, string> Titlecase = BuildTitlecase();
    static readonly Dictionary<int, string> Lowercase = BuildLowercase();

    /// <summary><c>String#capitalize</c>: the first character titlecased, the rest downcased, each on its own.</summary>
    public static string Capitalize(string s)
    {
        var builder = new StringBuilder(s.Length);
        var first = true;
        foreach (var rune in s.EnumerateRunes())
        {
            builder.Append(first ? Title(rune) : Down(rune));
            first = false;
        }
        return builder.ToString();
    }

    /// <summary><c>String#downcase</c>.</summary>
    public static string Downcase(string s)
    {
        var builder = new StringBuilder(s.Length);
        foreach (var rune in s.EnumerateRunes())
        {
            builder.Append(Down(rune));
        }
        return builder.ToString();
    }

    static string Title(Rune rune) => Titlecase.TryGetValue(rune.Value, out var mapped) ? mapped : Rune.ToUpperInvariant(rune).ToString();

    static string Down(Rune rune) => Lowercase.TryGetValue(rune.Value, out var mapped) ? mapped : Rune.ToLowerInvariant(rune).ToString();

    static Dictionary<int, string> BuildTitlecase()
    {
        var map = new Dictionary<int, string>
        {
            [0x00DF] = "Ss",
            [0x0131] = "I",
            [0x0149] = "ʼN",
            [0x017F] = "S",
            [0x01F0] = "J̌",
            [0x0390] = "Ϊ́",
            [0x03B0] = "Ϋ́",
            [0x0587] = "Եւ",
            [0x1E96] = "H̱",
            [0x1E97] = "T̈",
            [0x1E98] = "W̊",
            [0x1E99] = "Y̊",
            [0x1E9A] = "Aʾ",
            [0x1F50] = "Υ̓",
            [0x1F52] = "Υ̓̀",
            [0x1F54] = "Υ̓́",
            [0x1F56] = "Υ̓͂",
            [0x1FB2] = "Ὰͅ",
            [0x1FB4] = "Άͅ",
            [0x1FB6] = "Α͂",
            [0x1FB7] = "ᾼ͂",
            [0x1FC2] = "Ὴͅ",
            [0x1FC4] = "Ήͅ",
            [0x1FC6] = "Η͂",
            [0x1FC7] = "ῌ͂",
            [0x1FD2] = "Ϊ̀",
            [0x1FD3] = "Ϊ́",
            [0x1FD6] = "Ι͂",
            [0x1FD7] = "Ϊ͂",
            [0x1FE2] = "Ϋ̀",
            [0x1FE3] = "Ϋ́",
            [0x1FE4] = "Ρ̓",
            [0x1FE6] = "Υ͂",
            [0x1FE7] = "Ϋ͂",
            [0x1FF2] = "Ὼͅ",
            [0x1FF4] = "Ώͅ",
            [0x1FF6] = "Ω͂",
            [0x1FF7] = "ῼ͂",
            [0xFB00] = "Ff",
            [0xFB01] = "Fi",
            [0xFB02] = "Fl",
            [0xFB03] = "Ffi",
            [0xFB04] = "Ffl",
            [0xFB05] = "St",
            [0xFB06] = "St",
            [0xFB13] = "Մն",
            [0xFB14] = "Մե",
            [0xFB15] = "Մի",
            [0xFB16] = "Վն",
            [0xFB17] = "Մխ",
        };
        // Each digraph's three forms (DŽ Dž dž, LJ Lj lj, NJ Nj nj, DZ Dz dz) titlecase to the middle one.
        foreach (var titlecase in new[] { 0x01C5, 0x01C8, 0x01CB, 0x01F2 })
        {
            for (var c = titlecase - 1; c <= titlecase + 1; c++)
            {
                map[c] = char.ConvertFromUtf32(titlecase);
            }
        }
        // Georgian: Mkhedruli titlecases to itself, and Mtavruli to Mkhedruli.
        foreach (var c in Range(0x10D0, 0x10FA).Concat(Range(0x10FD, 0x10FF)))
        {
            map[c] = char.ConvertFromUtf32(c);
            map[c - 0x10D0 + 0x1C90] = char.ConvertFromUtf32(c);
        }
        // Case pairs new in Unicode 16.
        int[] newer = [0x019B, 0x0264, 0x1C8A, 0xA7CD, 0xA7DB, .. Range(0x10D70, 0x10D85)];
        foreach (var c in newer)
        {
            map[c] = char.ConvertFromUtf32(c);
        }
        return map;
    }

    static Dictionary<int, string> BuildLowercase()
    {
        var map = new Dictionary<int, string> { [0x0130] = "i̇" };
        // Case pairs new in Unicode 16.
        int[] newer = [0x1C89, 0xA7CB, 0xA7CC, 0xA7DA, 0xA7DC, .. Range(0x10D50, 0x10D65)];
        foreach (var c in newer)
        {
            map[c] = char.ConvertFromUtf32(c);
        }
        return map;
    }

    static IEnumerable<int> Range(int first, int last) => Enumerable.Range(first, last - first + 1);
}
