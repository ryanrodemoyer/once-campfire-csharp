using System.Collections.Frozen;

namespace Campfire.RichText.Html.Gumbo;

/// <summary>
/// Gumbo's <c>TagSet</c>: tag names, each in one or more namespaces (gumbo-parser/src/parser.c).
/// Gumbo matches tags by its <c>GumboTag</c> enum, which is the lowercase name for every tag here.
/// </summary>
sealed class TagSet
{
    readonly FrozenDictionary<string, int> namespaces;

    TagSet(Dictionary<string, int> namespaces) => this.namespaces = namespaces.ToFrozenDictionary();

    public static TagSet Html(params string[] tags) => new(tags.ToDictionary(t => t, _ => Bit(HtmlNamespace.Html)));

    /// <summary>The set plus tags in another namespace (Gumbo's TAG_SVG and TAG_MATHML).</summary>
    public TagSet With(HtmlNamespace ns, params string[] tags)
    {
        var copy = new Dictionary<string, int>(namespaces);
        foreach (var tag in tags)
        {
            copy[tag] = copy.GetValueOrDefault(tag) | Bit(ns);
        }
        return new TagSet(copy);
    }

    public bool Contains(HtmlNamespace ns, string tag) => namespaces.TryGetValue(tag, out var bits) && (bits & Bit(ns)) != 0;

    public bool Contains(HtmlElement element) => Contains(element.Namespace, element.Tag);

    /// <summary>Gumbo's tag_in for a token: the tag is in the set, in any namespace.</summary>
    public bool ContainsTag(string tag) => namespaces.ContainsKey(tag);

    static int Bit(HtmlNamespace ns) => 1 << (int)ns;
}

static class Tags
{
    /// <summary>The names Gumbo has a <c>GumboTag</c> for (gumbo-parser/src/tag_lookup.gperf).</summary>
    public static readonly FrozenSet<string> Known = FrozenSet.ToFrozenSet(
    [
        "html", "head", "title", "base", "link", "meta", "style", "script", "noscript", "template", "body",
        "article", "section", "nav", "aside", "h1", "h2", "h3", "h4", "h5", "h6", "hgroup", "header", "footer",
        "address", "p", "hr", "pre", "blockquote", "ol", "ul", "li", "dl", "dt", "dd", "figure", "figcaption",
        "main", "div", "a", "em", "strong", "small", "s", "cite", "q", "dfn", "abbr", "data", "time", "code",
        "var", "samp", "kbd", "sub", "sup", "i", "b", "u", "mark", "ruby", "rt", "rp", "bdi", "bdo", "span",
        "br", "wbr", "ins", "del", "image", "img", "iframe", "embed", "object", "param", "video", "audio",
        "source", "track", "canvas", "map", "area", "math", "mi", "mo", "mn", "ms", "mtext", "mglyph",
        "malignmark", "annotation-xml", "svg", "foreignobject", "desc", "table", "caption", "colgroup", "col",
        "tbody", "thead", "tfoot", "tr", "td", "th", "form", "fieldset", "legend", "label", "input", "button",
        "select", "datalist", "optgroup", "option", "textarea", "keygen", "output", "progress", "meter",
        "details", "summary", "menu", "menuitem", "applet", "acronym", "bgsound", "dir", "frame", "frameset",
        "noframes", "listing", "xmp", "nextid", "noembed", "plaintext", "rb", "strike", "basefont", "big",
        "blink", "center", "font", "marquee", "multicol", "nobr", "spacer", "tt", "rtc", "dialog", "search",
    ]);

    // gumbo-parser/src/svg_tags.gperf. Gumbo looks names up ignoring ASCII case; they reach here lowercased.
    public static readonly FrozenDictionary<string, string> SvgTagReplacements = new Dictionary<string, string>
    {
        ["altglyph"] = "altGlyph",
        ["altglyphdef"] = "altGlyphDef",
        ["altglyphitem"] = "altGlyphItem",
        ["animatecolor"] = "animateColor",
        ["animatemotion"] = "animateMotion",
        ["animatetransform"] = "animateTransform",
        ["clippath"] = "clipPath",
        ["feblend"] = "feBlend",
        ["fecolormatrix"] = "feColorMatrix",
        ["fecomponenttransfer"] = "feComponentTransfer",
        ["fecomposite"] = "feComposite",
        ["feconvolvematrix"] = "feConvolveMatrix",
        ["fediffuselighting"] = "feDiffuseLighting",
        ["fedisplacementmap"] = "feDisplacementMap",
        ["fedistantlight"] = "feDistantLight",
        ["feflood"] = "feFlood",
        ["fefunca"] = "feFuncA",
        ["fefuncb"] = "feFuncB",
        ["fefuncg"] = "feFuncG",
        ["fefuncr"] = "feFuncR",
        ["fegaussianblur"] = "feGaussianBlur",
        ["feimage"] = "feImage",
        ["femerge"] = "feMerge",
        ["femergenode"] = "feMergeNode",
        ["femorphology"] = "feMorphology",
        ["feoffset"] = "feOffset",
        ["fepointlight"] = "fePointLight",
        ["fespecularlighting"] = "feSpecularLighting",
        ["fespotlight"] = "feSpotLight",
        ["fetile"] = "feTile",
        ["feturbulence"] = "feTurbulence",
        ["foreignobject"] = "foreignObject",
        ["glyphref"] = "glyphRef",
        ["lineargradient"] = "linearGradient",
        ["radialgradient"] = "radialGradient",
        ["textpath"] = "textPath",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    // gumbo-parser/src/svg_attrs.gperf, likewise.
    public static readonly FrozenDictionary<string, string> SvgAttributeReplacements = new Dictionary<string, string>
    {
        ["attributename"] = "attributeName",
        ["attributetype"] = "attributeType",
        ["basefrequency"] = "baseFrequency",
        ["baseprofile"] = "baseProfile",
        ["calcmode"] = "calcMode",
        ["clippathunits"] = "clipPathUnits",
        ["diffuseconstant"] = "diffuseConstant",
        ["edgemode"] = "edgeMode",
        ["filterunits"] = "filterUnits",
        ["glyphref"] = "glyphRef",
        ["gradienttransform"] = "gradientTransform",
        ["gradientunits"] = "gradientUnits",
        ["kernelmatrix"] = "kernelMatrix",
        ["kernelunitlength"] = "kernelUnitLength",
        ["keypoints"] = "keyPoints",
        ["keysplines"] = "keySplines",
        ["keytimes"] = "keyTimes",
        ["lengthadjust"] = "lengthAdjust",
        ["limitingconeangle"] = "limitingConeAngle",
        ["markerheight"] = "markerHeight",
        ["markerunits"] = "markerUnits",
        ["markerwidth"] = "markerWidth",
        ["maskcontentunits"] = "maskContentUnits",
        ["maskunits"] = "maskUnits",
        ["numoctaves"] = "numOctaves",
        ["pathlength"] = "pathLength",
        ["patterncontentunits"] = "patternContentUnits",
        ["patterntransform"] = "patternTransform",
        ["patternunits"] = "patternUnits",
        ["pointsatx"] = "pointsAtX",
        ["pointsaty"] = "pointsAtY",
        ["pointsatz"] = "pointsAtZ",
        ["preservealpha"] = "preserveAlpha",
        ["preserveaspectratio"] = "preserveAspectRatio",
        ["primitiveunits"] = "primitiveUnits",
        ["refx"] = "refX",
        ["refy"] = "refY",
        ["repeatcount"] = "repeatCount",
        ["repeatdur"] = "repeatDur",
        ["requiredextensions"] = "requiredExtensions",
        ["requiredfeatures"] = "requiredFeatures",
        ["specularconstant"] = "specularConstant",
        ["specularexponent"] = "specularExponent",
        ["spreadmethod"] = "spreadMethod",
        ["startoffset"] = "startOffset",
        ["stddeviation"] = "stdDeviation",
        ["stitchtiles"] = "stitchTiles",
        ["surfacescale"] = "surfaceScale",
        ["systemlanguage"] = "systemLanguage",
        ["tablevalues"] = "tableValues",
        ["targetx"] = "targetX",
        ["targety"] = "targetY",
        ["textlength"] = "textLength",
        ["viewbox"] = "viewBox",
        ["viewtarget"] = "viewTarget",
        ["xchannelselector"] = "xChannelSelector",
        ["ychannelselector"] = "yChannelSelector",
        ["zoomandpan"] = "zoomAndPan",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    // gumbo-parser/src/foreign_attrs.gperf: name -> (prefix, local name). Case-sensitive.
    public static readonly FrozenDictionary<string, (string Prefix, string LocalName)> ForeignAttributes =
        new Dictionary<string, (string, string)>
        {
            ["xlink:actuate"] = ("xlink", "actuate"),
            ["xlink:arcrole"] = ("xlink", "arcrole"),
            ["xlink:href"] = ("xlink", "href"),
            ["xlink:role"] = ("xlink", "role"),
            ["xlink:show"] = ("xlink", "show"),
            ["xlink:title"] = ("xlink", "title"),
            ["xlink:type"] = ("xlink", "type"),
            ["xml:lang"] = ("xml", "lang"),
            ["xml:space"] = ("xml", "space"),
            ["xmlns"] = ("xmlns", "xmlns"),
            ["xmlns:xlink"] = ("xmlns", "xlink"),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>gumbo_ascii_strcasecmp: equal ignoring the case of ASCII letters only.</summary>
    public static bool AsciiEqualsIgnoreCase(string a, string b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }
        for (var i = 0; i < a.Length; i++)
        {
            if (AsciiToLower(a[i]) != AsciiToLower(b[i]))
            {
                return false;
            }
        }
        return true;
    }

    public static string AsciiToLower(string value)
    {
        foreach (var c in value)
        {
            if (c is >= 'A' and <= 'Z')
            {
                return string.Create(value.Length, value, static (span, source) =>
                {
                    for (var i = 0; i < source.Length; i++)
                    {
                        span[i] = AsciiToLower(source[i]);
                    }
                });
            }
        }
        return value;
    }

    static char AsciiToLower(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 0x20) : c;
}
