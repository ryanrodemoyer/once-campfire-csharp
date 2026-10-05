using System.Text;

namespace Campfire.RichText.Html.Gumbo;

// A port of Gumbo's tokenizer (gumbo-parser/src/tokenizer.c and utf8.c, nokogiri 1.19.4), the
// tokenizer Nokogiri::HTML5 uses. It follows the C state by state, quirks included: input is
// re-read from a mark to emit characters, attribute values take the raw input of a character
// reference that isn't one, and positions and parse errors are left out.

enum TokenType
{
    Doctype,
    StartTag,
    EndTag,
    Comment,
    Whitespace,
    Character,
    CData,
    Null,
    Eof,
}

/// <summary>GumboToken. The tokenizer fills in one token at a time; the tree builder reads it.</summary>
sealed class Token
{
    public TokenType Type { get; set; }

    /// <summary>For character, whitespace, null, CDATA and EOF tokens.</summary>
    public int Character { get; set; }

    /// <summary>A start or end tag's lowercase name, which Gumbo matches tags by.</summary>
    public string TagName { get; set; } = "";

    /// <summary>The element name when it differs from <see cref="TagName"/> (SVG's camelCase names).</summary>
    public string? ElementName { get; set; }

    public List<HtmlAttr> Attributes { get; set; } = [];

    public bool IsSelfClosing { get; set; }

    /// <summary>A comment's text.</summary>
    public string Text { get; set; } = "";

    public DoctypeToken Doctype { get; set; } = new();
}

sealed class DoctypeToken
{
    public string? Name { get; set; }

    public string? PublicIdentifier { get; set; }

    public string? SystemIdentifier { get; set; }

    public bool ForceQuirks { get; set; }

    public bool HasPublicIdentifier { get; set; }

    public bool HasSystemIdentifier { get; set; }
}

enum TokenizerState
{
    Data,
    RcData,
    RawText,
    ScriptData,
    PlainText,
    TagOpen,
    EndTagOpen,
    TagName,
    RcDataLt,
    RcDataEndTagOpen,
    RcDataEndTagName,
    RawTextLt,
    RawTextEndTagOpen,
    RawTextEndTagName,
    ScriptDataLt,
    ScriptDataEndTagOpen,
    ScriptDataEndTagName,
    ScriptDataEscapedStart,
    ScriptDataEscapedStartDash,
    ScriptDataEscaped,
    ScriptDataEscapedDash,
    ScriptDataEscapedDashDash,
    ScriptDataEscapedLt,
    ScriptDataEscapedEndTagOpen,
    ScriptDataEscapedEndTagName,
    ScriptDataDoubleEscapedStart,
    ScriptDataDoubleEscaped,
    ScriptDataDoubleEscapedDash,
    ScriptDataDoubleEscapedDashDash,
    ScriptDataDoubleEscapedLt,
    ScriptDataDoubleEscapedEnd,
    BeforeAttrName,
    AttrName,
    AfterAttrName,
    BeforeAttrValue,
    AttrValueDoubleQuoted,
    AttrValueSingleQuoted,
    AttrValueUnquoted,
    AfterAttrValueQuoted,
    SelfClosingStartTag,
    BogusComment,
    MarkupDeclarationOpen,
    CommentStart,
    CommentStartDash,
    Comment,
    CommentLt,
    CommentLtBang,
    CommentLtBangDash,
    CommentLtBangDashDash,
    CommentEndDash,
    CommentEnd,
    CommentEndBang,
    Doctype,
    BeforeDoctypeName,
    DoctypeName,
    AfterDoctypeName,
    AfterDoctypePublicKeyword,
    BeforeDoctypePublicId,
    DoctypePublicIdDoubleQuoted,
    DoctypePublicIdSingleQuoted,
    AfterDoctypePublicId,
    BetweenDoctypePublicSystemId,
    AfterDoctypeSystemKeyword,
    BeforeDoctypeSystemId,
    DoctypeSystemIdDoubleQuoted,
    DoctypeSystemIdSingleQuoted,
    AfterDoctypeSystemId,
    BogusDoctype,
    CDataSection,
    CDataSectionBracket,
    CDataSectionEnd,
    CharacterReference,
    NamedCharacterReference,
    AmbiguousAmpersand,
    NumericCharacterReference,
    HexadecimalCharacterReferenceStart,
    DecimalCharacterReferenceStart,
    HexadecimalCharacterReference,
    DecimalCharacterReference,
    NumericCharacterReferenceEnd,
}

sealed class Tokenizer
{
    const int noChar = -1;
    const int eof = -1;
    const int replacementChar = 0xFFFD;
    const int maxChar = 0x10FFFF;

    readonly string input;
    readonly int maxAttributes;

    // Utf8Iterator: the current code point starts at `start` and is `width` chars wide
    int start;
    int width;
    int current;
    int mark;

    TokenizerState state = TokenizerState.Data;
    bool reconsumeCurrentInput;
    bool isAdjustedCurrentNodeForeign;
    bool isInCData;
    int bufferedEmitChar = noChar;
    readonly StringBuilder temporaryBuffer = new();
    int resumePos = -1;
    TokenizerState returnState = TokenizerState.Data;
    int characterReferenceCode;

    // GumboTagState
    readonly StringBuilder tagBuffer = new();
    string tagName = "";
    List<HtmlAttr> attributes = [];
    bool dropNextAttrValue;
    string? lastStartTag;
    bool isStartTag;
    bool isSelfClosing;

    DoctypeToken doctype = new();

    public Tokenizer(string input, int maxAttributes)
    {
        this.input = input;
        this.maxAttributes = maxAttributes;
        ReadChar();
        // Gumbo drops a byte order mark only at the very start
        if (current == 0xFEFF)
        {
            start += width;
            ReadChar();
        }
    }

    /// <summary>Set once a tag had more attributes than allowed (GUMBO_STATUS_TOO_MANY_ATTRIBUTES).</summary>
    public bool TooManyAttributes { get; private set; }

    public void SetState(TokenizerState newState) => state = newState;

    /// <summary>Whether CDATA sections are allowed: the adjusted current node isn't HTML.</summary>
    public void SetAdjustedCurrentNodeForeign(bool foreign) => isAdjustedCurrentNodeForeign = foreign;

    // --- Utf8Iterator ---------------------------------------------------------------------------

    void ReadChar()
    {
        if (start >= input.Length)
        {
            current = eof;
            width = 0;
            return;
        }
        var c = input[start];
        width = 1;
        if (char.IsHighSurrogate(c) && start + 1 < input.Length && char.IsLowSurrogate(input[start + 1]))
        {
            current = char.ConvertToUtf32(c, input[start + 1]);
            width = 2;
            return;
        }
        if (char.IsSurrogate(c))
        {
            // Not valid UTF-8 in Ruby; Gumbo decodes invalid bytes as U+FFFD
            current = replacementChar;
            return;
        }
        if (c == '\r')
        {
            // A CR LF pair reads as the LF; a lone CR as LF
            if (start + 1 < input.Length && input[start + 1] == '\n')
            {
                start++;
            }
            current = '\n';
            return;
        }
        current = c;
    }

    void Next()
    {
        start += width;
        ReadChar();
    }

    bool MaybeConsumeMatch(string prefix, bool caseSensitive)
    {
        if (start + prefix.Length > input.Length
            || string.Compare(input, start, prefix, 0, prefix.Length, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) != 0)
        {
            return false;
        }
        for (var i = 0; i < prefix.Length; i++)
        {
            Next();
        }
        return true;
    }

    void SetMark() => mark = start;

    void ResetToMark()
    {
        start = mark;
        ReadChar();
    }

    // --- Emitting --------------------------------------------------------------------------------

    TokenType CharTokenType(int c)
    {
        if (isInCData && c > 0)
        {
            return TokenType.CData;
        }
        return c switch
        {
            '\t' or '\n' or '\r' or '\f' or ' ' => TokenType.Whitespace,
            0 => TokenType.Null,
            eof => TokenType.Eof,
            _ => TokenType.Character,
        };
    }

    void FinishToken()
    {
        if (!reconsumeCurrentInput)
        {
            Next();
        }
    }

    bool EmitChar(int c, Token output)
    {
        output.Type = CharTokenType(c);
        output.Character = c;
        FinishToken();
        return true;
    }

    bool EmitReplacementChar(Token output) => EmitChar(replacementChar, output);

    bool EmitEof(Token output) => EmitChar(eof, output);

    bool EmitDoctype(Token output)
    {
        output.Type = TokenType.Doctype;
        output.Doctype = doctype;
        FinishToken();
        doctype = new DoctypeToken();
        return true;
    }

    bool EmitCurrentTag(Token output)
    {
        if (isStartTag)
        {
            output.Type = TokenType.StartTag;
            output.TagName = tagName;
            output.ElementName = null;
            output.Attributes = attributes;
            output.IsSelfClosing = isSelfClosing;
            lastStartTag = tagName;
        }
        else
        {
            output.Type = TokenType.EndTag;
            output.TagName = tagName;
            output.Attributes = [];
            output.IsSelfClosing = false;
        }
        attributes = [];
        tagBuffer.Clear();
        FinishToken();
        return true;
    }

    void AbandonCurrentTag()
    {
        attributes = [];
        tagBuffer.Clear();
    }

    bool EmitComment(Token output)
    {
        output.Type = TokenType.Comment;
        output.Text = temporaryBuffer.ToString();
        temporaryBuffer.Clear();
        FinishToken();
        return true;
    }

    bool MaybeEmitFromMark(Token output)
    {
        if (resumePos < 0)
        {
            return false;
        }
        if (start >= resumePos)
        {
            resumePos = -1;
            return false;
        }
        return EmitChar(current, output);
    }

    /// <summary>Re-reads the input from the mark and emits it up to the current code point.</summary>
    bool EmitFromMark(Token output)
    {
        resumePos = start;
        ResetToMark();
        reconsumeCurrentInput = false;
        return MaybeEmitFromMark(output);
    }

    static void AppendCodePoint(StringBuilder buffer, int c)
    {
        if (c >= 0x10000)
        {
            buffer.Append(char.ConvertFromUtf32(c));
        }
        else
        {
            buffer.Append((char)c);
        }
    }

    bool CharacterReferencePartOfAttribute() => returnState is TokenizerState.AttrValueDoubleQuoted
        or TokenizerState.AttrValueSingleQuoted or TokenizerState.AttrValueUnquoted;

    // The raw input from the mark, ampersand included, goes into the attribute value as is
    bool FlushCodePointsConsumedAsCharacterReference(Token output)
    {
        if (CharacterReferencePartOfAttribute())
        {
            tagBuffer.Append(input, mark, start - mark);
            return false;
        }
        return EmitFromMark(output);
    }

    bool FlushCharRef(int first, int second, Token output)
    {
        if (CharacterReferencePartOfAttribute())
        {
            AppendCodePoint(tagBuffer, first);
            if (second != noChar)
            {
                AppendCodePoint(tagBuffer, second);
            }
            return false;
        }
        bufferedEmitChar = second;
        return EmitChar(first, output);
    }

    void StartNewTag(bool startTag)
    {
        tagBuffer.Clear();
        attributes = [];
        dropNextAttrValue = false;
        isStartTag = startTag;
        isSelfClosing = false;
    }

    void FinishTagName()
    {
        tagName = tagBuffer.ToString();
        tagBuffer.Clear();
    }

    void FinishAttributeName()
    {
        if (maxAttributes >= 0 && attributes.Count >= maxAttributes)
        {
            TooManyAttributes = true;
            tagBuffer.Clear();
            dropNextAttrValue = true;
            return;
        }

        // May've been set by a previous attribute without a value
        dropNextAttrValue = false;
        var name = tagBuffer.ToString();
        foreach (var attribute in attributes)
        {
            if (attribute.Name == name)
            {
                tagBuffer.Clear();
                dropNextAttrValue = true;
                return;
            }
        }
        attributes.Add(new HtmlAttr(name, ""));
        tagBuffer.Clear();
    }

    void FinishAttributeValue()
    {
        if (dropNextAttrValue)
        {
            dropNextAttrValue = false;
            tagBuffer.Clear();
            return;
        }
        attributes[^1].Value = tagBuffer.ToString();
        tagBuffer.Clear();
    }

    // _last_start_tag == gumbo_tagn_enum(buffer): two unknown tags are the same GUMBO_TAG_UNKNOWN
    bool IsAppropriateEndTag()
    {
        if (lastStartTag is null)
        {
            return false;
        }
        var name = tagBuffer.ToString();
        return name == lastStartTag || (!Tags.Known.Contains(name) && !Tags.Known.Contains(lastStartTag));
    }

    void ReconsumeIn(TokenizerState newState)
    {
        reconsumeCurrentInput = true;
        state = newState;
    }

    static bool IsAlpha(int c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    static bool IsDigit(int c) => c is >= '0' and <= '9';

    static bool IsAlnum(int c) => IsAlpha(c) || IsDigit(c);

    static bool IsHexDigit(int c) => IsDigit(c) || c is >= 'a' and <= 'f' or >= 'A' and <= 'F';

    static int ToLower(int c) => c is >= 'A' and <= 'Z' ? c + 0x20 : c;

    static bool IsSpace(int c) => c is '\t' or '\n' or '\f' or ' ';

    // --- Lexing ----------------------------------------------------------------------------------

    /// <summary>gumbo_lex: fills in the next token.</summary>
    public void Lex(Token output)
    {
        if (bufferedEmitChar != noChar)
        {
            reconsumeCurrentInput = true;
            EmitChar(bufferedEmitChar, output);
            reconsumeCurrentInput = false;
            bufferedEmitChar = noChar;
            return;
        }

        if (MaybeEmitFromMark(output))
        {
            return;
        }

        while (true)
        {
            var emitted = Dispatch(current, output);
            var shouldAdvance = !reconsumeCurrentInput;
            reconsumeCurrentInput = false;
            if (emitted)
            {
                return;
            }
            if (shouldAdvance)
            {
                Next();
            }
        }
    }

    bool Dispatch(int c, Token output) => state switch
    {
        TokenizerState.Data => DataState(c, output),
        TokenizerState.RcData => RcDataState(c, output),
        TokenizerState.RawText => RawTextState(c, output),
        TokenizerState.ScriptData => ScriptDataState(c, output),
        TokenizerState.PlainText => PlainTextState(c, output),
        TokenizerState.TagOpen => TagOpenState(c, output),
        TokenizerState.EndTagOpen => EndTagOpenState(c, output),
        TokenizerState.TagName => TagNameState(c, output),
        TokenizerState.RcDataLt => LessThanSignState(c, output, TokenizerState.RcDataEndTagOpen, TokenizerState.RcData),
        TokenizerState.RcDataEndTagOpen => EndTagOpenInTextState(c, output, TokenizerState.RcDataEndTagName, TokenizerState.RcData),
        TokenizerState.RcDataEndTagName => EndTagNameInTextState(c, output, TokenizerState.RcData),
        TokenizerState.RawTextLt => LessThanSignState(c, output, TokenizerState.RawTextEndTagOpen, TokenizerState.RawText),
        TokenizerState.RawTextEndTagOpen => EndTagOpenInTextState(c, output, TokenizerState.RawTextEndTagName, TokenizerState.RawText),
        TokenizerState.RawTextEndTagName => EndTagNameInTextState(c, output, TokenizerState.RawText),
        TokenizerState.ScriptDataLt => ScriptDataLtState(c, output),
        TokenizerState.ScriptDataEndTagOpen => EndTagOpenInTextState(c, output, TokenizerState.ScriptDataEndTagName, TokenizerState.ScriptData),
        TokenizerState.ScriptDataEndTagName => EndTagNameInTextState(c, output, TokenizerState.ScriptData),
        TokenizerState.ScriptDataEscapedStart => ScriptDataEscapedStartState(c, output, TokenizerState.ScriptDataEscapedStartDash),
        TokenizerState.ScriptDataEscapedStartDash => ScriptDataEscapedStartState(c, output, TokenizerState.ScriptDataEscapedDashDash),
        TokenizerState.ScriptDataEscaped => ScriptDataEscapedState(c, output),
        TokenizerState.ScriptDataEscapedDash => ScriptDataEscapedDashState(c, output),
        TokenizerState.ScriptDataEscapedDashDash => ScriptDataEscapedDashDashState(c, output),
        TokenizerState.ScriptDataEscapedLt => ScriptDataEscapedLtState(c, output),
        TokenizerState.ScriptDataEscapedEndTagOpen => EndTagOpenInTextState(c, output, TokenizerState.ScriptDataEscapedEndTagName, TokenizerState.ScriptDataEscaped),
        TokenizerState.ScriptDataEscapedEndTagName => EndTagNameInTextState(c, output, TokenizerState.ScriptDataEscaped),
        TokenizerState.ScriptDataDoubleEscapedStart => ScriptDataDoubleEscapeBoundaryState(c, output, TokenizerState.ScriptDataDoubleEscaped, TokenizerState.ScriptDataEscaped),
        TokenizerState.ScriptDataDoubleEscaped => ScriptDataDoubleEscapedState(c, output),
        TokenizerState.ScriptDataDoubleEscapedDash => ScriptDataDoubleEscapedDashState(c, output),
        TokenizerState.ScriptDataDoubleEscapedDashDash => ScriptDataDoubleEscapedDashDashState(c, output),
        TokenizerState.ScriptDataDoubleEscapedLt => ScriptDataDoubleEscapedLtState(c, output),
        TokenizerState.ScriptDataDoubleEscapedEnd => ScriptDataDoubleEscapeBoundaryState(c, output, TokenizerState.ScriptDataEscaped, TokenizerState.ScriptDataDoubleEscaped),
        TokenizerState.BeforeAttrName => BeforeAttrNameState(c),
        TokenizerState.AttrName => AttrNameState(c),
        TokenizerState.AfterAttrName => AfterAttrNameState(c, output),
        TokenizerState.BeforeAttrValue => BeforeAttrValueState(c, output),
        TokenizerState.AttrValueDoubleQuoted => AttrValueQuotedState(c, output, '"', TokenizerState.AttrValueDoubleQuoted),
        TokenizerState.AttrValueSingleQuoted => AttrValueQuotedState(c, output, '\'', TokenizerState.AttrValueSingleQuoted),
        TokenizerState.AttrValueUnquoted => AttrValueUnquotedState(c, output),
        TokenizerState.AfterAttrValueQuoted => AfterAttrValueQuotedState(c, output),
        TokenizerState.SelfClosingStartTag => SelfClosingStartTagState(c, output),
        TokenizerState.BogusComment => BogusCommentState(c, output),
        TokenizerState.MarkupDeclarationOpen => MarkupDeclarationOpenState(),
        TokenizerState.CommentStart => CommentStartState(c, output),
        TokenizerState.CommentStartDash => CommentStartDashState(c, output),
        TokenizerState.Comment => CommentState(c, output),
        TokenizerState.CommentLt => CommentLtState(c),
        TokenizerState.CommentLtBang => CommentLtBangState(c),
        TokenizerState.CommentLtBangDash => CommentLtBangDashState(c),
        TokenizerState.CommentLtBangDashDash => CommentLtBangDashDashState(),
        TokenizerState.CommentEndDash => CommentEndDashState(c, output),
        TokenizerState.CommentEnd => CommentEndState(c, output),
        TokenizerState.CommentEndBang => CommentEndBangState(c, output),
        TokenizerState.Doctype => DoctypeState(c, output),
        TokenizerState.BeforeDoctypeName => BeforeDoctypeNameState(c, output),
        TokenizerState.DoctypeName => DoctypeNameState(c, output),
        TokenizerState.AfterDoctypeName => AfterDoctypeNameState(c, output),
        TokenizerState.AfterDoctypePublicKeyword => AfterDoctypeKeywordState(c, output, TokenizerState.BeforeDoctypePublicId, TokenizerState.DoctypePublicIdDoubleQuoted, TokenizerState.DoctypePublicIdSingleQuoted),
        TokenizerState.BeforeDoctypePublicId => BeforeDoctypeIdState(c, output, TokenizerState.DoctypePublicIdDoubleQuoted, TokenizerState.DoctypePublicIdSingleQuoted),
        TokenizerState.DoctypePublicIdDoubleQuoted => DoctypeIdQuotedState(c, output, '"', isPublic: true),
        TokenizerState.DoctypePublicIdSingleQuoted => DoctypeIdQuotedState(c, output, '\'', isPublic: true),
        TokenizerState.AfterDoctypePublicId => AfterDoctypePublicIdState(c, output),
        TokenizerState.BetweenDoctypePublicSystemId => BetweenDoctypePublicSystemIdState(c, output),
        TokenizerState.AfterDoctypeSystemKeyword => AfterDoctypeKeywordState(c, output, TokenizerState.BeforeDoctypeSystemId, TokenizerState.DoctypeSystemIdDoubleQuoted, TokenizerState.DoctypeSystemIdSingleQuoted),
        TokenizerState.BeforeDoctypeSystemId => BeforeDoctypeIdState(c, output, TokenizerState.DoctypeSystemIdDoubleQuoted, TokenizerState.DoctypeSystemIdSingleQuoted),
        TokenizerState.DoctypeSystemIdDoubleQuoted => DoctypeIdQuotedState(c, output, '"', isPublic: false),
        TokenizerState.DoctypeSystemIdSingleQuoted => DoctypeIdQuotedState(c, output, '\'', isPublic: false),
        TokenizerState.AfterDoctypeSystemId => AfterDoctypeSystemIdState(c, output),
        TokenizerState.BogusDoctype => BogusDoctypeState(c, output),
        TokenizerState.CDataSection => CDataSectionState(c, output),
        TokenizerState.CDataSectionBracket => CDataSectionBracketState(c, output),
        TokenizerState.CDataSectionEnd => CDataSectionEndState(c, output),
        TokenizerState.CharacterReference => CharacterReferenceState(c, output),
        TokenizerState.NamedCharacterReference => NamedCharacterReferenceState(output),
        TokenizerState.AmbiguousAmpersand => AmbiguousAmpersandState(c, output),
        TokenizerState.NumericCharacterReference => NumericCharacterReferenceState(c),
        TokenizerState.HexadecimalCharacterReferenceStart => NumericCharacterReferenceStartState(output, IsHexDigit(c), TokenizerState.HexadecimalCharacterReference),
        TokenizerState.DecimalCharacterReferenceStart => NumericCharacterReferenceStartState(output, IsDigit(c), TokenizerState.DecimalCharacterReference),
        TokenizerState.HexadecimalCharacterReference => HexadecimalCharacterReferenceState(c),
        TokenizerState.DecimalCharacterReference => DecimalCharacterReferenceState(c),
        TokenizerState.NumericCharacterReferenceEnd => NumericCharacterReferenceEndState(output),
        _ => throw new InvalidOperationException($"Unknown tokenizer state {state}"),
    };

    bool DataState(int c, Token output)
    {
        switch (c)
        {
            case '&':
                state = TokenizerState.CharacterReference;
                SetMark();
                returnState = TokenizerState.Data;
                return false;
            case '<':
                state = TokenizerState.TagOpen;
                SetMark();
                return false;
            case eof:
                return EmitEof(output);
            default:
                // A NUL is emitted as is, as a null token
                return EmitChar(c, output);
        }
    }

    bool RcDataState(int c, Token output)
    {
        switch (c)
        {
            case '&':
                state = TokenizerState.CharacterReference;
                SetMark();
                returnState = TokenizerState.RcData;
                return false;
            case '<':
                state = TokenizerState.RcDataLt;
                SetMark();
                return false;
            case 0:
                return EmitReplacementChar(output);
            case eof:
                return EmitEof(output);
            default:
                return EmitChar(c, output);
        }
    }

    bool RawTextState(int c, Token output)
    {
        switch (c)
        {
            case '<':
                state = TokenizerState.RawTextLt;
                SetMark();
                return false;
            case 0:
                return EmitReplacementChar(output);
            case eof:
                return EmitEof(output);
            default:
                return EmitChar(c, output);
        }
    }

    bool ScriptDataState(int c, Token output)
    {
        switch (c)
        {
            case '<':
                state = TokenizerState.ScriptDataLt;
                SetMark();
                return false;
            case 0:
                return EmitReplacementChar(output);
            case eof:
                return EmitEof(output);
            default:
                return EmitChar(c, output);
        }
    }

    bool PlainTextState(int c, Token output) => c switch
    {
        0 => EmitReplacementChar(output),
        eof => EmitEof(output),
        _ => EmitChar(c, output),
    };

    bool TagOpenState(int c, Token output)
    {
        switch (c)
        {
            case '!':
                state = TokenizerState.MarkupDeclarationOpen;
                temporaryBuffer.Clear();
                return false;
            case '/':
                state = TokenizerState.EndTagOpen;
                return false;
            case '?':
                temporaryBuffer.Clear();
                ReconsumeIn(TokenizerState.BogusComment);
                return false;
            case eof:
                ReconsumeIn(TokenizerState.Data);
                return EmitFromMark(output);
            default:
                if (IsAlpha(c))
                {
                    ReconsumeIn(TokenizerState.TagName);
                    StartNewTag(startTag: true);
                    return false;
                }
                ReconsumeIn(TokenizerState.Data);
                return EmitFromMark(output);
        }
    }

    bool EndTagOpenState(int c, Token output)
    {
        switch (c)
        {
            case '>':
                state = TokenizerState.Data;
                return false;
            case eof:
                ReconsumeIn(TokenizerState.Data);
                return EmitFromMark(output);
            default:
                if (IsAlpha(c))
                {
                    ReconsumeIn(TokenizerState.TagName);
                    StartNewTag(startTag: false);
                }
                else
                {
                    ReconsumeIn(TokenizerState.BogusComment);
                    temporaryBuffer.Clear();
                }
                return false;
        }
    }

    bool TagNameState(int c, Token output)
    {
        switch (c)
        {
            case '\t' or '\n' or '\f' or ' ':
                FinishTagName();
                state = TokenizerState.BeforeAttrName;
                return false;
            case '/':
                FinishTagName();
                state = TokenizerState.SelfClosingStartTag;
                return false;
            case '>':
                FinishTagName();
                state = TokenizerState.Data;
                return EmitCurrentTag(output);
            case 0:
                AppendCodePoint(tagBuffer, replacementChar);
                return false;
            case eof:
                AbandonCurrentTag();
                return EmitEof(output);
            default:
                AppendCodePoint(tagBuffer, ToLower(c));
                return false;
        }
    }

    // RCDATA and RAWTEXT less-than sign states
    bool LessThanSignState(int c, Token output, TokenizerState endTagOpen, TokenizerState text)
    {
        if (c == '/')
        {
            state = endTagOpen;
            return false;
        }
        ReconsumeIn(text);
        return EmitFromMark(output);
    }

    // RCDATA, RAWTEXT, script data and script data escaped end tag open states
    bool EndTagOpenInTextState(int c, Token output, TokenizerState endTagName, TokenizerState text)
    {
        if (IsAlpha(c))
        {
            ReconsumeIn(endTagName);
            StartNewTag(startTag: false);
            return false;
        }
        ReconsumeIn(text);
        return EmitFromMark(output);
    }

    // RCDATA, RAWTEXT, script data and script data escaped end tag name states
    bool EndTagNameInTextState(int c, Token output, TokenizerState text)
    {
        if (IsAlpha(c))
        {
            AppendCodePoint(tagBuffer, ToLower(c));
            return false;
        }
        switch (c)
        {
            case '\t' or '\n' or '\f' or ' ':
                if (IsAppropriateEndTag())
                {
                    FinishTagName();
                    state = TokenizerState.BeforeAttrName;
                    return false;
                }
                break;
            case '/':
                if (IsAppropriateEndTag())
                {
                    FinishTagName();
                    state = TokenizerState.SelfClosingStartTag;
                    return false;
                }
                break;
            case '>':
                if (IsAppropriateEndTag())
                {
                    FinishTagName();
                    state = TokenizerState.Data;
                    return EmitCurrentTag(output);
                }
                break;
        }
        AbandonCurrentTag();
        ReconsumeIn(text);
        return EmitFromMark(output);
    }

    bool ScriptDataLtState(int c, Token output)
    {
        if (c == '/')
        {
            state = TokenizerState.ScriptDataEndTagOpen;
            return false;
        }
        if (c == '!')
        {
            // The only place the input advances before emitting from the mark
            Next();
            ReconsumeIn(TokenizerState.ScriptDataEscapedStart);
            return EmitFromMark(output);
        }
        ReconsumeIn(TokenizerState.ScriptData);
        return EmitFromMark(output);
    }

    // Script data escape start and escape start dash states
    bool ScriptDataEscapedStartState(int c, Token output, TokenizerState onDash)
    {
        if (c == '-')
        {
            state = onDash;
            return EmitChar(c, output);
        }
        ReconsumeIn(TokenizerState.ScriptData);
        return false;
    }

    bool ScriptDataEscapedState(int c, Token output)
    {
        switch (c)
        {
            case '-':
                state = TokenizerState.ScriptDataEscapedDash;
                return EmitChar(c, output);
            case '<':
                state = TokenizerState.ScriptDataEscapedLt;
                temporaryBuffer.Clear();
                SetMark();
                return false;
            case 0:
                return EmitReplacementChar(output);
            case eof:
                return EmitEof(output);
            default:
                return EmitChar(c, output);
        }
    }

    bool ScriptDataEscapedDashState(int c, Token output)
    {
        switch (c)
        {
            case '-':
                state = TokenizerState.ScriptDataEscapedDashDash;
                return EmitChar(c, output);
            case '<':
                state = TokenizerState.ScriptDataEscapedLt;
                temporaryBuffer.Clear();
                SetMark();
                return false;
            case 0:
                state = TokenizerState.ScriptDataEscaped;
                return EmitReplacementChar(output);
            case eof:
                return EmitEof(output);
            default:
                state = TokenizerState.ScriptDataEscaped;
                return EmitChar(c, output);
        }
    }

    bool ScriptDataEscapedDashDashState(int c, Token output)
    {
        switch (c)
        {
            case '-':
                return EmitChar(c, output);
            case '<':
                state = TokenizerState.ScriptDataEscapedLt;
                temporaryBuffer.Clear();
                SetMark();
                return false;
            case '>':
                state = TokenizerState.ScriptData;
                return EmitChar(c, output);
            case 0:
                state = TokenizerState.ScriptDataEscaped;
                return EmitReplacementChar(output);
            case eof:
                return EmitEof(output);
            default:
                state = TokenizerState.ScriptDataEscaped;
                return EmitChar(c, output);
        }
    }

    bool ScriptDataEscapedLtState(int c, Token output)
    {
        if (c == '/')
        {
            state = TokenizerState.ScriptDataEscapedEndTagOpen;
            return false;
        }
        if (IsAlpha(c))
        {
            ReconsumeIn(TokenizerState.ScriptDataDoubleEscapedStart);
            return EmitFromMark(output);
        }
        ReconsumeIn(TokenizerState.ScriptDataEscaped);
        return EmitFromMark(output);
    }

    // Script data double escape start and end states: "script" switches between the two
    bool ScriptDataDoubleEscapeBoundaryState(int c, Token output, TokenizerState onScript, TokenizerState otherwise)
    {
        switch (c)
        {
            case '\t' or '\n' or '\f' or ' ' or '/' or '>':
                state = temporaryBuffer.Equals("script".AsSpan()) ? onScript : otherwise;
                return EmitChar(c, output);
        }
        if (IsAlpha(c))
        {
            AppendCodePoint(temporaryBuffer, ToLower(c));
            return EmitChar(c, output);
        }
        ReconsumeIn(state == TokenizerState.ScriptDataDoubleEscapedStart ? TokenizerState.ScriptDataEscaped : TokenizerState.ScriptDataDoubleEscaped);
        return false;
    }

    bool ScriptDataDoubleEscapedState(int c, Token output)
    {
        switch (c)
        {
            case '-':
                state = TokenizerState.ScriptDataDoubleEscapedDash;
                return EmitChar(c, output);
            case '<':
                state = TokenizerState.ScriptDataDoubleEscapedLt;
                return EmitChar(c, output);
            case 0:
                return EmitReplacementChar(output);
            case eof:
                return EmitEof(output);
            default:
                return EmitChar(c, output);
        }
    }

    bool ScriptDataDoubleEscapedDashState(int c, Token output)
    {
        switch (c)
        {
            case '-':
                state = TokenizerState.ScriptDataDoubleEscapedDashDash;
                return EmitChar(c, output);
            case '<':
                state = TokenizerState.ScriptDataDoubleEscapedLt;
                return EmitChar(c, output);
            case 0:
                state = TokenizerState.ScriptDataDoubleEscaped;
                return EmitReplacementChar(output);
            case eof:
                return EmitEof(output);
            default:
                state = TokenizerState.ScriptDataDoubleEscaped;
                return EmitChar(c, output);
        }
    }

    bool ScriptDataDoubleEscapedDashDashState(int c, Token output)
    {
        switch (c)
        {
            case '-':
                return EmitChar(c, output);
            case '<':
                state = TokenizerState.ScriptDataDoubleEscapedLt;
                return EmitChar(c, output);
            case '>':
                state = TokenizerState.ScriptData;
                return EmitChar(c, output);
            case 0:
                state = TokenizerState.ScriptDataDoubleEscaped;
                return EmitReplacementChar(output);
            case eof:
                return EmitEof(output);
            default:
                state = TokenizerState.ScriptDataDoubleEscaped;
                return EmitChar(c, output);
        }
    }

    bool ScriptDataDoubleEscapedLtState(int c, Token output)
    {
        if (c == '/')
        {
            state = TokenizerState.ScriptDataDoubleEscapedEnd;
            temporaryBuffer.Clear();
            return EmitChar(c, output);
        }
        ReconsumeIn(TokenizerState.ScriptDataDoubleEscaped);
        return false;
    }

    bool BeforeAttrNameState(int c)
    {
        switch (c)
        {
            case '\t' or '\n' or '\f' or ' ':
                return false;
            case '/' or '>' or eof:
                ReconsumeIn(TokenizerState.AfterAttrName);
                return false;
            case '=':
                state = TokenizerState.AttrName;
                AppendCodePoint(tagBuffer, c);
                return false;
            default:
                ReconsumeIn(TokenizerState.AttrName);
                return false;
        }
    }

    bool AttrNameState(int c)
    {
        switch (c)
        {
            case '\t' or '\n' or '\f' or ' ' or '/' or '>' or eof:
                FinishAttributeName();
                ReconsumeIn(TokenizerState.AfterAttrName);
                return false;
            case '=':
                FinishAttributeName();
                state = TokenizerState.BeforeAttrValue;
                return false;
            case 0:
                AppendCodePoint(tagBuffer, replacementChar);
                return false;
            default:
                AppendCodePoint(tagBuffer, ToLower(c));
                return false;
        }
    }

    bool AfterAttrNameState(int c, Token output)
    {
        switch (c)
        {
            case '\t' or '\n' or '\f' or ' ':
                return false;
            case '/':
                state = TokenizerState.SelfClosingStartTag;
                return false;
            case '=':
                state = TokenizerState.BeforeAttrValue;
                return false;
            case '>':
                state = TokenizerState.Data;
                return EmitCurrentTag(output);
            case eof:
                AbandonCurrentTag();
                return EmitEof(output);
            default:
                ReconsumeIn(TokenizerState.AttrName);
                return false;
        }
    }

    bool BeforeAttrValueState(int c, Token output)
    {
        switch (c)
        {
            case '\t' or '\n' or '\f' or ' ':
                return false;
            case '"':
                state = TokenizerState.AttrValueDoubleQuoted;
                return false;
            case '\'':
                state = TokenizerState.AttrValueSingleQuoted;
                return false;
            case '>':
                state = TokenizerState.Data;
                return EmitCurrentTag(output);
            default:
                ReconsumeIn(TokenizerState.AttrValueUnquoted);
                return false;
        }
    }

    bool AttrValueQuotedState(int c, Token output, char quote, TokenizerState self)
    {
        if (c == quote)
        {
            state = TokenizerState.AfterAttrValueQuoted;
            return false;
        }
        switch (c)
        {
            case '&':
                state = TokenizerState.CharacterReference;
                SetMark();
                returnState = self;
                return false;
            case 0:
                AppendCodePoint(tagBuffer, replacementChar);
                return false;
            case eof:
                AbandonCurrentTag();
                return EmitEof(output);
            default:
                AppendCodePoint(tagBuffer, c);
                return false;
        }
    }

    bool AttrValueUnquotedState(int c, Token output)
    {
        switch (c)
        {
            case '\t' or '\n' or '\f' or ' ':
                state = TokenizerState.BeforeAttrName;
                FinishAttributeValue();
                return false;
            case '&':
                state = TokenizerState.CharacterReference;
                SetMark();
                returnState = TokenizerState.AttrValueUnquoted;
                return false;
            case '>':
                state = TokenizerState.Data;
                FinishAttributeValue();
                return EmitCurrentTag(output);
            case 0:
                AppendCodePoint(tagBuffer, replacementChar);
                return false;
            case eof:
                AbandonCurrentTag();
                return EmitEof(output);
            default:
                AppendCodePoint(tagBuffer, c);
                return false;
        }
    }

    bool AfterAttrValueQuotedState(int c, Token output)
    {
        FinishAttributeValue();
        switch (c)
        {
            case '\t' or '\n' or '\f' or ' ':
                state = TokenizerState.BeforeAttrName;
                return false;
            case '/':
                state = TokenizerState.SelfClosingStartTag;
                return false;
            case '>':
                state = TokenizerState.Data;
                return EmitCurrentTag(output);
            case eof:
                AbandonCurrentTag();
                return EmitEof(output);
            default:
                ReconsumeIn(TokenizerState.BeforeAttrName);
                return false;
        }
    }

    bool SelfClosingStartTagState(int c, Token output)
    {
        switch (c)
        {
            case '>':
                state = TokenizerState.Data;
                isSelfClosing = true;
                return EmitCurrentTag(output);
            case eof:
                AbandonCurrentTag();
                return EmitEof(output);
            default:
                ReconsumeIn(TokenizerState.BeforeAttrName);
                return false;
        }
    }

    bool BogusCommentState(int c, Token output)
    {
        switch (c)
        {
            case '>':
                state = TokenizerState.Data;
                return EmitComment(output);
            case eof:
                ReconsumeIn(TokenizerState.Data);
                return EmitComment(output);
            case 0:
                AppendCodePoint(temporaryBuffer, replacementChar);
                return false;
            default:
                AppendCodePoint(temporaryBuffer, c);
                return false;
        }
    }

    bool MarkupDeclarationOpenState()
    {
        if (MaybeConsumeMatch("--", caseSensitive: true))
        {
            ReconsumeIn(TokenizerState.CommentStart);
            return false;
        }
        if (MaybeConsumeMatch("DOCTYPE", caseSensitive: false))
        {
            ReconsumeIn(TokenizerState.Doctype);
            doctype.Name = "";
            doctype.PublicIdentifier = "";
            doctype.SystemIdentifier = "";
            return false;
        }
        if (MaybeConsumeMatch("[CDATA[", caseSensitive: true))
        {
            if (isAdjustedCurrentNodeForeign)
            {
                ReconsumeIn(TokenizerState.CDataSection);
                isInCData = true;
            }
            else
            {
                temporaryBuffer.Clear().Append("[CDATA[");
                ReconsumeIn(TokenizerState.BogusComment);
            }
            return false;
        }
        ReconsumeIn(TokenizerState.BogusComment);
        temporaryBuffer.Clear();
        return false;
    }

    bool CommentStartState(int c, Token output)
    {
        switch (c)
        {
            case '-':
                state = TokenizerState.CommentStartDash;
                return false;
            case '>':
                state = TokenizerState.Data;
                return EmitComment(output);
            default:
                ReconsumeIn(TokenizerState.Comment);
                return false;
        }
    }

    bool CommentStartDashState(int c, Token output)
    {
        switch (c)
        {
            case '-':
                state = TokenizerState.CommentEnd;
                return false;
            case '>':
                state = TokenizerState.Data;
                return EmitComment(output);
            case eof:
                ReconsumeIn(TokenizerState.Data);
                return EmitComment(output);
            default:
                ReconsumeIn(TokenizerState.Comment);
                temporaryBuffer.Append('-');
                return false;
        }
    }

    bool CommentState(int c, Token output)
    {
        switch (c)
        {
            case '<':
                state = TokenizerState.CommentLt;
                temporaryBuffer.Append('<');
                return false;
            case '-':
                state = TokenizerState.CommentEndDash;
                return false;
            case 0:
                AppendCodePoint(temporaryBuffer, replacementChar);
                return false;
            case eof:
                ReconsumeIn(TokenizerState.Data);
                return EmitComment(output);
            default:
                AppendCodePoint(temporaryBuffer, c);
                return false;
        }
    }

    bool CommentLtState(int c)
    {
        switch (c)
        {
            case '!':
                state = TokenizerState.CommentLtBang;
                temporaryBuffer.Append('!');
                return false;
            case '<':
                temporaryBuffer.Append('<');
                return false;
            default:
                ReconsumeIn(TokenizerState.Comment);
                return false;
        }
    }

    bool CommentLtBangState(int c)
    {
        if (c == '-')
        {
            state = TokenizerState.CommentLtBangDash;
            return false;
        }
        ReconsumeIn(TokenizerState.Comment);
        return false;
    }

    bool CommentLtBangDashState(int c)
    {
        if (c == '-')
        {
            state = TokenizerState.CommentLtBangDashDash;
            return false;
        }
        ReconsumeIn(TokenizerState.CommentEndDash);
        return false;
    }

    bool CommentLtBangDashDashState()
    {
        ReconsumeIn(TokenizerState.CommentEnd);
        return false;
    }

    bool CommentEndDashState(int c, Token output)
    {
        switch (c)
        {
            case '-':
                state = TokenizerState.CommentEnd;
                return false;
            case eof:
                state = TokenizerState.Data;
                return EmitComment(output);
            default:
                ReconsumeIn(TokenizerState.Comment);
                temporaryBuffer.Append('-');
                return false;
        }
    }

    bool CommentEndState(int c, Token output)
    {
        switch (c)
        {
            case '>':
                state = TokenizerState.Data;
                return EmitComment(output);
            case '!':
                state = TokenizerState.CommentEndBang;
                return false;
            case '-':
                temporaryBuffer.Append('-');
                return false;
            case eof:
                state = TokenizerState.Data;
                return EmitComment(output);
            default:
                ReconsumeIn(TokenizerState.Comment);
                temporaryBuffer.Append("--");
                return false;
        }
    }

    bool CommentEndBangState(int c, Token output)
    {
        switch (c)
        {
            case '-':
                state = TokenizerState.CommentEndDash;
                temporaryBuffer.Append("--!");
                return false;
            case '>':
                state = TokenizerState.Data;
                return EmitComment(output);
            case eof:
                state = TokenizerState.Data;
                return EmitComment(output);
            default:
                ReconsumeIn(TokenizerState.Comment);
                temporaryBuffer.Append("--!");
                return false;
        }
    }

    bool DoctypeState(int c, Token output)
    {
        switch (c)
        {
            case '\t' or '\n' or '\f' or ' ':
                state = TokenizerState.BeforeDoctypeName;
                return false;
            case '>':
                ReconsumeIn(TokenizerState.BeforeDoctypeName);
                return false;
            case eof:
                doctype.ForceQuirks = true;
                ReconsumeIn(TokenizerState.Data);
                return EmitDoctype(output);
            default:
                ReconsumeIn(TokenizerState.BeforeDoctypeName);
                return false;
        }
    }

    bool BeforeDoctypeNameState(int c, Token output)
    {
        switch (c)
        {
            case '\t' or '\n' or '\f' or ' ':
                return false;
            case 0:
                state = TokenizerState.DoctypeName;
                AppendCodePoint(temporaryBuffer, replacementChar);
                return false;
            case '>':
                state = TokenizerState.Data;
                doctype.ForceQuirks = true;
                return EmitDoctype(output);
            case eof:
                doctype.ForceQuirks = true;
                ReconsumeIn(TokenizerState.Data);
                return EmitDoctype(output);
            default:
                state = TokenizerState.DoctypeName;
                AppendCodePoint(temporaryBuffer, ToLower(c));
                return false;
        }
    }

    string FinishTemporaryBuffer()
    {
        var value = temporaryBuffer.ToString();
        temporaryBuffer.Clear();
        return value;
    }

    bool DoctypeNameState(int c, Token output)
    {
        switch (c)
        {
            case '\t' or '\n' or '\f' or ' ':
                state = TokenizerState.AfterDoctypeName;
                doctype.Name = FinishTemporaryBuffer();
                return false;
            case '>':
                state = TokenizerState.Data;
                doctype.Name = FinishTemporaryBuffer();
                return EmitDoctype(output);
            case 0:
                AppendCodePoint(temporaryBuffer, replacementChar);
                return false;
            case eof:
                ReconsumeIn(TokenizerState.Data);
                doctype.ForceQuirks = true;
                doctype.Name = FinishTemporaryBuffer();
                return EmitDoctype(output);
            default:
                AppendCodePoint(temporaryBuffer, ToLower(c));
                return false;
        }
    }

    bool AfterDoctypeNameState(int c, Token output)
    {
        switch (c)
        {
            case '\t' or '\n' or '\f' or ' ':
                return false;
            case '>':
                state = TokenizerState.Data;
                return EmitDoctype(output);
            case eof:
                state = TokenizerState.Data;
                doctype.ForceQuirks = true;
                return EmitDoctype(output);
            default:
                if (MaybeConsumeMatch("PUBLIC", caseSensitive: false))
                {
                    ReconsumeIn(TokenizerState.AfterDoctypePublicKeyword);
                }
                else if (MaybeConsumeMatch("SYSTEM", caseSensitive: false))
                {
                    ReconsumeIn(TokenizerState.AfterDoctypeSystemKeyword);
                }
                else
                {
                    ReconsumeIn(TokenizerState.BogusDoctype);
                    doctype.ForceQuirks = true;
                }
                return false;
        }
    }

    // After DOCTYPE public keyword and after DOCTYPE system keyword states
    bool AfterDoctypeKeywordState(int c, Token output, TokenizerState beforeId, TokenizerState doubleQuoted, TokenizerState singleQuoted)
    {
        switch (c)
        {
            case '\t' or '\n' or '\f' or ' ':
                state = beforeId;
                return false;
            case '"':
                state = doubleQuoted;
                return false;
            case '\'':
                state = singleQuoted;
                return false;
            case '>':
                state = TokenizerState.Data;
                doctype.ForceQuirks = true;
                return EmitDoctype(output);
            case eof:
                ReconsumeIn(TokenizerState.Data);
                doctype.ForceQuirks = true;
                return EmitDoctype(output);
            default:
                ReconsumeIn(TokenizerState.BogusDoctype);
                doctype.ForceQuirks = true;
                return false;
        }
    }

    // Before DOCTYPE public identifier and before DOCTYPE system identifier states
    bool BeforeDoctypeIdState(int c, Token output, TokenizerState doubleQuoted, TokenizerState singleQuoted)
    {
        switch (c)
        {
            case '\t' or '\n' or '\f' or ' ':
                return false;
            case '"':
                state = doubleQuoted;
                return false;
            case '\'':
                state = singleQuoted;
                return false;
            case '>':
                state = TokenizerState.Data;
                doctype.ForceQuirks = true;
                return EmitDoctype(output);
            case eof:
                ReconsumeIn(TokenizerState.Data);
                doctype.ForceQuirks = true;
                return EmitDoctype(output);
            default:
                ReconsumeIn(TokenizerState.BogusDoctype);
                doctype.ForceQuirks = true;
                return false;
        }
    }

    void FinishDoctypeId(bool isPublic)
    {
        if (isPublic)
        {
            doctype.PublicIdentifier = FinishTemporaryBuffer();
            doctype.HasPublicIdentifier = true;
        }
        else
        {
            doctype.SystemIdentifier = FinishTemporaryBuffer();
            doctype.HasSystemIdentifier = true;
        }
    }

    // DOCTYPE public and system identifier (double- and single-quoted) states
    bool DoctypeIdQuotedState(int c, Token output, char quote, bool isPublic)
    {
        if (c == quote)
        {
            state = isPublic ? TokenizerState.AfterDoctypePublicId : TokenizerState.AfterDoctypeSystemId;
            FinishDoctypeId(isPublic);
            return false;
        }
        switch (c)
        {
            case 0:
                AppendCodePoint(temporaryBuffer, replacementChar);
                return false;
            case '>':
                state = TokenizerState.Data;
                doctype.ForceQuirks = true;
                FinishDoctypeId(isPublic);
                return EmitDoctype(output);
            case eof:
                ReconsumeIn(TokenizerState.Data);
                doctype.ForceQuirks = true;
                FinishDoctypeId(isPublic);
                return EmitDoctype(output);
            default:
                AppendCodePoint(temporaryBuffer, c);
                return false;
        }
    }

    bool AfterDoctypePublicIdState(int c, Token output)
    {
        switch (c)
        {
            case '\t' or '\n' or '\f' or ' ':
                state = TokenizerState.BetweenDoctypePublicSystemId;
                return false;
            case '>':
                state = TokenizerState.Data;
                return EmitDoctype(output);
            case '"':
                state = TokenizerState.DoctypeSystemIdDoubleQuoted;
                return false;
            case '\'':
                state = TokenizerState.DoctypeSystemIdSingleQuoted;
                return false;
            case eof:
                ReconsumeIn(TokenizerState.Data);
                doctype.ForceQuirks = true;
                return EmitDoctype(output);
            default:
                ReconsumeIn(TokenizerState.BogusDoctype);
                doctype.ForceQuirks = true;
                return false;
        }
    }

    bool BetweenDoctypePublicSystemIdState(int c, Token output)
    {
        switch (c)
        {
            case '\t' or '\n' or '\f' or ' ':
                return false;
            case '>':
                state = TokenizerState.Data;
                return EmitDoctype(output);
            case '"':
                state = TokenizerState.DoctypeSystemIdDoubleQuoted;
                return false;
            case '\'':
                state = TokenizerState.DoctypeSystemIdSingleQuoted;
                return false;
            case eof:
                ReconsumeIn(TokenizerState.Data);
                doctype.ForceQuirks = true;
                return EmitDoctype(output);
            default:
                ReconsumeIn(TokenizerState.BogusDoctype);
                doctype.ForceQuirks = true;
                return false;
        }
    }

    bool AfterDoctypeSystemIdState(int c, Token output)
    {
        switch (c)
        {
            case '\t' or '\n' or '\f' or ' ':
                return false;
            case '>':
                state = TokenizerState.Data;
                return EmitDoctype(output);
            case eof:
                ReconsumeIn(TokenizerState.Data);
                doctype.ForceQuirks = true;
                return EmitDoctype(output);
            default:
                ReconsumeIn(TokenizerState.BogusDoctype);
                return false;
        }
    }

    bool BogusDoctypeState(int c, Token output)
    {
        switch (c)
        {
            case '>':
                state = TokenizerState.Data;
                return EmitDoctype(output);
            case eof:
                ReconsumeIn(TokenizerState.Data);
                return EmitDoctype(output);
            default:
                return false;
        }
    }

    bool CDataSectionState(int c, Token output)
    {
        switch (c)
        {
            case ']':
                state = TokenizerState.CDataSectionBracket;
                SetMark();
                return false;
            case eof:
                return EmitEof(output);
            default:
                return EmitChar(c, output);
        }
    }

    bool CDataSectionBracketState(int c, Token output)
    {
        if (c == ']')
        {
            state = TokenizerState.CDataSectionEnd;
            return false;
        }
        ReconsumeIn(TokenizerState.CDataSection);
        return EmitFromMark(output);
    }

    bool CDataSectionEndState(int c, Token output)
    {
        switch (c)
        {
            case ']':
                {
                    // Emit the first of three brackets, then carry on from the second
                    var result = EmitFromMark(output);
                    resumePos = -1;
                    SetMark();
                    state = TokenizerState.CDataSection;
                    return result;
                }
            case '>':
                Next();
                ReconsumeIn(TokenizerState.Data);
                isInCData = false;
                return false;
            default:
                ReconsumeIn(TokenizerState.CDataSection);
                return EmitFromMark(output);
        }
    }

    bool CharacterReferenceState(int c, Token output)
    {
        if (IsAlnum(c))
        {
            ReconsumeIn(TokenizerState.NamedCharacterReference);
            return false;
        }
        if (c == '#')
        {
            state = TokenizerState.NumericCharacterReference;
            return false;
        }
        ReconsumeIn(returnState);
        return FlushCodePointsConsumedAsCharacterReference(output);
    }

    bool NamedCharacterReferenceState(Token output)
    {
        var length = MatchNamedCharacterReference(out var first, out var second);
        if (length > 0)
        {
            var endsWithSemicolon = input[start + length - 1] == ';';
            for (var i = 0; i < length; i++)
            {
                Next();
            }
            var next = current;
            ReconsumeIn(returnState);
            if (CharacterReferencePartOfAttribute() && !endsWithSemicolon && (next == '=' || IsAlnum(next)))
            {
                return FlushCodePointsConsumedAsCharacterReference(output);
            }
            ReconsumeIn(returnState);
            return FlushCharRef(first, second, output);
        }
        ReconsumeIn(TokenizerState.AmbiguousAmpersand);
        return FlushCodePointsConsumedAsCharacterReference(output);
    }

    // match_named_char_ref: the longest name in the table the input starts with
    int MatchNamedCharacterReference(out int first, out int second)
    {
        var lookup = NamedCharacterReferences.All.GetAlternateLookup<ReadOnlySpan<char>>();
        var rest = input.AsSpan(start);
        var alnum = 0;
        while (alnum < rest.Length && alnum < NamedCharacterReferences.MaxLength && IsAlnum(rest[alnum]))
        {
            alnum++;
        }
        for (var length = alnum; length > 0; length--)
        {
            if (length < rest.Length && rest[length] == ';' && lookup.TryGetValue(rest[..(length + 1)], out var withSemicolon))
            {
                (first, second) = withSemicolon;
                return length + 1;
            }
            if (lookup.TryGetValue(rest[..length], out var withoutSemicolon))
            {
                (first, second) = withoutSemicolon;
                return length;
            }
        }
        first = second = noChar;
        return 0;
    }

    bool AmbiguousAmpersandState(int c, Token output)
    {
        if (IsAlnum(c))
        {
            if (CharacterReferencePartOfAttribute())
            {
                AppendCodePoint(tagBuffer, c);
                return false;
            }
            return EmitChar(c, output);
        }
        ReconsumeIn(returnState);
        return false;
    }

    bool NumericCharacterReferenceState(int c)
    {
        characterReferenceCode = 0;
        if (c is 'x' or 'X')
        {
            state = TokenizerState.HexadecimalCharacterReferenceStart;
            return false;
        }
        ReconsumeIn(TokenizerState.DecimalCharacterReferenceStart);
        return false;
    }

    // Hexadecimal and decimal character reference start states
    bool NumericCharacterReferenceStartState(Token output, bool isDigit, TokenizerState digits)
    {
        if (isDigit)
        {
            ReconsumeIn(digits);
            return false;
        }
        ReconsumeIn(returnState);
        return FlushCodePointsConsumedAsCharacterReference(output);
    }

    void AddDigit(int radix, int digit)
    {
        characterReferenceCode = characterReferenceCode * radix + digit;
        if (characterReferenceCode > maxChar)
        {
            characterReferenceCode = maxChar + 1;
        }
    }

    bool HexadecimalCharacterReferenceState(int c)
    {
        if (IsDigit(c))
        {
            AddDigit(16, c - '0');
            return false;
        }
        if (c is >= 'A' and <= 'F')
        {
            AddDigit(16, c - 'A' + 10);
            return false;
        }
        if (c is >= 'a' and <= 'f')
        {
            AddDigit(16, c - 'a' + 10);
            return false;
        }
        if (c == ';')
        {
            state = TokenizerState.NumericCharacterReferenceEnd;
            return false;
        }
        ReconsumeIn(TokenizerState.NumericCharacterReferenceEnd);
        return false;
    }

    bool DecimalCharacterReferenceState(int c)
    {
        if (IsDigit(c))
        {
            AddDigit(10, c - '0');
            return false;
        }
        if (c == ';')
        {
            state = TokenizerState.NumericCharacterReferenceEnd;
            return false;
        }
        ReconsumeIn(TokenizerState.NumericCharacterReferenceEnd);
        return false;
    }

    bool NumericCharacterReferenceEndState(Token output)
    {
        var c = characterReferenceCode;
        if (c == 0 || c > maxChar || c is >= 0xD800 and <= 0xDFFF)
        {
            c = replacementChar;
        }
        else if (!IsNoncharacter(c) && (c == 0x0D || (IsControl(c) && !IsSpace(c))))
        {
            c = WindowsReplacement(c);
        }
        ReconsumeIn(returnState);
        return FlushCharRef(c, noChar, output);
    }

    // utf8_is_noncharacter
    static bool IsNoncharacter(int c) => c is >= 0xFDD0 and <= 0xFDEF || (c & 0xFFFF) is 0xFFFE or 0xFFFF;

    // utf8_is_control: C0 and C1 controls (NUL excluded by the caller)
    static bool IsControl(int c) => c is >= 0x01 and <= 0x1F or >= 0x7F and <= 0x9F;

    static int WindowsReplacement(int c) => c switch
    {
        0x80 => 0x20AC,
        0x82 => 0x201A,
        0x83 => 0x0192,
        0x84 => 0x201E,
        0x85 => 0x2026,
        0x86 => 0x2020,
        0x87 => 0x2021,
        0x88 => 0x02C6,
        0x89 => 0x2030,
        0x8A => 0x0160,
        0x8B => 0x2039,
        0x8C => 0x0152,
        0x8E => 0x017D,
        0x91 => 0x2018,
        0x92 => 0x2019,
        0x93 => 0x201C,
        0x94 => 0x201D,
        0x95 => 0x2022,
        0x96 => 0x2013,
        0x97 => 0x2014,
        0x98 => 0x02DC,
        0x99 => 0x2122,
        0x9A => 0x0161,
        0x9B => 0x203A,
        0x9C => 0x0153,
        0x9E => 0x017E,
        0x9F => 0x0178,
        _ => c,
    };
}
