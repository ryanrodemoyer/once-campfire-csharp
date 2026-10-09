using System.Text;
using System.Text.Json;
using Campfire.RailsCompat.Ruby;
using Campfire.Vectors;

namespace Campfire.RichText.Fuzz;

/// <summary>One fuzz case: a stored message body, rendered for a request to <c>Host</c>.</summary>
public sealed record FuzzCase(long Index, string Family, string Host, string Body);

/// <summary>
/// Generates fuzz cases, each from its own seed so any case can be regenerated from its index.
/// The families: the OWASP XSS corpus in every place a body can carry it, malformed nesting,
/// huge inputs, Unicode edge cases, every SGID variant, mutations of the reference cases, and
/// random trees in the style of <c>reference-tools/richtext/generate.rb</c>'s <c>Fuzz</c>.
/// </summary>
/// <remarks>
/// Bodies are always valid Unicode: the column is UTF-8 text the port reads as a .NET string, so
/// a lone surrogate can't reach it (invalid UTF-8 is a known gap of R01).
/// </remarks>
public sealed class FuzzCorpus
{
    public static readonly string[] Families = ["owasp", "nesting", "huge", "unicode", "sgid", "mutation", "tree"];

    // Relative weights, in Families' order: huge inputs are slow on both sides, so they're rarer
    static readonly int[] Weights = [20, 15, 1, 15, 20, 15, 14];

    public static readonly string[] Hosts = ["once.campfire.test", "once.campfire.test", "once.campfire.test", "example.com", "campfire.example.org", "127.0.0.1"];

    static readonly Lazy<string[]> OwaspVectors = new(() =>
        [.. File.ReadLines(Path.Combine(VectorFiles.Root, "tests", "Campfire.RichText.Fuzz", "Corpus", "owasp-xss.jsonl"))
            .Skip(1) // the attribution
            .Select(line => JsonSerializer.Deserialize<string>(line)!)]);

    static readonly Lazy<string[]> ReferenceBodies = new(() => [.. RichTextVectors.File.Cases.Select(c => c.Body)]);

    static readonly Lazy<string[]> WebUrls = new(() => [.. RichTextVectors.File.WebUrls.Select(u => u.Value)]);

    readonly OracleRecords records;
    readonly long seed;

    public FuzzCorpus(OracleRecords records, long seed)
    {
        this.records = records;
        this.seed = seed;
    }

    public static IReadOnlyList<string> Owasp => OwaspVectors.Value;

    public FuzzCase Case(long index)
    {
        var random = new Random(Seed(index));
        var family = Pick(random);
        var host = Hosts[random.Next(Hosts.Length)];
        var body = family switch
        {
            "owasp" => OwaspBody(random),
            "nesting" => NestingBody(random),
            "huge" => HugeBody(random),
            "unicode" => UnicodeBody(random),
            "sgid" => SgidBody(random),
            "mutation" => Mutate(random, Choose(random, ReferenceBodies.Value)),
            _ => Tree(random, 0),
        };
        return new FuzzCase(index, family, host, WellFormed(body));
    }

    // SplitMix64 of the run's seed and the index, so neighbouring cases are unrelated
    int Seed(long index)
    {
        var z = unchecked((ulong)seed * 0x9E3779B97F4A7C15UL + (ulong)index);
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        return (int)(z ^ (z >> 31));
    }

    static string Pick(Random random)
    {
        var roll = random.Next(Weights.Sum());
        for (var i = 0; i < Weights.Length; i++)
        {
            if ((roll -= Weights[i]) < 0)
            {
                return Families[i];
            }
        }
        return Families[^1];
    }

    static T Choose<T>(Random random, IReadOnlyList<T> items) => items[random.Next(items.Count)];

    static bool Chance(Random random, double probability) => random.NextDouble() < probability;

    // Replaces lone surrogates, which only a .NET string can hold
    static string WellFormed(string body)
    {
        var builder = new StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            if (char.IsHighSurrogate(body[i]) && i + 1 < body.Length && char.IsLowSurrogate(body[i + 1]))
            {
                builder.Append(body[i]).Append(body[++i]);
            }
            else
            {
                builder.Append(char.IsSurrogate(body[i]) ? '\uFFFD' : body[i]);
            }
        }
        return builder.ToString();
    }

    static string Attribute(string value) => RubyEscape.HtmlEscape(value);

    // --- OWASP -----------------------------------------------------------------------------------

    // Every place a stored body can carry a payload: markup, attributes Action Text and Lexxy
    // read, attachment content, Trix attachment JSON, link targets and code blocks
    string OwaspBody(Random random)
    {
        var vector = Choose(random, OwaspVectors.Value);
        if (Chance(random, 0.4))
        {
            vector = Obfuscate(random, vector);
        }
        var escaped = Attribute(vector);
        var json = Attribute(JsonSerializer.Serialize(vector));
        return random.Next(16) switch
        {
            0 or 1 => vector,
            2 => $"<p>{vector}</p>",
            3 => $"<div>Hey {vector} there</div>",
            4 => $"<p><a href=\"{escaped}\">link</a></p>",
            5 => $"<p><img src=\"{escaped}\"></p>",
            6 => $"<action-text-attachment content-type=\"text/html\" content=\"{escaped}\"></action-text-attachment>",
            7 => $"<action-text-attachment content-type=\"application/vnd.actiontext.opengraph-embed\" url=\"{escaped}\" href=\"{escaped}\" filename=\"{escaped}\" caption=\"{escaped}\"></action-text-attachment>",
            8 => $"<action-text-attachment content-type=\"application/vnd.actiontext.opengraph-embed\" content=\"{escaped}\"></action-text-attachment>",
            9 => $"<action-text-attachment sgid=\"{Attribute(Choose(random, records.Sgids).Sgid)}\" content-type=\"application/vnd.campfire.mention\" content=\"{escaped}\"></action-text-attachment>",
            10 => $"<action-text-attachment content-type=\"image/png\" url=\"{escaped}\" caption=\"{escaped}\"></action-text-attachment>",
            11 => $"<figure data-trix-attachment=\"{Attribute($$"""{"contentType":"text/html","content":{{JsonSerializer.Serialize(vector)}}}""")}\"></figure>",
            12 => $"<div><figure data-trix-attachment=\"{Attribute($$"""{"contentType":"application/vnd.actiontext.opengraph-embed","href":{{JsonSerializer.Serialize(vector)}},"url":{{JsonSerializer.Serialize(vector)}}}""")}\" data-trix-attributes=\"{json}\"></figure></div>",
            13 => $"<pre data-language=\"{escaped}\">{vector}</pre>",
            14 => $"<p>{Mutate(random, vector)}</p>",
            _ => $"<p>{vector}</p><p>https://example.com/{Uri.EscapeDataString(vector)}</p>",
        };
    }

    static readonly string[] Spaces = ["\t", "\n", "\r", "\0", "\u000B", "\u000C", " ", "\u00A0", "\u2028", "\u180E", "\uFEFF", "/", "+"];

    // The evasions the cheat sheet describes, applied anywhere: case, whitespace and control
    // characters inside words, and character references with and without padding or semicolons
    static string Obfuscate(Random random, string vector)
    {
        var builder = new StringBuilder();
        foreach (var c in vector)
        {
            switch (random.Next(12))
            {
                case 0 when char.IsLetter(c):
                    builder.Append(char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c));
                    break;
                case 1 when char.IsAsciiLetter(c):
                    builder.Append($"&#{(int)c}{(Chance(random, 0.5) ? ";" : "")}");
                    break;
                case 2 when char.IsAsciiLetter(c):
                    builder.Append($"&#x{(int)c:X}{(Chance(random, 0.5) ? ";" : "")}");
                    break;
                case 3 when char.IsAsciiLetter(c):
                    builder.Append($"&#{new string('0', random.Next(1, 8))}{(int)c}");
                    break;
                case 4:
                    builder.Append(c).Append(Choose(random, Spaces));
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }
        return builder.ToString();
    }

    // --- Malformed nesting -----------------------------------------------------------------------

    static readonly string[] NestingTags =
    [
        "p", "div", "span", "a", "b", "i", "em", "strong", "s", "u", "mark", "code", "pre", "h1", "h2", "h6", "blockquote", "ul", "ol", "li",
        "dl", "dt", "dd", "table", "thead", "tbody", "tfoot", "tr", "td", "th", "caption", "colgroup", "col", "figure", "figcaption", "img",
        "br", "hr", "nobr", "font", "big", "small", "tt", "strike", "button", "form", "input", "select", "option", "optgroup", "textarea",
        "template", "svg", "math", "foreignObject", "desc", "title", "annotation-xml", "mi", "mo", "mtext", "script", "style", "noscript",
        "iframe", "noembed", "noframes", "xmp", "plaintext", "frameset", "frame", "head", "body", "html", "base", "meta", "link", "object",
        "embed", "param", "applet", "marquee", "ruby", "rt", "rp", "image", "isindex", "keygen", "menu", "menuitem", "details", "summary",
        "dialog", "address", "center", "listing", "search", "action-text-attachment", "lexxy-editor", "x-custom", "time", "sub", "sup",
    ];

    static readonly string[] NestingAttributes =
    [
        "", " class=\"x\"", " id=a", " href='https://example.com/'", " href=javascript:alert(1)", " encoding=\"text/html\"",
        " encoding=\"application/xhtml+xml\"", " xlink:href=\"javascript:alert(1)\"", " xmlns=\"http://www.w3.org/1999/xhtml\"",
        " type=hidden", " a=1 a=2", " /", " =x", " \"x\"=1", " <b>", " style=\"color:red\"", " onclick=x", " data-x", " sgid",
        " content-type=\"application/vnd.campfire.mention\"", " presentation=\"gallery\"", " href=\"https://x.com/dhh/status/1\"",
    ];

    static readonly string[] NestingSpecials =
    [
        "<!-- c -->", "<!-->", "<!--->", "<!-- -- -->", "<!--", "-->", "<![CDATA[x<y]]>", "<!x>", "<?php x ?>", "<!DOCTYPE html>", "</>",
        "</br>", "</p>", "</td>", "</table>", "</svg>", "</math>", "</template>", "</select>", "</a>", "</b>", "</html>", "</body>",
        "</ x>", "<a <b>", "<p", "<a href=\"", "&", "&amp", "&ampx;", "&#0;", "&#x110000;", "&#xD800;", "&#128512;", "&notit;",
        "&NotANamedRef;", "\0", "\r\n", "<", ">", "<b/>", "<br/>", "<p/>", "<div/>", "<svg/>", "<image src=x>",
    ];

    string NestingBody(Random random)
    {
        if (Chance(random, 0.08))
        {
            return NearLimits(random);
        }
        var builder = new StringBuilder();
        var open = new List<string>();
        var steps = random.Next(3, 60);
        for (var i = 0; i < steps; i++)
        {
            switch (random.Next(10))
            {
                case 0 or 1 or 2 or 3:
                    var tag = Choose(random, NestingTags);
                    builder.Append('<').Append(RandomCase(random, tag)).Append(Choose(random, NestingAttributes)).Append('>');
                    open.Add(tag);
                    break;
                case 4 or 5 when open.Count > 0:
                    // Close any open element, not necessarily the innermost: misnesting
                    var index = Chance(random, 0.5) ? open.Count - 1 : random.Next(open.Count);
                    builder.Append("</").Append(open[index]).Append('>');
                    open.RemoveAt(index);
                    break;
                case 6:
                    builder.Append("</").Append(Choose(random, NestingTags)).Append('>');
                    break;
                case 7:
                    builder.Append(Choose(random, NestingSpecials));
                    break;
                case 8:
                    builder.Append(SgidAttachment(random));
                    break;
                default:
                    builder.Append(Choose(random, TreeTexts));
                    break;
            }
        }
        return builder.ToString();
    }

    static string RandomCase(Random random, string tag) => Chance(random, 0.1) ? tag.ToUpperInvariant() : tag;

    // Around Gumbo's 400-element depth and 400-attribute limits, in shapes that check them at
    // different points of the parse
    string NearLimits(Random random)
    {
        var depth = random.Next(380, 420);
        var tag = Choose(random, (string[])["b", "span", "div", "i", "p", "table", "svg", "a", "template", "ul", "font", "td"]);
        var attributes = string.Join(' ', Enumerable.Range(1, random.Next(380, 420)).Select(i => $"a{i}=\"{i}\""));
        return random.Next(5) switch
        {
            0 => string.Concat(Enumerable.Repeat($"<{tag}>", depth)) + "deep",
            1 => string.Concat(Enumerable.Repeat($"<{tag}>", depth)) + string.Concat(Enumerable.Repeat($"</{tag}>", depth)),
            2 => $"<p {attributes}>many</p>",
            3 => string.Concat(Enumerable.Repeat($"<{tag}>", depth / 2)) + SgidAttachment(random) + string.Concat(Enumerable.Repeat("<i>", depth / 2)),
            _ => $"<p>{string.Concat(Enumerable.Range(1, depth).Select(i => $"<i title=\"{i}\">"))}</p>x",
        };
    }

    // --- Huge inputs -----------------------------------------------------------------------------

    string HugeBody(Random random)
    {
        // Log-uniform between 8 KB and 1 MB
        var size = (int)Math.Exp(random.NextDouble() * (Math.Log(1 << 20) - Math.Log(8 << 10)) + Math.Log(8 << 10));
        Func<string> piece = random.Next(8) switch
        {
            0 => () => Choose(random, TreeTexts),
            1 => () => $"<p>{Choose(random, TreeTexts)}</p>",
            2 => () => SgidAttachment(random),
            3 => () => "https://example.com/" + new string('a', random.Next(1, 200)) + " ",
            4 => () => "&amp;&lt;&#x1F600;&nbsp;",
            5 => () => $"<a href=\"https://example.com/{random.Next()}\">x</a> www.example.com/{random.Next()} me{random.Next()}@example.com ",
            6 => () => Mention(Choose(random, records.Users)),
            _ => () => Tree(random, 0),
        };
        var builder = new StringBuilder(size + 1024);
        if (Chance(random, 0.2))
        {
            // One huge attribute value or text node
            var filler = new string(Choose(random, (char[])['a', ' ', '&', '<', '\n', '\u00A0', '\u00E9']), size);
            return Choose(random, (string[])[$"<p title=\"{filler}\">x</p>", $"<a href=\"https://example.com/{filler}\">x</a>", filler, $"https://example.com/{filler}"]);
        }
        while (builder.Length < size)
        {
            builder.Append(piece());
        }
        return builder.ToString();
    }

    // --- Unicode ---------------------------------------------------------------------------------

    static readonly string[] Unicode =
    [
        "\0", "\u0001", "\u0008", "\u000B", "\u000C", "\r", "\r\n", "\u001F", "\u007F", "\u0080", "\u0085", "\u009F", "\u00A0", "\u00AD",
        "\u034F", "\u061C", "\u115F", "\u1680", "\u180E", "\u2000", "\u200A", "\u200B", "\u200C", "\u200D", "\u200E", "\u200F", "\u2028",
        "\u2029", "\u202A", "\u202E", "\u202F", "\u205F", "\u2060", "\u2066", "\u2069", "\u3000", "\uFEFF", "\uFFFD", "\uFFFE", "\uFFFF",
        "\uFDD0", "\uFDEF", "\uFF1C", "\uFF1E", "\uFF02", "\uFF07", "\uFF1A", "\uFF4A\uFF41\uFF56\uFF41", "\u017F", "\u0130", "\u0131",
        "\u212A", "\u212B", "\uFB00", "\u03C2", "\u1E9E", "e\u0301", "\u0301\u0301\u0301", "\u0E01\u0E49", "\uD83D\uDE00",
        "\uD83D\uDC69\u200D\uD83D\uDCBB", "\uD83D\uDC4D\uD83C\uDFFD", "\uD83C\uDDFA\uD83C\uDDF8", "\uD835\uDC00", "\uDBFF\uDFFF",
        "\uDB40\uDC01", "\uD800\uDC00", "\u05E9\u05DC\u05D5\u05DD", "\u0645\u0631\u062D\u0628\u0627", "\u0661\u0662\u0667",
        "\u4E2D\u6587", "\uD55C\uAD6D\uC5B4", "\u0928\u092E\u0938\u094D\u0924\u0947", "\u2603", "\u00E9", "\u00DF", "\u0000\u0000",
        "\u2024", "\u3002", "\uFF0E", "\uFF61", "\u2044", "\u2215", "\uFF0F", "\u02F8", "\u2039", "\u203A", "\u00AB", "\u00BB",
    ];

    static readonly string[] UnicodeHosts =
    [
        "https://\u4F8B\u3048.\u30C6\u30B9\u30C8/x", "https://xn--r8jz45g.xn--zckzah/x", "https://exa\u200Bmple.com/", "https://example\u3002com/",
        "https://\u0435xample.com/", "http://\uFF11\uFF12\uFF17.\uFF10.\uFF10.\uFF11/", "https://ex\u00ADample.com/", "https://x.com/\u00E9/status/1",
        "https://twitter\uFF0Ecom/dhh/status/1", "mailto:\u00E9@example.com", "https://example.com/\uD83D\uDE00?q=\u00E9#\u2028",
        "https://example.com/\u202Egnp.exe", "www.\u00E9xample.com", "me@\u00E9xample.com",
    ];

    string UnicodeBody(Random random)
    {
        string Noise() => string.Concat(Enumerable.Range(0, random.Next(1, 4)).Select(_ => Choose(random, Unicode)));
        var parts = Enumerable.Range(0, random.Next(1, 8)).Select(_ => random.Next(9) switch
        {
            0 => $"<p>{Noise()}{Choose(random, TreeTexts)}{Noise()}</p>",
            1 => $"{Choose(random, UnicodeHosts)}{Noise()} ",
            2 => $"<a href=\"{Attribute(Noise() + "javascript" + Noise() + ":alert(1)")}\">x</a>",
            3 => $"<a href=\"{Attribute(Choose(random, UnicodeHosts))}\">{Noise()}</a>",
            4 => $"<{Choose(random, (string[])["p", "b", "div", "span"])}{Noise()} title=\"{Attribute(Noise())}\">{Noise()}</p>",
            5 => $"&#x{char.ConvertToUtf32(Choose(random, (string[])["\uD83D\uDE00", "\u00A0", "\u202E", "\u0301", "\uFFFD"]), 0):X};{Noise()}",
            6 => SgidAttachment(random).Replace("sgid=\"", "sgid=\"" + Noise(), StringComparison.Ordinal),
            7 => Mutate(random, Choose(random, ReferenceBodies.Value)).Replace(" ", Noise(), StringComparison.Ordinal),
            _ => Noise(),
        });
        return string.Concat(parts);
    }

    // --- SGIDs -----------------------------------------------------------------------------------

    static readonly string[] ContentTypes =
    [
        "application/vnd.campfire.mention", "application/vnd.campfire.mention", "application/octet-stream", "", "text/html", "image/png",
        "video/mp4", "application/vnd.actiontext.opengraph-embed", "application/vnd.campfire.mention; charset=utf-8", "APPLICATION/VND.CAMPFIRE.MENTION",
    ];

    string SgidBody(Random random)
    {
        var attachments = Enumerable.Range(0, random.Next(1, 5)).Select(_ => SgidAttachment(random)).ToList();
        return random.Next(7) switch
        {
            0 => $"<p>Hey {string.Join(" and ", attachments)}!</p>",
            1 => $"<div>{string.Concat(attachments)}</div>",
            2 => $"<div class=\"attachment-gallery\">{string.Concat(attachments)}</div>",
            3 => $"<p><{Choose(random, (string[])["svg", "math", "table", "template", "select", "a href=\"/x\"", "pre"])}>{string.Concat(attachments)}</p>",
            4 => string.Concat(attachments.Select(a => $"<p>{a}</p>")) + Choose(random, TreeTexts),
            5 => TrixAttachment(random),
            _ => string.Concat(attachments),
        };
    }

    string SgidAttachment(Random random)
    {
        var sgid = SgidVariant(random);
        var contentType = Choose(random, ContentTypes);
        var attributes = new StringBuilder();
        attributes.Append(Chance(random, 0.9) ? $" sgid=\"{Attribute(sgid)}\"" : "");
        attributes.Append(contentType.Length > 0 ? $" content-type=\"{contentType}\"" : "");
        if (Chance(random, 0.5))
        {
            attributes.Append($" content=\"{Attribute(Chance(random, 0.7) ? Choose(random, records.Users).MentionContent : Choose(random, TreeTexts))}\"");
        }
        if (Chance(random, 0.1))
        {
            attributes.Append($" gid=\"{Attribute(Choose(random, records.Sgids).Sgid)}\"");
        }
        if (Chance(random, 0.1))
        {
            attributes.Append($" url=\"{Attribute(Choose(random, WebUrls.Value))}\" href=\"{Attribute(Choose(random, WebUrls.Value))}\" filename=\"{Attribute(Choose(random, TreeTexts))}\"");
        }
        if (Chance(random, 0.05))
        {
            attributes.Append(" presentation=\"gallery\" caption=\"Cap\"");
        }
        var inner = Chance(random, 0.1) ? Choose(random, TreeTexts) : "";
        return $"<action-text-attachment{attributes}>{inner}</action-text-attachment>";
    }

    string TrixAttachment(Random random)
    {
        var sgid = JsonSerializer.Serialize(SgidVariant(random));
        var contentType = JsonSerializer.Serialize(Choose(random, ContentTypes));
        var json = random.Next(4) switch
        {
            0 => $$"""{"sgid":{{sgid}},"contentType":{{contentType}}}""",
            1 => $$"""{"sgid":{{sgid}},"contentType":{{contentType}},"content":{{JsonSerializer.Serialize(Choose(random, records.Users).MentionContent)}}}""",
            2 => $$"""{"sgid":{{sgid}} /* comment */,"contentType":{{contentType}}}""",
            _ => $$"""{"contentType":"application/vnd.actiontext.opengraph-embed","href":{{JsonSerializer.Serialize(Choose(random, WebUrls.Value))}},"url":{{JsonSerializer.Serialize(Choose(random, WebUrls.Value))}},"filename":"T"}""",
        };
        var attributes = Chance(random, 0.3) ? $" data-trix-attributes=\"{Attribute("""{"caption":"c","presentation":"gallery"}""")}\"" : "";
        return $"<div>Hi <figure data-trix-attachment=\"{Attribute(json)}\"{attributes} data-trix-content-type={contentType}></figure></div>";
    }

    static readonly string[] CraftedGids =
    [
        "gid://campfire/User/1", "gid://campfire/User/2", "gid://campfire/User/5", "gid://campfire/User/6", "gid://campfire/User/999",
        "gid://campfire/User/0", "gid://campfire/User/-1", "gid://campfire/User/1?expires_in=1", "gid://campfire/User/1/2",
        "gid://campfire/User/01", "gid://campfire/User/1x", "gid://campfire/User/", "gid://campfire/Room/1", "gid://campfire/Message/1",
        "gid://campfire/Rooms::Open/1", "gid://campfire/Account/1", "gid://campfire/Session/1", "gid://campfire/Nope/1",
        "gid://campfire/Kernel/1", "gid://other/User/1", "gid://CAMPFIRE/User/1", "gid://campfire/user/1", "GID://campfire/User/1",
        "gid://campfire/User/1\n", "gid://campfire/User/\u0661", "gid:/campfire/User/1", "User/1", "", "gid://campfire/User/1%00",
    ];

    // Every SGID the oracle minted, as is or damaged, and payloads built from scratch that only
    // the invalid-signature fallback reads
    string SgidVariant(Random random)
    {
        var minted = Choose(random, records.Sgids).Sgid;
        switch (random.Next(14))
        {
            case 0 or 1 or 2:
                return minted;
            case 3:
                return Damage(random, minted, minted.IndexOf("--", StringComparison.Ordinal) + 2, minted.Length);
            case 4:
                return Damage(random, minted, 0, Math.Max(1, minted.IndexOf("--", StringComparison.Ordinal)));
            case 5:
                return minted[..random.Next(minted.Length + 1)];
            case 6:
                return Choose(random, (string[])["", "--", "----", "--abc", "abc--", "!!!--abc", "WzFd--abc", "e30=--x", "bnVsbA==--x", "IiI=--x"]);
            case 7:
                {
                    var other = Choose(random, records.Sgids).Sgid;
                    var cut = other.IndexOf("--", StringComparison.Ordinal);
                    var mine = minted.IndexOf("--", StringComparison.Ordinal);
                    return cut < 0 || mine < 0 ? minted + other : minted[..mine] + other[cut..];
                }
            case 8:
                var pad = Choose(random, (string[])[" ", "\n", "%20", "=", "==", "--", "\u00A0"]);
                return Chance(random, 0.5) ? pad + minted : minted + pad;
            case 9:
                return Chance(random, 0.5) ? minted.Replace('+', '-').Replace('/', '_') : minted.Replace('-', '+').Replace('_', '/');
            default:
                return Crafted(random);
        }
    }

    static string Damage(Random random, string value, int from, int to)
    {
        if (to <= from)
        {
            return value;
        }
        var chars = value.ToCharArray();
        var at = random.Next(from, to);
        chars[at] = Choose(random, (char[])['A', 'a', '0', '-', '_', '+', '/', '=', 'Z']);
        return new string(chars);
    }

    static string Crafted(Random random)
    {
        var gid = JsonSerializer.Serialize(Choose(random, CraftedGids));
        var json = random.Next(12) switch
        {
            0 or 1 => $$$"""{"_rails":{"data":{{{gid}}},"pur":"attachable"}}""",
            2 => $$$"""{"_rails":{"data":{{{gid}}},"pur":"transfer","exp":"2020-01-01T00:00:00.000Z"}}""",
            3 => $$$"""{"_rails":{"message":"{{{Convert.ToBase64String(MarshalString(random, Choose(random, CraftedGids)))}}}","exp":null,"pur":"attachable"}}""",
            4 => $$$"""{"_rails":{"data":{{{Choose(random, (string[])["1", "null", "true", "[]", "{}", "[\"gid://campfire/User/1\"]", "{\"a\":1}", "1.5"])}}}}}""",
            5 => $$$"""{"_rails":{{{Choose(random, (string[])["null", "1", "\"x\"", "[]", "{}"])}}}}""",
            6 => Choose(random, (string[])["null", "1", "[]", "[1]", "\"x\"", "{}", "{\"_rails\":null}", "{\"a\":1}", "true"]),
            7 => $$$"""/* c */{"_rails":{"data":{{{gid}}}}}""",
            8 => $$$"""{"_rails":{"data":{{{gid}}},"data":"gid://campfire/User/2"}}""",
            9 => $$$"""{"_rails":{"message":"{{{Convert.ToBase64String(Encoding.UTF8.GetBytes("junk" + Choose(random, CraftedGids) + "junk"))}}}"}}""",
            10 => "\uFEFF" + $$$"""{"_rails":{"data":{{{gid}}}}}""",
            _ => """{"_rails":{"data":"\u0067id://campfire/User/1"}}""",
        };
        var bytes = Encoding.UTF8.GetBytes(json);
        if (Chance(random, 0.1))
        {
            bytes = [.. bytes, 0xFF, 0xFE];
        }
        var encoded = random.Next(4) switch
        {
            0 => Convert.ToBase64String(bytes).TrimEnd('='),
            1 => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_'),
            _ => Convert.ToBase64String(bytes),
        };
        return $"{encoded}--{Choose(random, (string[])["invalid", "", "0000", "a--b"])}";
    }

    // Marshal.dump of a short string, as Rails 7 SGIDs carry their GID
    static byte[] MarshalString(Random random, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        byte[] prefix = [0x04, 0x08, 0x49, 0x22, (byte)(bytes.Length + 5)];
        byte[] suffix = [0x06, 0x3A, 0x06, 0x45, 0x54];
        return Chance(random, 0.2) ? [.. prefix, .. bytes] : [.. prefix, .. bytes, .. suffix];
    }

    string Mention(OracleUser user) =>
        $"<action-text-attachment sgid=\"{Attribute(records.Sgids.First(s => s.Label == $"{user.Key} attachable").Sgid)}\" content-type=\"application/vnd.campfire.mention\" content=\"{Attribute(user.MentionContent)}\"></action-text-attachment>";

    // --- Mutation (generate.rb's Mutate) ---------------------------------------------------------

    static readonly string[] MutationTokens =
    [
        "<", ">", "\"", "'", "&", "&#", "&#x", ";", "</", "/>", "<b>", "</b>", "<p>", "</p>", "<div>", "<svg>", "<math>", "<table>", "<td>",
        "<select>", "<a href=", "<!--", "-->", "<![CDATA[", "\n", "\r", "\t", "\u00A0", "\u0000", "javascript:", "http://", "https://x.com/",
        "www.", "@", "=", " ", "(", ")", "]", "--", "=\"", "action-text-attachment", "sgid=",
        "content-type=\"application/vnd.actiontext.opengraph-embed\"", "\u00E9", "\uD83D\uDE00", "\u202E", "%", "#", "?",
        "<action-text-attachment>", "</action-text-attachment>", "<figure>", "data-trix-attachment=", "content=\"", "{", "}", "\\",
    ];

    static string Mutate(Random random, string body)
    {
        var builder = new StringBuilder(body);
        var edits = random.Next(1, 7);
        for (var i = 0; i < edits; i++)
        {
            var position = builder.Length == 0 ? 0 : random.Next(builder.Length + 1);
            switch (random.Next(4))
            {
                case 0 or 1:
                    builder.Insert(position, Choose(random, MutationTokens));
                    break;
                case 2:
                    builder.Remove(position, Math.Min(random.Next(1, 13), builder.Length - position));
                    break;
                default:
                    var length = Math.Min(random.Next(1, 41), builder.Length - position);
                    builder.Insert(position, builder.ToString(position, length));
                    break;
            }
        }
        return builder.ToString();
    }

    // --- Random trees (generate.rb's Fuzz) -------------------------------------------------------

    static readonly string[] TreeTags =
    [
        "p", "div", "span", "a", "b", "strong", "i", "em", "s", "u", "mark", "code", "pre", "h1", "h2", "blockquote", "ul", "ol", "li",
        "table", "tr", "td", "th", "tbody", "figure", "figcaption", "img", "br", "hr", "script", "style", "iframe", "svg", "math", "textarea",
        "select", "option", "template", "noscript", "object", "form", "input", "button", "video", "source", "action-text-attachment", "font",
        "center", "marquee", "details", "summary", "sub", "sup", "del", "ins", "small", "abbr", "acronym", "address", "big", "cite", "dd",
        "dfn", "dl", "dt", "h3", "h4", "h5", "h6", "kbd", "samp", "time", "tt", "var", "thead", "tfoot", "audio", "picture", "area", "map",
    ];

    static readonly string[] TreeAttributes =
    [
        "href=\"https://example.com/a?b=1&amp;c=2\"", "href=\"javascript:alert(1)\"", "href=\" jav&#x09;ascript:x\"", "href=\"/rooms/1\"",
        "href=\"data:text/html,x\"", "href=\"mailto:a@b.co\"", "src=\"https://example.com/i.png\"", "src=\"x\" onerror=\"alert(1)\"",
        "onclick=\"x()\"", "style=\"color: red; background: url(javascript:x)\"", "class=\"og-embed mention\"", "data-controller=\"x\"",
        "data-language=\"ruby\"", "title=\"t > http://example.com/in-title\"", "title='q\"uote'", "lang=\"en\"", "id=\"i\"",
        "content-type=\"application/vnd.actiontext.opengraph-embed\"", "content-type=\"application/vnd.campfire.mention\"",
        "content-type=\"text/html\" content=\"&lt;b&gt;inner&lt;/b&gt;\"", "content-type=\"image/png\" url=\"https://example.com/p.png\"",
        "filename=\"Title\" href=\"https://example.org/\"", "caption=\"Cap\"", "presentation=\"gallery\"", "width=\"10\" height=\"20\"",
        "value=\"3\"", "datetime=\"2024-01-01\"", "cite=\"javascript:x\"", "srcset=\"javascript:x 1x\"", "formaction=\"javascript:x\"",
        "style=\"color: var(--x)\"", "style=\"white-space: pre\"", "style=\"background-color: rgb(1,2,3)\"", "xml:lang=\"en\"",
        "abbr=\"a\"", "alt=\"a\"", "name=\"n\"", "target=\"_top\"", "rel=\"opener\"", "data-trix-attachment=\"{}\"",
    ];

    static readonly string[] TreeTexts =
    [
        "hello", "https://example.com/x.", "www.example.com", "(https://example.com/(a))", "me@example.com", "&amp; &lt; &gt; &quot; &nbsp;",
        "\uD83D\uDE00 \uD83D\uDC4D\uD83C\uDFFD", "\u05E9\u05DC\u05D5\u05DD", "\n", "  ", "a\u00A0b", "https://x.com/dhh/status/1?s=2", "&#106;avascript:x", "<!-- c -->", "]]>", "&", "<",
        "http://example.com/&gt;", "x@y", "https://twitter.com/dhh/status/2", "http://localhost:3000/rooms/1", "ftp://example.com/f",
        "https://example.com/a_(b)_c?d=e#f", "https://example.com/\"onmouseover=\"x", "mailto:me@example.com", "a.b@c.d.e",
        "https://\u4F8B\u3048.\u30C6\u30B9\u30C8/", "/play trombone", "@David", "https://once.campfire.test/rooms/1", "1 < 2 && 3 > 2",
    ];

    string Tree(Random random, int depth)
    {
        var builder = new StringBuilder();
        var count = random.Next(1, 5);
        for (var i = 0; i < count; i++)
        {
            if (depth > 3 || Chance(random, 0.35))
            {
                builder.Append(Chance(random, 0.08) ? Mention(Choose(random, records.Users)) : Choose(random, TreeTexts));
                continue;
            }
            var tag = Choose(random, TreeTags);
            var attributes = string.Join(' ', Enumerable.Range(0, random.Next(0, 3)).Select(_ => Choose(random, TreeAttributes)));
            builder.Append('<').Append(tag).Append(attributes.Length > 0 ? " " + attributes : "").Append('>');
            builder.Append(Tree(random, depth + 1));
            if (Chance(random, 0.9))
            {
                builder.Append("</").Append(tag).Append('>');
            }
        }
        return builder.ToString();
    }
}
