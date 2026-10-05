namespace Campfire.Templates.Generator;

/// <summary>
/// Net change in bracket depth (<c>( [ {</c> minus <c>) ] }</c>) across a fragment of C#, skipping
/// strings, characters and comments. The emitter uses it to see where a block expression such as
/// <c>&lt;%= Helper(() =&gt; { %&gt;</c> is closed again by <c>&lt;% }) %&gt;</c>.
/// </summary>
static class CodeDepth
{
    public static int Delta(string code)
    {
        var i = 0;
        return Scan(code, ref i, stopAtUnmatchedBrace: false);
    }

    // Scans code from i. In an interpolation hole (stopAtUnmatchedBrace), returns at the '}' that
    // closes it, leaving i on it.
    static int Scan(string s, ref int i, bool stopAtUnmatchedBrace)
    {
        var depth = 0;
        while (i < s.Length)
        {
            var c = s[i];
            switch (c)
            {
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']':
                    depth--;
                    break;
                case '}':
                    if (stopAtUnmatchedBrace && depth == 0)
                    {
                        return depth;
                    }
                    depth--;
                    break;
                case '/' when At(s, i + 1) == '/':
                    while (i < s.Length && s[i] != '\n')
                    {
                        i++;
                    }
                    continue;
                case '/' when At(s, i + 1) == '*':
                    var end = s.IndexOf("*/", i + 2, System.StringComparison.Ordinal);
                    i = end < 0 ? s.Length : end + 2;
                    continue;
                case '\'':
                    SkipQuoted(s, ref i, '\'');
                    continue;
                case '"' or '$' or '@':
                    if (TrySkipString(s, ref i))
                    {
                        continue;
                    }
                    break;
            }
            i++;
        }
        return depth;
    }

    static bool TrySkipString(string s, ref int i)
    {
        var start = i;
        var dollars = 0;
        var verbatim = false;
        while (i < s.Length && (s[i] == '$' || s[i] == '@'))
        {
            if (s[i] == '$')
            {
                dollars++;
            }
            else
            {
                verbatim = true;
            }
            i++;
        }
        if (At(s, i) != '"')
        {
            i = start;
            return false;
        }

        var quotes = 0;
        while (At(s, i + quotes) == '"')
        {
            quotes++;
        }
        if (quotes >= 3)
        {
            SkipRawString(s, ref i, quotes, dollars);
            return true;
        }

        i++; // opening quote
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '"')
            {
                if (verbatim && At(s, i + 1) == '"')
                {
                    i += 2;
                    continue;
                }
                i++;
                return true;
            }
            if (c == '\\' && !verbatim)
            {
                i += 2;
                continue;
            }
            if (c == '{' && dollars > 0)
            {
                if (At(s, i + 1) == '{')
                {
                    i += 2;
                    continue;
                }
                i++;
                Scan(s, ref i, stopAtUnmatchedBrace: true);
            }
            i++;
        }
        return true;
    }

    static void SkipRawString(string s, ref int i, int quotes, int dollars)
    {
        i += quotes;
        while (i < s.Length)
        {
            if (s[i] == '"')
            {
                var run = 0;
                while (At(s, i + run) == '"')
                {
                    run++;
                }
                i += run;
                if (run >= quotes)
                {
                    return;
                }
                continue;
            }
            if (dollars > 0 && s[i] == '{')
            {
                var run = 0;
                while (At(s, i + run) == '{')
                {
                    run++;
                }
                i += run;
                if (run >= dollars)
                {
                    Scan(s, ref i, stopAtUnmatchedBrace: true);
                    i++;
                }
                continue;
            }
            i++;
        }
    }

    static void SkipQuoted(string s, ref int i, char quote)
    {
        i++;
        while (i < s.Length && s[i] != quote && s[i] != '\n')
        {
            i += s[i] == '\\' ? 2 : 1;
        }
        i++;
    }

    static char At(string s, int i) => i < s.Length ? s[i] : '\0';
}
