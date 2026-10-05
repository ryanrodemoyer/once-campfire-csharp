using System.Text;

namespace Campfire.RichText.Html.Gumbo;

enum InsertionMode
{
    Initial,
    BeforeHtml,
    BeforeHead,
    InHead,
    InHeadNoscript,
    AfterHead,
    InBody,
    Text,
    InTable,
    InTableText,
    InCaption,
    InColumnGroup,
    InTableBody,
    InRow,
    InCell,
    InSelect,
    InSelectInTable,
    InTemplate,
    AfterBody,
    InFrameset,
    AfterFrameset,
    AfterAfterBody,
    AfterAfterFrameset,
}

/// <summary>
/// A port of Gumbo's tree construction (gumbo-parser/src/parser.c, nokogiri 1.19.4), building
/// straight into <see cref="HtmlNode"/>s. Gumbo predates parts of today's WHATWG algorithm (its
/// select modes, text buffering until the next insertion), so it is followed rather than the spec.
/// Parse errors and source positions are left out.
/// </summary>
sealed class TreeBuilder
{
    // The "scope marker" in the list of active formatting elements
    static readonly HtmlElement Marker = new("#marker");

    // Gumbo's stand-in when the fragment's context has a form ancestor
    static readonly HtmlElement FormAncestor = new("form");

    readonly Tokenizer tokenizer;
    readonly int maxTreeDepth;
    readonly HtmlDocument document = new();
    readonly HtmlElement? fragmentContext;

    HtmlElement? root;
    InsertionMode insertionMode = InsertionMode.Initial;
    InsertionMode originalInsertionMode = InsertionMode.Initial;
    readonly List<HtmlElement> openElements = [];
    readonly List<HtmlElement> activeFormattingElements = [];
    readonly List<InsertionMode> templateInsertionModes = [];
    HtmlElement? headElement;
    HtmlElement? formElement;
    bool reprocessCurrentToken;
    bool framesetOk = true;
    bool ignoreNextLinefeed;
    bool fosterParentInsertions;

    // TextNodeBufferState: characters wait here until the next node is inserted
    readonly StringBuilder textBuffer = new();
    TextKind textKind = TextKind.Whitespace;

    HtmlDoctype? doctype;
    bool treeTooDeep;

    enum TextKind
    {
        Whitespace,
        Text,
        CData,
    }

    TreeBuilder(Tokenizer tokenizer, int maxTreeDepth, HtmlElement? fragmentContext)
    {
        this.tokenizer = tokenizer;
        this.maxTreeDepth = maxTreeDepth;
        this.fragmentContext = fragmentContext;
    }

    public sealed record Result(HtmlDocument Document, HtmlElement? Root, HtmlDoctype? Doctype, string? Error);

    public static Result Parse(string html, HtmlParseOptions options, FragmentContext? context)
    {
        var tokenizer = new Tokenizer(html, options.MaxAttributes);
        HtmlElement? contextElement = null;
        if (context is not null)
        {
            List<HtmlAttr> attributes = context.Encoding is null ? [] : [new HtmlAttr("encoding", context.Encoding)];
            contextElement = new HtmlElement(context.Name, Tags.AsciiToLower(context.Name), context.Namespace, attributes);
        }

        // nokogiri's gumbo.c adds one to the depth limit for a fragment's html element
        var maxDepth = options.MaxTreeDepth < 0 ? int.MaxValue : options.MaxTreeDepth + (context is null ? 0 : 1);
        var builder = new TreeBuilder(tokenizer, maxDepth, contextElement);
        if (context is not null)
        {
            builder.InitFragment(context);
        }
        builder.Run();

        var error = builder.treeTooDeep ? HtmlParseException.TreeTooDeep
            : tokenizer.TooManyAttributes ? HtmlParseException.TooManyAttributes
            : null;
        return new Result(builder.document, builder.root, builder.doctype, error);
    }

    // fragment_parser_init
    void InitFragment(FragmentContext context)
    {
        var ctx = fragmentContext!;
        document.QuirksMode = context.QuirksMode;
        if (ctx.Namespace == HtmlNamespace.Html)
        {
            switch (ctx.Tag)
            {
                case "title" or "textarea":
                    tokenizer.SetState(TokenizerState.RcData);
                    break;
                case "style" or "xmp" or "iframe" or "noembed" or "noframes":
                    tokenizer.SetState(TokenizerState.RawText);
                    break;
                case "script":
                    tokenizer.SetState(TokenizerState.ScriptData);
                    break;
                case "plaintext":
                    tokenizer.SetState(TokenizerState.PlainText);
                    break;
            }
        }

        root = InsertElementOfTag("html");
        if (ctx.Tag == "template")
        {
            templateInsertionModes.Add(InsertionMode.InTemplate);
        }
        ResetInsertionModeAppropriately();
        if (context.HasFormAncestor || (ctx.Tag == "form" && ctx.Namespace == HtmlNamespace.Html))
        {
            formElement = FormAncestor;
        }
    }

    readonly Token token = new();

    // gumbo_parse_with_options' main loop
    void Run()
    {
        do
        {
            if (reprocessCurrentToken)
            {
                reprocessCurrentToken = false;
            }
            else
            {
                var adjusted = AdjustedCurrentNode;
                tokenizer.SetAdjustedCurrentNodeForeign(adjusted is not null && adjusted.Namespace != HtmlNamespace.Html);
                // Past the depth limit, Gumbo carries on as if the input had ended
                if (openElements.Count > maxTreeDepth)
                {
                    treeTooDeep = true;
                    token.Type = TokenType.Eof;
                }
                else
                {
                    tokenizer.Lex(token);
                }
            }
            HandleToken();
        }
        while (token.Type != TokenType.Eof || reprocessCurrentToken);

        FinishParsing();
    }

    void FinishParsing()
    {
        FlushTextBuffer();
        while (PopCurrentNode() is not null)
        {
        }
    }

    // --- Stack and tree helpers ------------------------------------------------------------------

    HtmlElement? CurrentNode => openElements.Count > 0 ? openElements[^1] : null;

    HtmlElement? AdjustedCurrentNode => openElements.Count == 1 && fragmentContext is not null ? fragmentContext : CurrentNode;

    bool IsFragmentParser => fragmentContext is not null;

    static bool IsHtml(HtmlElement node, string tag) => node.Namespace == HtmlNamespace.Html && node.Tag == tag;

    static bool TagIs(Token token, bool startTag, string tag) =>
        token.Type == (startTag ? TokenType.StartTag : TokenType.EndTag) && token.TagName == tag;

    static bool TagIn(Token token, bool startTag, TagSet tags) =>
        token.Type == (startTag ? TokenType.StartTag : TokenType.EndTag) && tags.ContainsTag(token.TagName);

    const bool start = true;
    const bool end = false;

    // gumbo_get_attribute: by name, ignoring ASCII case and namespace
    static HtmlAttr? GetAttribute(List<HtmlAttr> attributes, string name)
    {
        foreach (var attribute in attributes)
        {
            if (Tags.AsciiEqualsIgnoreCase(attribute.Name, name))
            {
                return attribute;
            }
        }
        return null;
    }

    static bool AttributeMatches(List<HtmlAttr> attributes, string name, string value) =>
        GetAttribute(attributes, name) is { } attribute && Tags.AsciiEqualsIgnoreCase(value, attribute.Value);

    static bool AllAttributesMatch(List<HtmlAttr> first, List<HtmlAttr> second)
    {
        var unmatched = second.Count;
        foreach (var attribute in first)
        {
            if (GetAttribute(second, attribute.Name) is { } other && other.Value == attribute.Value)
            {
                unmatched--;
            }
            else
            {
                return false;
            }
        }
        return unmatched == 0;
    }

    void ResetInsertionModeAppropriately()
    {
        for (var i = openElements.Count - 1; i >= 0; i--)
        {
            var mode = AppropriateInsertionMode(i);
            if (mode != InsertionMode.Initial)
            {
                insertionMode = mode;
                return;
            }
        }
    }

    // get_appropriate_insertion_mode: Initial means keep looking
    InsertionMode AppropriateInsertionMode(int index)
    {
        var node = openElements[index];
        var isLast = index == 0;
        if (isLast && IsFragmentParser)
        {
            node = fragmentContext!;
        }
        if (node.Namespace != HtmlNamespace.Html)
        {
            return isLast ? InsertionMode.InBody : InsertionMode.Initial;
        }

        switch (node.Tag)
        {
            case "select":
                if (isLast)
                {
                    return InsertionMode.InSelect;
                }
                for (var i = index; i > 0; i--)
                {
                    var ancestor = openElements[i];
                    if (IsHtml(ancestor, "template"))
                    {
                        return InsertionMode.InSelect;
                    }
                    if (IsHtml(ancestor, "table"))
                    {
                        return InsertionMode.InSelectInTable;
                    }
                }
                return InsertionMode.InSelect;
            case "td" or "th":
                if (!isLast)
                {
                    return InsertionMode.InCell;
                }
                break;
            case "tr":
                return InsertionMode.InRow;
            case "tbody" or "thead" or "tfoot":
                return InsertionMode.InTableBody;
            case "caption":
                return InsertionMode.InCaption;
            case "colgroup":
                return InsertionMode.InColumnGroup;
            case "table":
                return InsertionMode.InTable;
            case "template":
                return CurrentTemplateInsertionMode;
            case "head":
                if (!isLast)
                {
                    return InsertionMode.InHead;
                }
                break;
            case "body":
                return InsertionMode.InBody;
            case "frameset":
                return InsertionMode.InFrameset;
            case "html":
                return headElement is not null ? InsertionMode.AfterHead : InsertionMode.BeforeHead;
        }
        return isLast ? InsertionMode.InBody : InsertionMode.Initial;
    }

    InsertionMode CurrentTemplateInsertionMode =>
        templateInsertionModes.Count == 0 ? InsertionMode.Initial : templateInsertionModes[^1];

    static readonly TagSet MathMlIntegrationPointTags = TagSet.Html().With(HtmlNamespace.MathMl, "mi", "mo", "mn", "ms", "mtext");
    static readonly TagSet HtmlIntegrationPointSvgTags = TagSet.Html().With(HtmlNamespace.Svg, "foreignobject", "desc", "title");

    static bool IsMathMlIntegrationPoint(HtmlElement node) => MathMlIntegrationPointTags.Contains(node);

    static bool IsHtmlIntegrationPoint(HtmlElement node)
    {
        if (HtmlIntegrationPointSvgTags.Contains(node))
        {
            return true;
        }
        return node.Namespace == HtmlNamespace.MathMl && node.Tag == "annotation-xml"
            && (AttributeMatches(node.AttributeList, "encoding", "text/html")
                || AttributeMatches(node.AttributeList, "encoding", "application/xhtml+xml"));
    }

    readonly record struct InsertionLocation(HtmlParentNode Target, int Index);

    static readonly TagSet FosterParentTargets = TagSet.Html("table", "tbody", "tfoot", "thead", "tr");

    InsertionLocation AppropriateInsertionLocation(HtmlParentNode? overrideTarget)
    {
        var target = overrideTarget ?? (root is not null ? CurrentNode! : document);
        if (!fosterParentInsertions || target is not HtmlElement element || !FosterParentTargets.Contains(element))
        {
            return new InsertionLocation(target, -1);
        }

        // Foster parenting
        var lastTemplate = -1;
        var lastTable = -1;
        for (var i = 0; i < openElements.Count; i++)
        {
            if (IsHtml(openElements[i], "template"))
            {
                lastTemplate = i;
            }
            if (IsHtml(openElements[i], "table"))
            {
                lastTable = i;
            }
        }
        if (lastTemplate != -1 && (lastTable == -1 || lastTemplate > lastTable))
        {
            return new InsertionLocation(openElements[lastTemplate], -1);
        }
        if (lastTable == -1)
        {
            return new InsertionLocation(openElements[0], -1);
        }
        var table = openElements[lastTable];
        if (table.Parent is { } parent)
        {
            return new InsertionLocation(parent, parent.IndexOf(table));
        }
        return new InsertionLocation(openElements[lastTable - 1], -1);
    }

    static void InsertNode(HtmlNode node, InsertionLocation location)
    {
        if (location.Index != -1)
        {
            location.Target.InsertAt(location.Index, node);
        }
        else
        {
            location.Target.AppendChild(node);
        }
    }

    void FlushTextBuffer()
    {
        if (textBuffer.Length == 0)
        {
            return;
        }
        var text = textBuffer.ToString();
        HtmlNode node = textKind == TextKind.CData ? new HtmlCData(text) : new HtmlText(text);
        var location = AppropriateInsertionLocation(null);
        // A document can't have text children, so the text is dropped
        if (location.Target is not HtmlDocument)
        {
            InsertNode(node, location);
        }
        textBuffer.Clear();
        textKind = TextKind.Whitespace;
    }

    HtmlElement? PopCurrentNode()
    {
        FlushTextBuffer();
        if (openElements.Count == 0)
        {
            return null;
        }
        var node = openElements[^1];
        openElements.RemoveAt(openElements.Count - 1);
        return node;
    }

    void AppendComment(HtmlParentNode parent, Token token)
    {
        FlushTextBuffer();
        parent.AppendChild(new HtmlComment(token.Text));
    }

    static readonly TagSet TableRowContext = TagSet.Html("html", "tr", "template");
    static readonly TagSet TableContext = TagSet.Html("html", "table", "template");
    static readonly TagSet TableBodyContext = TagSet.Html("html", "tbody", "tfoot", "thead", "template");

    void ClearStackBackTo(TagSet context)
    {
        while (!context.Contains(CurrentNode!))
        {
            PopCurrentNode();
        }
    }

    static HtmlElement CreateElementFromToken(Token token, HtmlNamespace ns)
    {
        var element = new HtmlElement(token.ElementName ?? token.TagName, token.TagName, ns, token.Attributes);
        token.Attributes = [];
        return element;
    }

    void InsertElement(HtmlElement node, bool isReconstructingFormattingElements)
    {
        if (!isReconstructingFormattingElements)
        {
            FlushTextBuffer();
        }
        InsertNode(node, AppropriateInsertionLocation(null));
        openElements.Add(node);
    }

    HtmlElement InsertElementFromToken(Token token)
    {
        var element = CreateElementFromToken(token, HtmlNamespace.Html);
        InsertElement(element, false);
        return element;
    }

    HtmlElement InsertElementOfTag(string tag)
    {
        var element = new HtmlElement(tag, tag, HtmlNamespace.Html, []);
        InsertElement(element, false);
        return element;
    }

    HtmlElement InsertForeignElement(Token token, HtmlNamespace ns)
    {
        var element = CreateElementFromToken(token, ns);
        InsertElement(element, false);
        return element;
    }

    void InsertTextToken(Token token)
    {
        if (token.Character >= 0x10000)
        {
            textBuffer.Append(char.ConvertFromUtf32(token.Character));
        }
        else
        {
            textBuffer.Append((char)token.Character);
        }
        if (token.Type == TokenType.Character)
        {
            textKind = TextKind.Text;
        }
        else if (token.Type == TokenType.CData)
        {
            textKind = TextKind.CData;
        }
    }

    // The generic raw text and RCDATA element parsing algorithms
    void RunGenericParsingAlgorithm(Token token, TokenizerState lexerState)
    {
        InsertElementFromToken(token);
        tokenizer.SetState(lexerState);
        originalInsertionMode = insertionMode;
        insertionMode = InsertionMode.Text;
    }

    bool FindLastAnchorIndex(out int anchorIndex)
    {
        for (var i = activeFormattingElements.Count - 1; i >= 0; i--)
        {
            var node = activeFormattingElements[i];
            if (node == Marker)
            {
                break;
            }
            if (IsHtml(node, "a"))
            {
                anchorIndex = i;
                return true;
            }
        }
        anchorIndex = -1;
        return false;
    }

    int CountFormattingElementsOfTag(HtmlElement desired, out int earliestMatchingIndex)
    {
        earliestMatchingIndex = activeFormattingElements.Count;
        var count = 0;
        for (var i = activeFormattingElements.Count - 1; i >= 0; i--)
        {
            var node = activeFormattingElements[i];
            if (node == Marker)
            {
                break;
            }
            if (node.Namespace == desired.Namespace && node.Tag == desired.Tag
                && AllAttributesMatch(node.AttributeList, desired.AttributeList))
            {
                count++;
                earliestMatchingIndex = i;
            }
        }
        return count;
    }

    void AddFormattingElement(HtmlElement node)
    {
        // Noah's Ark clause: at most three identical elements after the last marker
        if (node != Marker && CountFormattingElementsOfTag(node, out var earliest) >= 3)
        {
            activeFormattingElements.RemoveAt(earliest);
        }
        activeFormattingElements.Add(node);
    }

    bool IsOpenElement(HtmlElement node) => openElements.Contains(node);

    static HtmlElement CloneNode(HtmlElement node) =>
        new(node.Name, node.Tag, node.Namespace, node.AttributeList.ConvertAll(a => a.Clone()));

    void ReconstructActiveFormattingElements()
    {
        if (activeFormattingElements.Count == 0)
        {
            return;
        }
        var i = activeFormattingElements.Count - 1;
        var element = activeFormattingElements[i];
        if (element == Marker || IsOpenElement(element))
        {
            return;
        }
        do
        {
            if (i == 0)
            {
                i = -1;
                break;
            }
            element = activeFormattingElements[--i];
        }
        while (element != Marker && !IsOpenElement(element));

        for (++i; i < activeFormattingElements.Count; i++)
        {
            var clone = CloneNode(activeFormattingElements[i]);
            InsertNode(clone, AppropriateInsertionLocation(null));
            openElements.Add(clone);
            activeFormattingElements[i] = clone;
        }
    }

    void ClearActiveFormattingElements()
    {
        while (activeFormattingElements.Count > 0)
        {
            var node = activeFormattingElements[^1];
            activeFormattingElements.RemoveAt(activeFormattingElements.Count - 1);
            if (node == Marker)
            {
                break;
            }
        }
    }

    // gumbo_compute_quirks_mode
    static readonly string[] QuirksModePublicIdPrefixes =
    [
        "+//Silmaril//dtd html Pro v0r11 19970101//", "-//AdvaSoft Ltd//DTD HTML 3.0 asWedit + extensions//",
        "-//AS//DTD HTML 3.0 asWedit + extensions//", "-//IETF//DTD HTML 2.0 Level 1//", "-//IETF//DTD HTML 2.0 Level 2//",
        "-//IETF//DTD HTML 2.0 Strict Level 1//", "-//IETF//DTD HTML 2.0 Strict Level 2//", "-//IETF//DTD HTML 2.0 Strict//",
        "-//IETF//DTD HTML 2.0//", "-//IETF//DTD HTML 2.1E//", "-//IETF//DTD HTML 3.0//", "-//IETF//DTD HTML 3.2 Final//",
        "-//IETF//DTD HTML 3.2//", "-//IETF//DTD HTML 3//", "-//IETF//DTD HTML Level 0//", "-//IETF//DTD HTML Level 1//",
        "-//IETF//DTD HTML Level 2//", "-//IETF//DTD HTML Level 3//", "-//IETF//DTD HTML Strict Level 0//",
        "-//IETF//DTD HTML Strict Level 1//", "-//IETF//DTD HTML Strict Level 2//", "-//IETF//DTD HTML Strict Level 3//",
        "-//IETF//DTD HTML Strict//", "-//IETF//DTD HTML//", "-//Metrius//DTD Metrius Presentational//",
        "-//Microsoft//DTD Internet Explorer 2.0 HTML Strict//", "-//Microsoft//DTD Internet Explorer 2.0 HTML//",
        "-//Microsoft//DTD Internet Explorer 2.0 Tables//", "-//Microsoft//DTD Internet Explorer 3.0 HTML Strict//",
        "-//Microsoft//DTD Internet Explorer 3.0 HTML//", "-//Microsoft//DTD Internet Explorer 3.0 Tables//",
        "-//Netscape Comm. Corp.//DTD HTML//", "-//Netscape Comm. Corp.//DTD Strict HTML//",
        "-//O'Reilly and Associates//DTD HTML 2.0//", "-//O'Reilly and Associates//DTD HTML Extended 1.0//",
        "-//O'Reilly and Associates//DTD HTML Extended Relaxed 1.0//",
        "-//SoftQuad Software//DTD HoTMetaL PRO 6.0::19990601::)extensions to HTML 4.0//",
        "-//SoftQuad//DTD HoTMetaL PRO 4.0::19971010::extensions to HTML 4.0//", "-//Spyglass//DTD HTML 2.0 Extended//",
        "-//SQ//DTD HTML 2.0 HoTMetaL + extensions//", "-//Sun Microsystems Corp.//DTD HotJava HTML//",
        "-//Sun Microsystems Corp.//DTD HotJava Strict HTML//", "-//W3C//DTD HTML 3 1995-03-24//",
        "-//W3C//DTD HTML 3.2 Draft//", "-//W3C//DTD HTML 3.2 Final//", "-//W3C//DTD HTML 3.2//",
        "-//W3C//DTD HTML 3.2S Draft//", "-//W3C//DTD HTML 4.0 Frameset//", "-//W3C//DTD HTML 4.0 Transitional//",
        "-//W3C//DTD HTML Experimental 19960712//", "-//W3C//DTD HTML Experimental 970421//", "-//W3C//DTD W3 HTML//",
        "-//W3O//DTD W3 HTML 3.0//", "-//WebTechs//DTD Mozilla HTML 2.0//", "-//WebTechs//DTD Mozilla HTML//",
    ];

    static readonly string[] QuirksModePublicIdExactMatches = ["-//W3O//DTD W3 HTML Strict 3.0//EN//", "-/W3C/DTD HTML 4.0 Transitional/EN", "HTML"];
    static readonly string[] QuirksModeSystemIdExactMatches = ["http://www.ibm.com/data/dtd/v11/ibmxhtml1-transitional.dtd"];
    static readonly string[] LimitedQuirksPublicIdPrefixes = ["-//W3C//DTD XHTML 1.0 Frameset//", "-//W3C//DTD XHTML 1.0 Transitional//"];
    static readonly string[] SystemIdDependentPublicIdPrefixes = ["-//W3C//DTD HTML 4.01 Frameset//", "-//W3C//DTD HTML 4.01 Transitional//"];

    static bool IsInStaticList(string? needle, string[] haystack, bool exactMatch)
    {
        if (string.IsNullOrEmpty(needle))
        {
            return false;
        }
        foreach (var item in haystack)
        {
            if (exactMatch
                ? Tags.AsciiEqualsIgnoreCase(needle, item)
                : needle.Length >= item.Length && Tags.AsciiEqualsIgnoreCase(needle[..item.Length], item))
            {
                return true;
            }
        }
        return false;
    }

    static HtmlQuirksMode ComputeQuirksMode(DoctypeToken doctype) => doctype.ForceQuirks
        ? HtmlQuirksMode.Quirks
        : ComputeQuirksMode(
            doctype.Name,
            doctype.HasPublicIdentifier ? doctype.PublicIdentifier : null,
            doctype.HasSystemIdentifier ? doctype.SystemIdentifier : null);

    public static HtmlQuirksMode ComputeQuirksMode(string? name, string? publicId, string? systemId)
    {
        if (name != "html"
            || IsInStaticList(publicId, QuirksModePublicIdPrefixes, false)
            || IsInStaticList(publicId, QuirksModePublicIdExactMatches, true)
            || IsInStaticList(systemId, QuirksModeSystemIdExactMatches, true)
            || (systemId is null && IsInStaticList(publicId, SystemIdDependentPublicIdPrefixes, false)))
        {
            return HtmlQuirksMode.Quirks;
        }
        if (IsInStaticList(publicId, LimitedQuirksPublicIdPrefixes, false)
            || (systemId is not null && IsInStaticList(publicId, SystemIdDependentPublicIdPrefixes, false)))
        {
            return HtmlQuirksMode.LimitedQuirks;
        }
        return HtmlQuirksMode.NoQuirks;
    }

    // --- Scopes ----------------------------------------------------------------------------------

    static readonly TagSet DefaultScope = TagSet.Html("applet", "caption", "html", "table", "td", "th", "marquee", "object", "template")
        .With(HtmlNamespace.MathMl, "mi", "mo", "mn", "ms", "mtext", "annotation-xml")
        .With(HtmlNamespace.Svg, "foreignobject", "desc", "title");

    static readonly TagSet ListItemScope = DefaultScope.With(HtmlNamespace.Html, "ol", "ul");
    static readonly TagSet ButtonScope = DefaultScope.With(HtmlNamespace.Html, "button");
    static readonly TagSet TableScope = TagSet.Html("html", "table", "template");
    static readonly TagSet SelectScope = TagSet.Html("optgroup", "option");
    static readonly TagSet HtmlOnly = TagSet.Html("html");
    static readonly TagSet HeadingTags = TagSet.Html("h1", "h2", "h3", "h4", "h5", "h6");
    static readonly string[] HeadingNames = ["h1", "h2", "h3", "h4", "h5", "h6"];
    static readonly TagSet TdTh = TagSet.Html("td", "th");
    static readonly TagSet DdDt = TagSet.Html("dd", "dt");

    bool HasAnElementInSpecificScope(ReadOnlySpan<string> expected, bool negate, TagSet tags)
    {
        for (var i = openElements.Count - 1; i >= 0; i--)
        {
            var node = openElements[i];
            if (node.Namespace == HtmlNamespace.Html && expected.Contains(node.Tag))
            {
                return true;
            }
            if (negate != tags.Contains(node))
            {
                return false;
            }
        }
        return false;
    }

    bool HasOpenElement(string tag) => HasAnElementInSpecificScope([tag], false, HtmlOnly);

    bool HasAnElementInScope(string tag) => HasAnElementInSpecificScope([tag], false, DefaultScope);

    bool HasNodeInScope(HtmlElement node)
    {
        for (var i = openElements.Count - 1; i >= 0; i--)
        {
            var current = openElements[i];
            if (current == node)
            {
                return true;
            }
            if (DefaultScope.Contains(current))
            {
                return false;
            }
        }
        return false;
    }

    bool HasAnElementInListScope(string tag) => HasAnElementInSpecificScope([tag], false, ListItemScope);

    bool HasAnElementInButtonScope(string tag) => HasAnElementInSpecificScope([tag], false, ButtonScope);

    bool HasAnElementInTableScope(string tag) => HasAnElementInSpecificScope([tag], false, TableScope);

    bool HasAnElementInSelectScope(string tag) => HasAnElementInSpecificScope([tag], true, SelectScope);

    static readonly TagSet ImpliedEndTags = TagSet.Html("dd", "dt", "li", "optgroup", "option", "p", "rb", "rp", "rt", "rtc");

    static readonly TagSet ImpliedEndTagsThoroughly = TagSet.Html(
        "caption", "colgroup", "dd", "dt", "li", "optgroup", "option", "p", "rb", "rp", "rt", "rtc", "tbody", "td",
        "tfoot", "th", "thead", "tr");

    // exception: the tag to leave open, or null
    void GenerateImpliedEndTags(string? exception)
    {
        while (CurrentNode is { } current && ImpliedEndTags.Contains(current) && !(exception is not null && IsHtml(current, exception)))
        {
            PopCurrentNode();
        }
    }

    void GenerateAllImpliedEndTagsThoroughly()
    {
        while (CurrentNode is { } current && ImpliedEndTagsThoroughly.Contains(current))
        {
            PopCurrentNode();
        }
    }

    bool CloseTable()
    {
        if (!HasAnElementInTableScope("table"))
        {
            return false;
        }
        var node = PopCurrentNode()!;
        while (!IsHtml(node, "table"))
        {
            node = PopCurrentNode()!;
        }
        ResetInsertionModeAppropriately();
        return true;
    }

    void CloseTableCell(string cellTag)
    {
        GenerateImpliedEndTags(null);
        HtmlElement node;
        do
        {
            node = PopCurrentNode()!;
        }
        while (!IsHtml(node, cellTag));
        ClearActiveFormattingElements();
        insertionMode = InsertionMode.InRow;
    }

    void CloseCurrentCell() => CloseTableCell(HasAnElementInTableScope("td") ? "td" : "th");

    void CloseCurrentSelect()
    {
        var node = PopCurrentNode()!;
        while (!IsHtml(node, "select"))
        {
            node = PopCurrentNode()!;
        }
        ResetInsertionModeAppropriately();
    }

    static readonly TagSet SpecialTags = TagSet.Html(
            "address", "applet", "area", "article", "aside", "base", "basefont", "bgsound", "blockquote", "body", "br",
            "button", "caption", "center", "col", "colgroup", "dd", "details", "dir", "div", "dl", "dt", "embed",
            "fieldset", "figcaption", "figure", "footer", "form", "frame", "frameset", "h1", "h2", "h3", "h4", "h5",
            "h6", "head", "header", "hgroup", "hr", "html", "iframe", "img", "input", "li", "link", "listing",
            "marquee", "menu", "meta", "nav", "noembed", "noframes", "noscript", "object", "ol", "p", "param",
            "plaintext", "pre", "script", "section", "select", "style", "summary", "table", "tbody", "td", "template",
            "textarea", "tfoot", "th", "thead", "title", "tr", "ul", "wbr", "xmp")
        .With(HtmlNamespace.MathMl, "mi", "mo", "mn", "ms", "mtext", "annotation-xml")
        .With(HtmlNamespace.Svg, "foreignobject", "desc", "title");

    static bool IsSpecialNode(HtmlElement node) => SpecialTags.Contains(node);

    // Pops elements until one with the target name has been popped
    void ImplicitlyCloseTags(HtmlNamespace ns, string target)
    {
        GenerateImpliedEndTags(target);
        while (!(CurrentNode!.Namespace == ns && CurrentNode.Tag == target))
        {
            PopCurrentNode();
        }
        PopCurrentNode();
    }

    void MaybeImplicitlyCloseP()
    {
        if (HasAnElementInButtonScope("p"))
        {
            ImplicitlyCloseTags(HtmlNamespace.Html, "p");
        }
    }

    static readonly TagSet AddressDivP = TagSet.Html("address", "div", "p");

    void MaybeImplicitlyCloseListTag(bool isLi)
    {
        framesetOk = false;
        for (var i = openElements.Count - 1; i >= 0; i--)
        {
            var node = openElements[i];
            var isListTag = isLi ? IsHtml(node, "li") : DdDt.Contains(node);
            if (isListTag)
            {
                ImplicitlyCloseTags(node.Namespace, node.Tag);
                return;
            }
            if (IsSpecialNode(node) && !AddressDivP.Contains(node))
            {
                return;
            }
        }
    }

    void MergeAttributes(Token token, HtmlElement node)
    {
        // A fragment's html element is never serialized, and merging into it can take quadratic time
        if (IsFragmentParser && node == root)
        {
            return;
        }
        foreach (var attribute in token.Attributes)
        {
            if (GetAttribute(node.AttributeList, attribute.Name) is null)
            {
                node.AttributeList.Add(attribute);
            }
        }
        token.Attributes = [];
    }

    static void AdjustForeignAttributes(Token token)
    {
        var attributes = token.Attributes;
        for (var i = 0; i < attributes.Count; i++)
        {
            if (Tags.ForeignAttributes.TryGetValue(attributes[i].Name, out var replacement))
            {
                attributes[i] = new HtmlAttr(replacement.Prefix, replacement.LocalName, attributes[i].Value);
            }
        }
    }

    static void AdjustSvgTag(Token token)
    {
        if (token.TagName == "foreignobject")
        {
            token.ElementName = "foreignObject";
        }
        else if (!Tags.Known.Contains(token.TagName) && Tags.SvgTagReplacements.TryGetValue(token.TagName, out var replacement))
        {
            token.ElementName = replacement;
        }
    }

    static void AdjustSvgAttributes(Token token)
    {
        foreach (var attribute in token.Attributes)
        {
            if (Tags.SvgAttributeReplacements.TryGetValue(attribute.Name, out var replacement))
            {
                attribute.Name = replacement;
            }
        }
    }

    static void AdjustMathMlAttributes(Token token)
    {
        if (GetAttribute(token.Attributes, "definitionurl") is { } attribute)
        {
            attribute.Name = "definitionURL";
        }
    }

    // The "any other end tag" steps of "in body"
    void InBodyAnyOtherEndTag(Token token)
    {
        var tag = token.TagName;
        for (var i = openElements.Count - 1; i >= 0; i--)
        {
            var node = openElements[i];
            if (IsHtml(node, tag))
            {
                GenerateImpliedEndTags(tag);
                while (node != PopCurrentNode())
                {
                }
                return;
            }
            if (IsSpecialNode(node))
            {
                IgnoreToken(token);
                return;
            }
        }
    }

    // ignore_token as Nokogiri builds Gumbo (without NDEBUG): a start tag loses its attributes.
    // That shows when the adoption agency ignores an <a> or <nobr> start tag that is then inserted.
    static void IgnoreToken(Token token)
    {
        if (token.Type == TokenType.StartTag)
        {
            token.Attributes = [];
        }
    }

    void AdoptionAgencyAlgorithm(Token token)
    {
        var subject = token.TagName;

        // Step 2
        var currentNode = CurrentNode!;
        if (IsHtml(currentNode, subject) && !activeFormattingElements.Contains(currentNode))
        {
            PopCurrentNode();
            return;
        }

        // Steps 3-5 & 21
        for (var outer = 0; outer < 8; outer++)
        {
            // Step 6
            HtmlElement? formattingNode = null;
            var formattingNodeInOpenElements = -1;
            for (var j = activeFormattingElements.Count - 1; j >= 0; j--)
            {
                var node = activeFormattingElements[j];
                if (node == Marker)
                {
                    break;
                }
                if (IsHtml(node, subject))
                {
                    formattingNode = node;
                    formattingNodeInOpenElements = openElements.IndexOf(node);
                    break;
                }
            }
            if (formattingNode is null)
            {
                InBodyAnyOtherEndTag(token);
                return;
            }

            // Step 7
            if (formattingNodeInOpenElements == -1)
            {
                activeFormattingElements.Remove(formattingNode);
                return;
            }

            // Step 8
            if (!HasAnElementInScope(formattingNode.Tag))
            {
                return;
            }

            // Step 10
            HtmlElement? furthestBlock = null;
            for (var j = formattingNodeInOpenElements; j < openElements.Count; j++)
            {
                if (IsSpecialNode(openElements[j]))
                {
                    furthestBlock = openElements[j];
                    break;
                }
            }

            // Step 11
            if (furthestBlock is null)
            {
                while (PopCurrentNode() != formattingNode)
                {
                }
                activeFormattingElements.Remove(formattingNode);
                return;
            }

            // Step 12
            var commonAncestor = openElements[formattingNodeInOpenElements - 1];

            // Step 13
            var bookmark = 1 + activeFormattingElements.IndexOf(formattingNode);

            // Step 14
            var node14 = furthestBlock;
            var lastNode = furthestBlock;
            var savedNodeIndex = openElements.IndexOf(node14);
            for (var j = 0; ;)
            {
                j++;
                var nodeIndex = openElements.IndexOf(node14);
                if (nodeIndex == -1)
                {
                    nodeIndex = savedNodeIndex;
                }
                savedNodeIndex = --nodeIndex;
                node14 = openElements[nodeIndex];
                if (node14 == formattingNode)
                {
                    break;
                }
                var formattingIndex = activeFormattingElements.IndexOf(node14);
                if (j > 3 && formattingIndex != -1)
                {
                    activeFormattingElements.RemoveAt(formattingIndex);
                    if (formattingIndex < bookmark)
                    {
                        bookmark--;
                    }
                    continue;
                }
                if (formattingIndex == -1)
                {
                    openElements.RemoveAt(nodeIndex);
                    continue;
                }
                node14 = CloneNode(node14);
                activeFormattingElements[formattingIndex] = node14;
                openElements[nodeIndex] = node14;
                if (lastNode == furthestBlock)
                {
                    bookmark = formattingIndex + 1;
                }
                lastNode.Remove();
                node14.AppendChild(lastNode);
                lastNode = node14;
            }

            // Step 15
            lastNode.Remove();
            InsertNode(lastNode, AppropriateInsertionLocation(commonAncestor));

            // Steps 16-18: the new formatting element takes the furthest block's children
            var newFormattingNode = CloneNode(formattingNode);
            foreach (var child in furthestBlock.ChildList.ToArray())
            {
                newFormattingNode.AppendChild(child);
            }
            furthestBlock.AppendChild(newFormattingNode);

            // Step 19
            var formattingNodeIndex = activeFormattingElements.IndexOf(formattingNode);
            if (formattingNodeIndex < bookmark)
            {
                bookmark--;
            }
            activeFormattingElements.RemoveAt(formattingNodeIndex);
            activeFormattingElements.Insert(bookmark, newFormattingNode);

            // Step 20
            openElements.Remove(formattingNode);
            openElements.Insert(openElements.IndexOf(furthestBlock) + 1, newFormattingNode);
        }
    }

    // --- Insertion modes -------------------------------------------------------------------------

    void HandleInitial(Token token)
    {
        if (token.Type == TokenType.Whitespace)
        {
            return;
        }
        if (token.Type == TokenType.Comment)
        {
            AppendComment(document, token);
            return;
        }
        if (token.Type == TokenType.Doctype)
        {
            var d = token.Doctype;
            doctype = new HtmlDoctype(d.Name ?? "", d.PublicIdentifier ?? "", d.SystemIdentifier ?? "");
            document.QuirksMode = ComputeQuirksMode(d);
            insertionMode = InsertionMode.BeforeHtml;
            return;
        }
        document.QuirksMode = HtmlQuirksMode.Quirks;
        insertionMode = InsertionMode.BeforeHtml;
        reprocessCurrentToken = true;
    }

    static readonly TagSet HeadBodyHtmlBr = TagSet.Html("head", "body", "html", "br");

    void HandleBeforeHtml(Token token)
    {
        if (token.Type is TokenType.Doctype or TokenType.Whitespace)
        {
            return;
        }
        if (token.Type == TokenType.Comment)
        {
            AppendComment(document, token);
            return;
        }
        if (TagIs(token, start, "html"))
        {
            root = InsertElementFromToken(token);
            insertionMode = InsertionMode.BeforeHead;
            return;
        }
        if (token.Type == TokenType.EndTag && !TagIn(token, end, HeadBodyHtmlBr))
        {
            return;
        }
        root = InsertElementOfTag("html");
        insertionMode = InsertionMode.BeforeHead;
        reprocessCurrentToken = true;
    }

    void HandleBeforeHead(Token token)
    {
        if (token.Type is TokenType.Whitespace or TokenType.Doctype)
        {
            return;
        }
        if (token.Type == TokenType.Comment)
        {
            AppendComment(CurrentNode!, token);
            return;
        }
        if (TagIs(token, start, "html"))
        {
            HandleInBody(token);
            return;
        }
        if (TagIs(token, start, "head"))
        {
            headElement = InsertElementFromToken(token);
            insertionMode = InsertionMode.InHead;
            return;
        }
        if (token.Type == TokenType.EndTag && !TagIn(token, end, HeadBodyHtmlBr))
        {
            return;
        }
        headElement = InsertElementOfTag("head");
        insertionMode = InsertionMode.InHead;
        reprocessCurrentToken = true;
    }

    static readonly TagSet InHeadVoidTags = TagSet.Html("base", "basefont", "bgsound", "link");
    static readonly TagSet NoframesStyle = TagSet.Html("noframes", "style");
    static readonly TagSet BodyHtmlBr = TagSet.Html("body", "html", "br");

    void HandleInHead(Token token)
    {
        if (token.Type == TokenType.Whitespace)
        {
            InsertTextToken(token);
            return;
        }
        if (token.Type == TokenType.Comment)
        {
            AppendComment(CurrentNode!, token);
            return;
        }
        if (token.Type == TokenType.Doctype)
        {
            return;
        }
        if (TagIs(token, start, "html"))
        {
            HandleInBody(token);
            return;
        }
        if (TagIn(token, start, InHeadVoidTags) || TagIs(token, start, "meta"))
        {
            InsertElementFromToken(token);
            PopCurrentNode();
            return;
        }
        if (TagIs(token, start, "title"))
        {
            RunGenericParsingAlgorithm(token, TokenizerState.RcData);
            return;
        }
        if (TagIn(token, start, NoframesStyle))
        {
            RunGenericParsingAlgorithm(token, TokenizerState.RawText);
            return;
        }
        if (TagIs(token, start, "noscript"))
        {
            InsertElementFromToken(token);
            insertionMode = InsertionMode.InHeadNoscript;
            return;
        }
        if (TagIs(token, start, "script"))
        {
            RunGenericParsingAlgorithm(token, TokenizerState.ScriptData);
            return;
        }
        if (TagIs(token, end, "head"))
        {
            PopCurrentNode();
            insertionMode = InsertionMode.AfterHead;
            return;
        }
        if (TagIn(token, end, BodyHtmlBr))
        {
            PopCurrentNode();
            insertionMode = InsertionMode.AfterHead;
            reprocessCurrentToken = true;
            return;
        }
        if (TagIs(token, start, "template"))
        {
            InsertElementFromToken(token);
            AddFormattingElement(Marker);
            framesetOk = false;
            insertionMode = InsertionMode.InTemplate;
            templateInsertionModes.Add(InsertionMode.InTemplate);
            return;
        }
        if (TagIs(token, end, "template"))
        {
            if (!HasOpenElement("template"))
            {
                return;
            }
            GenerateAllImpliedEndTagsThoroughly();
            while (!IsHtml(PopCurrentNode()!, "template"))
            {
            }
            ClearActiveFormattingElements();
            templateInsertionModes.RemoveAt(templateInsertionModes.Count - 1);
            ResetInsertionModeAppropriately();
            return;
        }
        if (TagIs(token, start, "head") || token.Type == TokenType.EndTag)
        {
            return;
        }
        PopCurrentNode();
        insertionMode = InsertionMode.AfterHead;
        reprocessCurrentToken = true;
    }

    static readonly TagSet InHeadNoscriptTags = TagSet.Html("basefont", "bgsound", "link", "meta", "noframes", "style");
    static readonly TagSet HeadNoscript = TagSet.Html("head", "noscript");

    void HandleInHeadNoscript(Token token)
    {
        if (token.Type == TokenType.Doctype)
        {
            return;
        }
        if (TagIs(token, start, "html"))
        {
            HandleInBody(token);
            return;
        }
        if (TagIs(token, end, "noscript"))
        {
            PopCurrentNode();
            insertionMode = InsertionMode.InHead;
            return;
        }
        if (token.Type is TokenType.Whitespace or TokenType.Comment || TagIn(token, start, InHeadNoscriptTags))
        {
            HandleInHead(token);
            return;
        }
        if (TagIn(token, start, HeadNoscript) || (token.Type == TokenType.EndTag && !TagIs(token, end, "br")))
        {
            return;
        }
        PopCurrentNode();
        insertionMode = InsertionMode.InHead;
        reprocessCurrentToken = true;
    }

    static readonly TagSet AfterHeadInHeadTags = TagSet.Html(
        "base", "basefont", "bgsound", "link", "meta", "noframes", "script", "style", "template", "title");

    void HandleAfterHead(Token token)
    {
        if (token.Type == TokenType.Whitespace)
        {
            InsertTextToken(token);
            return;
        }
        if (token.Type == TokenType.Comment)
        {
            AppendComment(CurrentNode!, token);
            return;
        }
        if (token.Type == TokenType.Doctype)
        {
            return;
        }
        if (TagIs(token, start, "html"))
        {
            HandleInBody(token);
            return;
        }
        if (TagIs(token, start, "body"))
        {
            InsertElementFromToken(token);
            framesetOk = false;
            insertionMode = InsertionMode.InBody;
            return;
        }
        if (TagIs(token, start, "frameset"))
        {
            InsertElementFromToken(token);
            insertionMode = InsertionMode.InFrameset;
            return;
        }
        if (TagIn(token, start, AfterHeadInHeadTags))
        {
            // Pending characters belong to the root, so they go before the head is pushed
            FlushTextBuffer();
            openElements.Add(headElement!);
            HandleInHead(token);
            openElements.Remove(headElement!);
            return;
        }
        if (TagIs(token, end, "template"))
        {
            HandleInHead(token);
            return;
        }
        if (TagIs(token, start, "head") || (token.Type == TokenType.EndTag && !TagIn(token, end, BodyHtmlBr)))
        {
            return;
        }
        InsertElementOfTag("body");
        insertionMode = InsertionMode.InBody;
        reprocessCurrentToken = true;
    }

    static readonly TagSet InBodyInHeadTags = AfterHeadInHeadTags;

    static readonly TagSet InBodyBlockStartTags = TagSet.Html(
        "address", "article", "aside", "blockquote", "center", "details", "dialog", "dir", "div", "dl", "fieldset",
        "figcaption", "figure", "footer", "header", "hgroup", "main", "menu", "nav", "ol", "p", "section", "summary",
        "ul", "search");

    static readonly TagSet PreListing = TagSet.Html("pre", "listing");

    static readonly TagSet InBodyBlockEndTags = TagSet.Html(
        "address", "article", "aside", "blockquote", "button", "center", "details", "dialog", "dir", "div", "dl",
        "fieldset", "figcaption", "figure", "footer", "header", "hgroup", "listing", "main", "menu", "nav", "ol",
        "pre", "section", "summary", "ul", "search");

    static readonly TagSet FormattingStartTags = TagSet.Html(
        "b", "big", "code", "em", "font", "i", "s", "small", "strike", "strong", "tt", "u");

    static readonly TagSet FormattingEndTags = TagSet.Html(
        "a", "b", "big", "code", "em", "font", "i", "nobr", "s", "small", "strike", "strong", "tt", "u");

    static readonly TagSet AppletMarqueeObject = TagSet.Html("applet", "marquee", "object");
    static readonly TagSet InBodyVoidTags = TagSet.Html("area", "br", "embed", "img", "image", "keygen", "wbr");
    static readonly TagSet ParamSourceTrack = TagSet.Html("param", "source", "track");
    static readonly TagSet OptgroupOption = TagSet.Html("optgroup", "option");
    static readonly TagSet RbRtc = TagSet.Html("rb", "rtc");
    static readonly TagSet RpRt = TagSet.Html("rp", "rt");

    static readonly TagSet InBodyIgnoredStartTags = TagSet.Html(
        "caption", "col", "colgroup", "frame", "head", "tbody", "td", "tfoot", "th", "thead", "tr");


    void HandleInBody(Token token)
    {
        switch (token.Type)
        {
            case TokenType.Null:
                return;
            case TokenType.Whitespace:
                ReconstructActiveFormattingElements();
                InsertTextToken(token);
                return;
            case TokenType.Character or TokenType.CData:
                ReconstructActiveFormattingElements();
                InsertTextToken(token);
                framesetOk = false;
                return;
            case TokenType.Comment:
                AppendComment(CurrentNode!, token);
                return;
            case TokenType.Doctype:
                return;
            case TokenType.Eof:
                if (CurrentTemplateInsertionMode != InsertionMode.Initial)
                {
                    HandleInTemplate(token);
                }
                return;
        }

        if (TagIs(token, start, "html"))
        {
            if (HasOpenElement("template"))
            {
                return;
            }
            MergeAttributes(token, root!);
            return;
        }
        if (TagIn(token, start, InBodyInHeadTags) || TagIs(token, end, "template"))
        {
            HandleInHead(token);
            return;
        }
        if (TagIs(token, start, "body"))
        {
            if (openElements.Count < 2 || !IsHtml(openElements[1], "body") || HasOpenElement("template"))
            {
                return;
            }
            framesetOk = false;
            MergeAttributes(token, openElements[1]);
            return;
        }
        if (TagIs(token, start, "frameset"))
        {
            if (openElements.Count < 2 || !IsHtml(openElements[1], "body") || !framesetOk)
            {
                return;
            }
            // Everything but the root, body included (Gumbo compares with the stale slot body was in)
            var body = openElements[1];
            while (PopCurrentNode() != body)
            {
            }
            ClearActiveFormattingElements();
            root!.RemoveChild(body);
            InsertElementFromToken(token);
            insertionMode = InsertionMode.InFrameset;
            return;
        }
        if (TagIs(token, end, "body"))
        {
            if (!HasAnElementInScope("body"))
            {
                return;
            }
            insertionMode = InsertionMode.AfterBody;
            return;
        }
        if (TagIs(token, end, "html"))
        {
            if (!HasAnElementInScope("body"))
            {
                return;
            }
            insertionMode = InsertionMode.AfterBody;
            reprocessCurrentToken = true;
            return;
        }
        if (TagIn(token, start, InBodyBlockStartTags))
        {
            MaybeImplicitlyCloseP();
            InsertElementFromToken(token);
            return;
        }
        if (TagIn(token, start, HeadingTags))
        {
            MaybeImplicitlyCloseP();
            if (HeadingTags.Contains(CurrentNode!))
            {
                PopCurrentNode();
            }
            InsertElementFromToken(token);
            return;
        }
        if (TagIn(token, start, PreListing))
        {
            MaybeImplicitlyCloseP();
            InsertElementFromToken(token);
            ignoreNextLinefeed = true;
            framesetOk = false;
            return;
        }
        if (TagIs(token, start, "form"))
        {
            if (formElement is not null && !HasOpenElement("template"))
            {
                return;
            }
            MaybeImplicitlyCloseP();
            var form = InsertElementFromToken(token);
            if (!HasOpenElement("template"))
            {
                formElement = form;
            }
            return;
        }
        if (TagIs(token, start, "li"))
        {
            MaybeImplicitlyCloseListTag(isLi: true);
            MaybeImplicitlyCloseP();
            InsertElementFromToken(token);
            return;
        }
        if (TagIn(token, start, DdDt))
        {
            MaybeImplicitlyCloseListTag(isLi: false);
            MaybeImplicitlyCloseP();
            InsertElementFromToken(token);
            return;
        }
        if (TagIs(token, start, "plaintext"))
        {
            MaybeImplicitlyCloseP();
            InsertElementFromToken(token);
            tokenizer.SetState(TokenizerState.PlainText);
            return;
        }
        if (TagIs(token, start, "button"))
        {
            if (HasAnElementInScope("button"))
            {
                GenerateImpliedEndTags(null);
                while (!IsHtml(PopCurrentNode()!, "button"))
                {
                }
            }
            ReconstructActiveFormattingElements();
            InsertElementFromToken(token);
            framesetOk = false;
            return;
        }
        if (TagIn(token, end, InBodyBlockEndTags))
        {
            if (!HasAnElementInScope(token.TagName))
            {
                return;
            }
            ImplicitlyCloseTags(HtmlNamespace.Html, token.TagName);
            return;
        }
        if (TagIs(token, end, "form"))
        {
            if (HasOpenElement("template"))
            {
                if (!HasAnElementInScope("form"))
                {
                    return;
                }
                GenerateImpliedEndTags(null);
                while (!IsHtml(PopCurrentNode()!, "form"))
                {
                }
                return;
            }
            var node = formElement;
            formElement = null;
            if (node is null || !HasNodeInScope(node))
            {
                return;
            }
            // Only the form is removed from the stack, so text pending inside it is flushed first
            FlushTextBuffer();
            GenerateImpliedEndTags(null);
            openElements.Remove(node);
            return;
        }
        if (TagIs(token, end, "p"))
        {
            if (!HasAnElementInButtonScope("p"))
            {
                InsertElementOfTag("p");
            }
            ImplicitlyCloseTags(HtmlNamespace.Html, "p");
            return;
        }
        if (TagIs(token, end, "li"))
        {
            if (!HasAnElementInListScope("li"))
            {
                return;
            }
            ImplicitlyCloseTags(HtmlNamespace.Html, "li");
            return;
        }
        if (TagIn(token, end, DdDt))
        {
            if (!HasAnElementInScope(token.TagName))
            {
                return;
            }
            ImplicitlyCloseTags(HtmlNamespace.Html, token.TagName);
            return;
        }
        if (TagIn(token, end, HeadingTags))
        {
            if (!HasAnElementInSpecificScope(HeadingNames, false, DefaultScope))
            {
                return;
            }
            GenerateImpliedEndTags(null);
            HtmlElement current;
            do
            {
                current = PopCurrentNode()!;
            }
            while (!HeadingTags.Contains(current));
            return;
        }
        if (TagIs(token, start, "a"))
        {
            if (FindLastAnchorIndex(out _))
            {
                AdoptionAgencyAlgorithm(token);
                // In case the adoption agency left the <a> in the list
                if (FindLastAnchorIndex(out var lastA))
                {
                    var lastElement = activeFormattingElements[lastA];
                    activeFormattingElements.RemoveAt(lastA);
                    openElements.Remove(lastElement);
                }
            }
            ReconstructActiveFormattingElements();
            AddFormattingElement(InsertElementFromToken(token));
            return;
        }
        if (TagIn(token, start, FormattingStartTags))
        {
            ReconstructActiveFormattingElements();
            AddFormattingElement(InsertElementFromToken(token));
            return;
        }
        if (TagIs(token, start, "nobr"))
        {
            ReconstructActiveFormattingElements();
            if (HasAnElementInScope("nobr"))
            {
                AdoptionAgencyAlgorithm(token);
                ReconstructActiveFormattingElements();
            }
            InsertElementFromToken(token);
            AddFormattingElement(CurrentNode!);
            return;
        }
        if (TagIn(token, end, FormattingEndTags))
        {
            AdoptionAgencyAlgorithm(token);
            return;
        }
        if (TagIn(token, start, AppletMarqueeObject))
        {
            ReconstructActiveFormattingElements();
            InsertElementFromToken(token);
            AddFormattingElement(Marker);
            framesetOk = false;
            return;
        }
        if (TagIn(token, end, AppletMarqueeObject))
        {
            if (!HasAnElementInScope(token.TagName))
            {
                return;
            }
            ImplicitlyCloseTags(HtmlNamespace.Html, token.TagName);
            ClearActiveFormattingElements();
            return;
        }
        if (TagIs(token, start, "table"))
        {
            if (document.QuirksMode != HtmlQuirksMode.Quirks)
            {
                MaybeImplicitlyCloseP();
            }
            InsertElementFromToken(token);
            framesetOk = false;
            insertionMode = InsertionMode.InTable;
            return;
        }
        if (TagIs(token, end, "br"))
        {
            ReconstructActiveFormattingElements();
            InsertElementOfTag("br");
            PopCurrentNode();
            framesetOk = false;
            return;
        }
        if (TagIn(token, start, InBodyVoidTags))
        {
            if (token.TagName == "image")
            {
                token.TagName = "img";
            }
            ReconstructActiveFormattingElements();
            InsertElementFromToken(token);
            PopCurrentNode();
            framesetOk = false;
            return;
        }
        if (TagIs(token, start, "input"))
        {
            ReconstructActiveFormattingElements();
            var input = InsertElementFromToken(token);
            PopCurrentNode();
            if (!AttributeMatches(input.AttributeList, "type", "hidden"))
            {
                framesetOk = false;
            }
            return;
        }
        if (TagIn(token, start, ParamSourceTrack))
        {
            InsertElementFromToken(token);
            PopCurrentNode();
            return;
        }
        if (TagIs(token, start, "hr"))
        {
            MaybeImplicitlyCloseP();
            InsertElementFromToken(token);
            PopCurrentNode();
            framesetOk = false;
            return;
        }
        if (TagIs(token, start, "textarea"))
        {
            RunGenericParsingAlgorithm(token, TokenizerState.RcData);
            ignoreNextLinefeed = true;
            framesetOk = false;
            return;
        }
        if (TagIs(token, start, "xmp"))
        {
            MaybeImplicitlyCloseP();
            ReconstructActiveFormattingElements();
            framesetOk = false;
            RunGenericParsingAlgorithm(token, TokenizerState.RawText);
            return;
        }
        if (TagIs(token, start, "iframe"))
        {
            framesetOk = false;
            RunGenericParsingAlgorithm(token, TokenizerState.RawText);
            return;
        }
        if (TagIs(token, start, "noembed"))
        {
            RunGenericParsingAlgorithm(token, TokenizerState.RawText);
            return;
        }
        if (TagIs(token, start, "select"))
        {
            ReconstructActiveFormattingElements();
            InsertElementFromToken(token);
            framesetOk = false;
            insertionMode = insertionMode is InsertionMode.InTable or InsertionMode.InCaption or InsertionMode.InTableBody
                or InsertionMode.InRow or InsertionMode.InCell
                ? InsertionMode.InSelectInTable
                : InsertionMode.InSelect;
            return;
        }
        if (TagIn(token, start, OptgroupOption))
        {
            if (IsHtml(CurrentNode!, "option"))
            {
                PopCurrentNode();
            }
            ReconstructActiveFormattingElements();
            InsertElementFromToken(token);
            return;
        }
        if (TagIn(token, start, RbRtc))
        {
            if (HasAnElementInScope("ruby"))
            {
                GenerateImpliedEndTags(null);
            }
            InsertElementFromToken(token);
            return;
        }
        if (TagIn(token, start, RpRt))
        {
            if (HasAnElementInScope("ruby"))
            {
                GenerateImpliedEndTags("rtc");
            }
            InsertElementFromToken(token);
            return;
        }
        if (TagIs(token, start, "math"))
        {
            ReconstructActiveFormattingElements();
            AdjustMathMlAttributes(token);
            AdjustForeignAttributes(token);
            InsertForeignElement(token, HtmlNamespace.MathMl);
            if (token.IsSelfClosing)
            {
                PopCurrentNode();
            }
            return;
        }
        if (TagIs(token, start, "svg"))
        {
            ReconstructActiveFormattingElements();
            AdjustSvgAttributes(token);
            AdjustForeignAttributes(token);
            InsertForeignElement(token, HtmlNamespace.Svg);
            if (token.IsSelfClosing)
            {
                PopCurrentNode();
            }
            return;
        }
        if (TagIn(token, start, InBodyIgnoredStartTags))
        {
            return;
        }
        if (token.Type == TokenType.StartTag)
        {
            ReconstructActiveFormattingElements();
            InsertElementFromToken(token);
            return;
        }
        InBodyAnyOtherEndTag(token);
    }

    void HandleText(Token token)
    {
        if (token.Type is TokenType.Character or TokenType.Whitespace)
        {
            InsertTextToken(token);
            return;
        }
        if (token.Type == TokenType.Eof)
        {
            reprocessCurrentToken = true;
        }
        PopCurrentNode();
        insertionMode = originalInsertionMode;
    }

    static readonly TagSet TableTextContext = TagSet.Html("table", "tbody", "template", "tfoot", "thead", "tr");
    static readonly TagSet TableSectionTags = TagSet.Html("tbody", "tfoot", "thead");
    static readonly TagSet CellAndRowTags = TagSet.Html("td", "th", "tr");

    static readonly TagSet InTableIgnoredEndTags = TagSet.Html(
        "body", "caption", "col", "colgroup", "html", "tbody", "td", "tfoot", "th", "thead", "tr");

    static readonly TagSet StyleScriptTemplate = TagSet.Html("style", "script", "template");

    void HandleInTable(Token token)
    {
        if (token.Type is TokenType.Character or TokenType.Whitespace or TokenType.Null && TableTextContext.Contains(CurrentNode!))
        {
            // The pending table character tokens are the text buffer itself
            originalInsertionMode = insertionMode;
            reprocessCurrentToken = true;
            insertionMode = InsertionMode.InTableText;
            return;
        }
        if (token.Type == TokenType.Comment)
        {
            AppendComment(CurrentNode!, token);
            return;
        }
        if (token.Type == TokenType.Doctype)
        {
            return;
        }
        if (TagIs(token, start, "caption"))
        {
            ClearStackBackTo(TableContext);
            AddFormattingElement(Marker);
            InsertElementFromToken(token);
            insertionMode = InsertionMode.InCaption;
            return;
        }
        if (TagIs(token, start, "colgroup"))
        {
            ClearStackBackTo(TableContext);
            InsertElementFromToken(token);
            insertionMode = InsertionMode.InColumnGroup;
            return;
        }
        if (TagIs(token, start, "col"))
        {
            ClearStackBackTo(TableContext);
            InsertElementOfTag("colgroup");
            reprocessCurrentToken = true;
            insertionMode = InsertionMode.InColumnGroup;
            return;
        }
        if (TagIn(token, start, TableSectionTags))
        {
            ClearStackBackTo(TableContext);
            InsertElementFromToken(token);
            insertionMode = InsertionMode.InTableBody;
            return;
        }
        if (TagIn(token, start, CellAndRowTags))
        {
            ClearStackBackTo(TableContext);
            InsertElementOfTag("tbody");
            insertionMode = InsertionMode.InTableBody;
            reprocessCurrentToken = true;
            return;
        }
        if (TagIs(token, start, "table"))
        {
            if (CloseTable())
            {
                reprocessCurrentToken = true;
            }
            return;
        }
        if (TagIs(token, end, "table"))
        {
            CloseTable();
            return;
        }
        if (TagIn(token, end, InTableIgnoredEndTags))
        {
            return;
        }
        if (TagIn(token, start, StyleScriptTemplate) || TagIs(token, end, "template"))
        {
            HandleInHead(token);
            return;
        }
        if (TagIs(token, start, "input") && AttributeMatches(token.Attributes, "type", "hidden"))
        {
            InsertElementFromToken(token);
            PopCurrentNode();
            return;
        }
        if (TagIs(token, start, "form"))
        {
            if (formElement is not null || HasOpenElement("template"))
            {
                return;
            }
            formElement = InsertElementFromToken(token);
            PopCurrentNode();
            return;
        }
        if (token.Type == TokenType.Eof)
        {
            HandleInBody(token);
            return;
        }
        fosterParentInsertions = true;
        HandleInBody(token);
        fosterParentInsertions = false;
    }

    void HandleInTableText(Token token)
    {
        if (token.Type == TokenType.Null)
        {
            return;
        }
        if (token.Type is TokenType.Whitespace or TokenType.Character)
        {
            InsertTextToken(token);
            return;
        }
        // Text with anything but whitespace is foster parented
        if (textKind != TextKind.Whitespace)
        {
            fosterParentInsertions = true;
            framesetOk = false;
            ReconstructActiveFormattingElements();
        }
        FlushTextBuffer();
        fosterParentInsertions = false;
        reprocessCurrentToken = true;
        insertionMode = originalInsertionMode;
    }

    static readonly TagSet InCaptionTableStartTags = TagSet.Html(
        "caption", "col", "colgroup", "tbody", "td", "tfoot", "th", "thead", "tr");

    static readonly TagSet InCaptionIgnoredEndTags = TagSet.Html(
        "body", "col", "colgroup", "html", "tbody", "td", "tfoot", "th", "thead", "tr");

    void HandleInCaption(Token token)
    {
        if (TagIs(token, end, "caption"))
        {
            if (!HasAnElementInTableScope("caption"))
            {
                return;
            }
            GenerateImpliedEndTags(null);
            while (!IsHtml(PopCurrentNode()!, "caption"))
            {
            }
            ClearActiveFormattingElements();
            insertionMode = InsertionMode.InTable;
            return;
        }
        if (TagIn(token, start, InCaptionTableStartTags) || TagIs(token, end, "table"))
        {
            if (!HasAnElementInTableScope("caption"))
            {
                return;
            }
            GenerateImpliedEndTags(null);
            while (!IsHtml(PopCurrentNode()!, "caption"))
            {
            }
            ClearActiveFormattingElements();
            insertionMode = InsertionMode.InTable;
            reprocessCurrentToken = true;
            return;
        }
        if (TagIn(token, end, InCaptionIgnoredEndTags))
        {
            return;
        }
        HandleInBody(token);
    }

    void HandleInColumnGroup(Token token)
    {
        if (token.Type == TokenType.Whitespace)
        {
            InsertTextToken(token);
            return;
        }
        if (token.Type == TokenType.Comment)
        {
            AppendComment(CurrentNode!, token);
            return;
        }
        if (token.Type == TokenType.Doctype)
        {
            return;
        }
        if (TagIs(token, start, "html"))
        {
            HandleInBody(token);
            return;
        }
        if (TagIs(token, start, "col"))
        {
            InsertElementFromToken(token);
            PopCurrentNode();
            return;
        }
        if (TagIs(token, end, "colgroup"))
        {
            if (!IsHtml(CurrentNode!, "colgroup"))
            {
                return;
            }
            PopCurrentNode();
            insertionMode = InsertionMode.InTable;
            return;
        }
        if (TagIs(token, end, "col"))
        {
            return;
        }
        if (TagIs(token, start, "template") || TagIs(token, end, "template"))
        {
            HandleInHead(token);
            return;
        }
        if (token.Type == TokenType.Eof)
        {
            HandleInBody(token);
            return;
        }
        if (!IsHtml(CurrentNode!, "colgroup"))
        {
            return;
        }
        PopCurrentNode();
        insertionMode = InsertionMode.InTable;
        reprocessCurrentToken = true;
    }

    static readonly TagSet InTableBodyTableTags = TagSet.Html("caption", "col", "colgroup", "tbody", "tfoot", "thead");
    static readonly TagSet InTableBodyIgnoredEndTags = TagSet.Html("body", "caption", "col", "colgroup", "html", "td", "th", "tr");

    void HandleInTableBody(Token token)
    {
        if (TagIs(token, start, "tr"))
        {
            ClearStackBackTo(TableBodyContext);
            InsertElementFromToken(token);
            insertionMode = InsertionMode.InRow;
            return;
        }
        if (TagIn(token, start, TdTh))
        {
            ClearStackBackTo(TableBodyContext);
            InsertElementOfTag("tr");
            insertionMode = InsertionMode.InRow;
            reprocessCurrentToken = true;
            return;
        }
        if (TagIn(token, end, TableSectionTags))
        {
            if (!HasAnElementInTableScope(token.TagName))
            {
                return;
            }
            ClearStackBackTo(TableBodyContext);
            PopCurrentNode();
            insertionMode = InsertionMode.InTable;
            return;
        }
        if (TagIn(token, start, InTableBodyTableTags) || TagIs(token, end, "table"))
        {
            if (!(HasAnElementInTableScope("tbody") || HasAnElementInTableScope("thead") || HasAnElementInTableScope("tfoot")))
            {
                return;
            }
            ClearStackBackTo(TableBodyContext);
            PopCurrentNode();
            insertionMode = InsertionMode.InTable;
            reprocessCurrentToken = true;
            return;
        }
        if (TagIn(token, end, InTableBodyIgnoredEndTags))
        {
            return;
        }
        HandleInTable(token);
    }

    static readonly TagSet InRowTableTags = TagSet.Html("caption", "col", "colgroup", "tbody", "tfoot", "thead", "tr");
    static readonly TagSet InRowIgnoredEndTags = TagSet.Html("body", "caption", "col", "colgroup", "html", "td", "th");

    void HandleInRow(Token token)
    {
        if (TagIn(token, start, TdTh))
        {
            ClearStackBackTo(TableRowContext);
            InsertElementFromToken(token);
            insertionMode = InsertionMode.InCell;
            AddFormattingElement(Marker);
            return;
        }
        if (TagIs(token, end, "tr"))
        {
            if (!HasAnElementInTableScope("tr"))
            {
                return;
            }
            ClearStackBackTo(TableRowContext);
            PopCurrentNode();
            insertionMode = InsertionMode.InTableBody;
            return;
        }
        if (TagIn(token, start, InRowTableTags) || TagIs(token, end, "table"))
        {
            if (!HasAnElementInTableScope("tr"))
            {
                return;
            }
            ClearStackBackTo(TableRowContext);
            PopCurrentNode();
            insertionMode = InsertionMode.InTableBody;
            reprocessCurrentToken = true;
            return;
        }
        if (TagIn(token, end, TableSectionTags))
        {
            if (!HasAnElementInTableScope(token.TagName) || !HasAnElementInTableScope("tr"))
            {
                return;
            }
            ClearStackBackTo(TableRowContext);
            PopCurrentNode();
            insertionMode = InsertionMode.InTableBody;
            reprocessCurrentToken = true;
            return;
        }
        if (TagIn(token, end, InRowIgnoredEndTags))
        {
            return;
        }
        HandleInTable(token);
    }

    static readonly TagSet InCellIgnoredEndTags = TagSet.Html("body", "caption", "col", "colgroup", "html");
    static readonly TagSet InCellTableEndTags = TagSet.Html("table", "tbody", "tfoot", "thead", "tr");

    void HandleInCell(Token token)
    {
        if (TagIn(token, end, TdTh))
        {
            if (!HasAnElementInTableScope(token.TagName))
            {
                return;
            }
            CloseTableCell(token.TagName);
            return;
        }
        if (TagIn(token, start, InCaptionTableStartTags))
        {
            if (!HasAnElementInTableScope("th") && !HasAnElementInTableScope("td"))
            {
                return;
            }
            reprocessCurrentToken = true;
            CloseCurrentCell();
            return;
        }
        if (TagIn(token, end, InCellIgnoredEndTags))
        {
            return;
        }
        if (TagIn(token, end, InCellTableEndTags))
        {
            if (!HasAnElementInTableScope(token.TagName))
            {
                return;
            }
            reprocessCurrentToken = true;
            CloseCurrentCell();
            return;
        }
        HandleInBody(token);
    }

    static readonly TagSet InputKeygenTextarea = TagSet.Html("input", "keygen", "textarea");
    static readonly TagSet ScriptTemplate = TagSet.Html("script", "template");

    void HandleInSelect(Token token)
    {
        switch (token.Type)
        {
            case TokenType.Null or TokenType.Doctype:
                return;
            case TokenType.Character or TokenType.Whitespace:
                InsertTextToken(token);
                return;
            case TokenType.Comment:
                AppendComment(CurrentNode!, token);
                return;
        }
        if (TagIs(token, start, "html"))
        {
            HandleInBody(token);
            return;
        }
        if (TagIs(token, start, "option"))
        {
            if (IsHtml(CurrentNode!, "option"))
            {
                PopCurrentNode();
            }
            InsertElementFromToken(token);
            return;
        }
        if (TagIs(token, start, "optgroup"))
        {
            if (IsHtml(CurrentNode!, "option"))
            {
                PopCurrentNode();
            }
            if (IsHtml(CurrentNode!, "optgroup"))
            {
                PopCurrentNode();
            }
            InsertElementFromToken(token);
            return;
        }
        if (TagIs(token, start, "hr"))
        {
            if (IsHtml(CurrentNode!, "option"))
            {
                PopCurrentNode();
            }
            if (IsHtml(CurrentNode!, "optgroup"))
            {
                PopCurrentNode();
            }
            InsertElementFromToken(token);
            PopCurrentNode();
            return;
        }
        if (TagIs(token, end, "optgroup"))
        {
            if (IsHtml(CurrentNode!, "option") && IsHtml(openElements[^2], "optgroup"))
            {
                PopCurrentNode();
            }
            if (IsHtml(CurrentNode!, "optgroup"))
            {
                PopCurrentNode();
            }
            return;
        }
        if (TagIs(token, end, "option"))
        {
            if (IsHtml(CurrentNode!, "option"))
            {
                PopCurrentNode();
            }
            return;
        }
        if (TagIs(token, end, "select"))
        {
            if (HasAnElementInSelectScope("select"))
            {
                CloseCurrentSelect();
            }
            return;
        }
        if (TagIs(token, start, "select"))
        {
            if (HasAnElementInSelectScope("select"))
            {
                CloseCurrentSelect();
            }
            return;
        }
        if (TagIn(token, start, InputKeygenTextarea))
        {
            if (HasAnElementInSelectScope("select"))
            {
                CloseCurrentSelect();
                reprocessCurrentToken = true;
            }
            return;
        }
        if (TagIn(token, start, ScriptTemplate) || TagIs(token, end, "template"))
        {
            HandleInHead(token);
            return;
        }
        if (token.Type == TokenType.Eof)
        {
            HandleInBody(token);
        }
    }

    static readonly TagSet InSelectInTableTags = TagSet.Html("caption", "table", "tbody", "tfoot", "thead", "tr", "td", "th");

    void HandleInSelectInTable(Token token)
    {
        if (TagIn(token, start, InSelectInTableTags))
        {
            CloseCurrentSelect();
            reprocessCurrentToken = true;
            return;
        }
        if (TagIn(token, end, InSelectInTableTags))
        {
            if (!HasAnElementInTableScope(token.TagName))
            {
                return;
            }
            CloseCurrentSelect();
            reprocessCurrentToken = true;
            return;
        }
        HandleInSelect(token);
    }

    static readonly TagSet InTemplateTableTags = TagSet.Html("caption", "colgroup", "tbody", "tfoot", "thead");

    void SwitchTemplateInsertionMode(InsertionMode mode)
    {
        templateInsertionModes.RemoveAt(templateInsertionModes.Count - 1);
        templateInsertionModes.Add(mode);
        insertionMode = mode;
        reprocessCurrentToken = true;
    }

    void HandleInTemplate(Token token)
    {
        if (token.Type is TokenType.Whitespace or TokenType.Character or TokenType.Comment or TokenType.Null or TokenType.Doctype)
        {
            HandleInBody(token);
            return;
        }
        if (TagIn(token, start, AfterHeadInHeadTags) || TagIs(token, end, "template"))
        {
            HandleInHead(token);
            return;
        }
        if (TagIn(token, start, InTemplateTableTags))
        {
            SwitchTemplateInsertionMode(InsertionMode.InTable);
            return;
        }
        if (TagIs(token, start, "col"))
        {
            SwitchTemplateInsertionMode(InsertionMode.InColumnGroup);
            return;
        }
        if (TagIs(token, start, "tr"))
        {
            SwitchTemplateInsertionMode(InsertionMode.InTableBody);
            return;
        }
        if (TagIn(token, start, TdTh))
        {
            SwitchTemplateInsertionMode(InsertionMode.InRow);
            return;
        }
        if (token.Type == TokenType.StartTag)
        {
            SwitchTemplateInsertionMode(InsertionMode.InBody);
            return;
        }
        if (token.Type == TokenType.EndTag)
        {
            return;
        }
        // end of file
        if (!HasOpenElement("template"))
        {
            return;
        }
        while (!IsHtml(PopCurrentNode()!, "template"))
        {
        }
        ClearActiveFormattingElements();
        templateInsertionModes.RemoveAt(templateInsertionModes.Count - 1);
        ResetInsertionModeAppropriately();
        reprocessCurrentToken = true;
    }

    void HandleAfterBody(Token token)
    {
        if (token.Type == TokenType.Whitespace || TagIs(token, start, "html"))
        {
            HandleInBody(token);
            return;
        }
        if (token.Type == TokenType.Comment)
        {
            AppendComment(root!, token);
            return;
        }
        if (token.Type == TokenType.Doctype)
        {
            return;
        }
        if (TagIs(token, end, "html"))
        {
            // A fragment ignores the closing html tag
            if (!IsFragmentParser)
            {
                insertionMode = InsertionMode.AfterAfterBody;
            }
            return;
        }
        if (token.Type == TokenType.Eof)
        {
            return;
        }
        insertionMode = InsertionMode.InBody;
        reprocessCurrentToken = true;
    }

    void HandleInFrameset(Token token)
    {
        switch (token.Type)
        {
            case TokenType.Whitespace:
                InsertTextToken(token);
                return;
            case TokenType.Comment:
                AppendComment(CurrentNode!, token);
                return;
            case TokenType.Doctype:
                return;
        }
        if (TagIs(token, start, "html"))
        {
            HandleInBody(token);
            return;
        }
        if (TagIs(token, start, "frameset"))
        {
            InsertElementFromToken(token);
            return;
        }
        if (TagIs(token, end, "frameset"))
        {
            if (IsHtml(CurrentNode!, "html"))
            {
                return;
            }
            PopCurrentNode();
            if (!IsFragmentParser && !IsHtml(CurrentNode!, "frameset"))
            {
                insertionMode = InsertionMode.AfterFrameset;
            }
            return;
        }
        if (TagIs(token, start, "frame"))
        {
            InsertElementFromToken(token);
            PopCurrentNode();
            return;
        }
        if (TagIs(token, start, "noframes"))
        {
            HandleInHead(token);
        }
    }

    void HandleAfterFrameset(Token token)
    {
        switch (token.Type)
        {
            case TokenType.Whitespace:
                InsertTextToken(token);
                return;
            case TokenType.Comment:
                AppendComment(CurrentNode!, token);
                return;
            case TokenType.Doctype:
                return;
        }
        if (TagIs(token, start, "html"))
        {
            HandleInBody(token);
            return;
        }
        if (TagIs(token, end, "html"))
        {
            insertionMode = InsertionMode.AfterAfterFrameset;
            return;
        }
        if (TagIs(token, start, "noframes"))
        {
            HandleInHead(token);
        }
    }

    void HandleAfterAfterBody(Token token)
    {
        if (token.Type == TokenType.Comment)
        {
            AppendComment(document, token);
            return;
        }
        if (token.Type is TokenType.Doctype or TokenType.Whitespace || TagIs(token, start, "html"))
        {
            HandleInBody(token);
            return;
        }
        if (token.Type == TokenType.Eof)
        {
            return;
        }
        insertionMode = InsertionMode.InBody;
        reprocessCurrentToken = true;
    }

    void HandleAfterAfterFrameset(Token token)
    {
        if (token.Type == TokenType.Comment)
        {
            AppendComment(document, token);
            return;
        }
        if (token.Type is TokenType.Doctype or TokenType.Whitespace || TagIs(token, start, "html"))
        {
            HandleInBody(token);
            return;
        }
        if (TagIs(token, start, "noframes"))
        {
            HandleInHead(token);
        }
    }

    void HandleHtmlContent(Token token)
    {
        switch (insertionMode)
        {
            case InsertionMode.Initial: HandleInitial(token); break;
            case InsertionMode.BeforeHtml: HandleBeforeHtml(token); break;
            case InsertionMode.BeforeHead: HandleBeforeHead(token); break;
            case InsertionMode.InHead: HandleInHead(token); break;
            case InsertionMode.InHeadNoscript: HandleInHeadNoscript(token); break;
            case InsertionMode.AfterHead: HandleAfterHead(token); break;
            case InsertionMode.InBody: HandleInBody(token); break;
            case InsertionMode.Text: HandleText(token); break;
            case InsertionMode.InTable: HandleInTable(token); break;
            case InsertionMode.InTableText: HandleInTableText(token); break;
            case InsertionMode.InCaption: HandleInCaption(token); break;
            case InsertionMode.InColumnGroup: HandleInColumnGroup(token); break;
            case InsertionMode.InTableBody: HandleInTableBody(token); break;
            case InsertionMode.InRow: HandleInRow(token); break;
            case InsertionMode.InCell: HandleInCell(token); break;
            case InsertionMode.InSelect: HandleInSelect(token); break;
            case InsertionMode.InSelectInTable: HandleInSelectInTable(token); break;
            case InsertionMode.InTemplate: HandleInTemplate(token); break;
            case InsertionMode.AfterBody: HandleAfterBody(token); break;
            case InsertionMode.InFrameset: HandleInFrameset(token); break;
            case InsertionMode.AfterFrameset: HandleAfterFrameset(token); break;
            case InsertionMode.AfterAfterBody: HandleAfterAfterBody(token); break;
            case InsertionMode.AfterAfterFrameset: HandleAfterAfterFrameset(token); break;
        }
    }

    static readonly TagSet BreakoutStartTags = TagSet.Html(
        "b", "big", "blockquote", "body", "br", "center", "code", "dd", "div", "dl", "dt", "em", "embed", "h1", "h2",
        "h3", "h4", "h5", "h6", "head", "hr", "i", "img", "li", "listing", "menu", "meta", "nobr", "ol", "p", "pre",
        "ruby", "s", "small", "span", "strong", "strike", "sub", "sup", "table", "tt", "u", "ul", "var");

    static readonly TagSet BrP = TagSet.Html("br", "p");

    void HandleInForeignContent(Token token)
    {
        switch (token.Type)
        {
            case TokenType.Null:
                token.Character = 0xFFFD;
                InsertTextToken(token);
                return;
            case TokenType.Whitespace:
                InsertTextToken(token);
                return;
            case TokenType.CData or TokenType.Character:
                InsertTextToken(token);
                framesetOk = false;
                return;
            case TokenType.Comment:
                AppendComment(CurrentNode!, token);
                return;
            case TokenType.Doctype:
                return;
        }

        if (TagIn(token, start, BreakoutStartTags)
            || (TagIs(token, start, "font")
                && (GetAttribute(token.Attributes, "color") is not null
                    || GetAttribute(token.Attributes, "face") is not null
                    || GetAttribute(token.Attributes, "size") is not null))
            || TagIn(token, end, BrP))
        {
            while (!(IsMathMlIntegrationPoint(CurrentNode!) || IsHtmlIntegrationPoint(CurrentNode!)
                || CurrentNode!.Namespace == HtmlNamespace.Html))
            {
                PopCurrentNode();
            }
            HandleHtmlContent(token);
            return;
        }

        if (token.Type == TokenType.StartTag)
        {
            var ns = AdjustedCurrentNode!.Namespace;
            if (ns == HtmlNamespace.MathMl)
            {
                AdjustMathMlAttributes(token);
            }
            if (ns == HtmlNamespace.Svg)
            {
                AdjustSvgTag(token);
                AdjustSvgAttributes(token);
            }
            AdjustForeignAttributes(token);
            InsertForeignElement(token, ns);
            if (token.IsSelfClosing)
            {
                PopCurrentNode();
            }
            return;
        }

        // An end tag: close up to a foreign element with its name, or leave it to the HTML rules
        var node = CurrentNode!;
        var i = openElements.Count - 1;
        while (i > 0)
        {
            if (node.Tag == token.TagName)
            {
                while (node != PopCurrentNode())
                {
                }
                return;
            }
            --i;
            node = openElements[i];
            if (node.Namespace == HtmlNamespace.Html)
            {
                break;
            }
        }
        if (i == 0)
        {
            return;
        }
        HandleHtmlContent(token);
    }

    static readonly TagSet MglyphMalignmark = TagSet.Html("mglyph", "malignmark");

    void HandleToken()
    {
        if (ignoreNextLinefeed && token.Type == TokenType.Whitespace && token.Character == '\n')
        {
            ignoreNextLinefeed = false;
            return;
        }
        ignoreNextLinefeed = false;

        var current = AdjustedCurrentNode;
        if (current is null
            || current.Namespace == HtmlNamespace.Html
            || (IsMathMlIntegrationPoint(current)
                && (token.Type is TokenType.Character or TokenType.Whitespace or TokenType.Null
                    || (token.Type == TokenType.StartTag && !TagIn(token, start, MglyphMalignmark))))
            || (current.Namespace == HtmlNamespace.MathMl && current.Tag == "annotation-xml" && TagIs(token, start, "svg"))
            || (IsHtmlIntegrationPoint(current)
                && token.Type is TokenType.StartTag or TokenType.Character or TokenType.Null or TokenType.Whitespace)
            || token.Type == TokenType.Eof)
        {
            HandleHtmlContent(token);
        }
        else
        {
            HandleInForeignContent(token);
        }
    }
}
