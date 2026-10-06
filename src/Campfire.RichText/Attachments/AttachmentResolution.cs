using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.Ruby;
using Campfire.RichText.Html;
using Campfire.RichText.Sanitize;

namespace Campfire.RichText.Attachments;

/// <summary>
/// Resolves <c>&lt;action-text-attachment&gt;</c> nodes to what they attach, as Action Text,
/// Lexxy and Campfire's extensions do.
/// </summary>
public static partial class AttachmentResolution
{
    public const string TagName = "action-text-attachment";

    // ActionText::Attachment::OpengraphEmbed.from_node matches the constant as a regexp, dots and all
    [GeneratedRegex("application/vnd.actiontext.opengraph-embed")]
    private static partial Regex OpengraphContentType();

    [GeneratedRegex("^image(/.+|$)", RegexOptions.Multiline)]
    private static partial Regex ImageContentType();

    [GeneratedRegex("^video(/.+|$)", RegexOptions.Multiline)]
    private static partial Regex VideoContentType();

    [GeneratedRegex("(gid://campfire/[^/]+/[0-9]+)")]
    private static partial Regex MarshaledGid();

    static readonly JsonDocumentOptions RubyJson = new() { CommentHandling = JsonCommentHandling.Skip };

    public static bool IsAttachment(HtmlNode node) => node is HtmlElement element && element.IsHtml(TagName);

    /// <summary>The attachment elements under <paramref name="root"/>, in document order (<c>css("action-text-attachment")</c>).</summary>
    public static List<HtmlElement> AttachmentNodes(HtmlParentNode root) =>
        root.Descendants().OfType<HtmlElement>().Where(e => e.IsHtml(TagName)).ToList();

    /// <summary>
    /// Campfire's <c>ActionText::Attachment.from_node</c> (<c>reference/lib/rails_ext/action_text_attachables.rb</c>):
    /// an opengraph embed, else a User found through a possibly invalid SGID, else Action Text's own
    /// lookup as Lexxy extends it.
    /// </summary>
    public static Attachment AttachmentFromNode(HtmlElement node, RenderContext context) =>
        new(AttachableFromNode(node, context), RubyText.Presence(node.GetAttribute("caption")));

    static Attachable AttachableFromNode(HtmlElement node, RenderContext context)
    {
        if (OpengraphEmbedFromNode(node, context) is { } embed)
        {
            return embed;
        }
        if (AttachableFromPossiblyExpiredSgid(node.GetAttribute("sgid"), context) is { } user)
        {
            return new Mention(user);
        }
        return ActionTextAttachableFromNode(node, context);
    }

    /// <summary>
    /// <c>ActionText::Attachable.from_node</c> with Lexxy's RemoteVideo fallback for a missing
    /// attachable. <c>Content#attachables</c> (and so <c>Message#mentionees</c>) uses this directly,
    /// without Campfire's invalid-signature fallback.
    /// </summary>
    public static Attachable ActionTextAttachableFromNode(HtmlElement node, RenderContext context)
    {
        var sgid = node.GetAttribute("sgid");
        var signed = sgid is null ? SignedLookup.None : context.Resolver.LocateSigned(sgid);
        if (signed is SignedLookup.User found)
        {
            return new Mention(found.Value);
        }

        var contentType = node.GetAttribute("content-type");
        var content = node.GetAttribute("content");
        if (contentType is not null && contentType.Contains("html", StringComparison.Ordinal) && !RubyText.IsBlank(content))
        {
            return new ContentAttachment(content!);
        }

        if (node.GetAttribute("url") is { } url)
        {
            if (ImageContentType().IsMatch(contentType ?? ""))
            {
                return new RemoteImage(url, node.GetAttribute("width"), node.GetAttribute("height"));
            }
            if (VideoContentType().IsMatch(contentType ?? ""))
            {
                return new RemoteVideo(url, contentType ?? "", node.GetAttribute("width"), node.GetAttribute("height"), node.GetAttribute("filename"));
            }
        }

        return new MissingAttachable(signed is SignedLookup.MissingRecord missing ? missing.ModelName : null);
    }

    /// <summary>
    /// <c>attachable_from_possibly_expired_sgid</c>: reads the GlobalID out of an SGID without checking
    /// its signature, and only ever returns a User.
    /// </summary>
    public static MentionUser? AttachableFromPossiblyExpiredSgid(string? sgid, RenderContext context)
    {
        // sgid&.split("--")&.first: Ruby's split drops trailing empty fields
        if (sgid is null || FirstSplitField(sgid) is not { } message)
        {
            return null;
        }

        var root = ParseJson(DecodeBase64(message));
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new RichTextRaisedException("NoMethodError: undefined method 'dig'");
        }

        JsonElement? rails = null;
        if (root.TryGetProperty("_rails", out var railsProperty) && railsProperty.ValueKind != JsonValueKind.Null)
        {
            if (railsProperty.ValueKind != JsonValueKind.Object)
            {
                throw new RichTextRaisedException("TypeError: dig");
            }
            rails = railsProperty;
        }

        string? gid = null;
        if (Truthy(rails, "data") is { } data)
        {
            // GlobalID.find of anything but a string finds nothing
            gid = data.ValueKind == JsonValueKind.String ? data.GetString() : null;
        }
        else if (Truthy(rails, "message") is { } marshaled)
        {
            // Rails 7 Marshal-dumped the GID. The signature isn't verified, so the dump can't be
            // safely loaded; the GID is matched out of its bytes instead.
            if (marshaled.ValueKind != JsonValueKind.String)
            {
                throw new RichTextRaisedException("NoMethodError: undefined method 'unpack1'");
            }
            var bytes = DecodeBase64(marshaled.GetString()!);
            var match = MarshaledGid().Match(Encoding.Latin1.GetString(bytes));
            gid = match.Success ? Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(match.Value)) : null;
        }

        if (gid is null)
        {
            return null;
        }

        var user = context.Resolver.FindGid(gid, out var result);
        if (user is null && result == GidLookupResult.Raises)
        {
            throw new RichTextRaisedException("GlobalID.find raised");
        }
        return user;
    }

    static string? FirstSplitField(string s)
    {
        var fields = s.Split("--");
        var count = fields.Length;
        while (count > 0 && fields[count - 1].Length == 0)
        {
            count--;
        }
        return count > 0 ? fields[0] : null;
    }

    static JsonElement? Truthy(JsonElement? obj, string name) =>
        obj is { } o && o.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.False)
            ? value
            : null;

    // JSON.parse takes the bytes as UTF-8; when they aren't, its error message quotes them and
    // logging that message raises in turn
    static JsonElement ParseJson(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        try
        {
            using var document = JsonDocument.Parse(text, RubyJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new RichTextRaisedException("JSON::ParserError", unloggable: !IsValidUtf8(bytes));
        }
    }

    static bool IsValidUtf8(byte[] bytes) => System.Text.Unicode.Utf8.IsValid(bytes);

    /// <summary><c>Base64.strict_decode64(message) rescue Base64.urlsafe_decode64(message)</c></summary>
    static byte[] DecodeBase64(string message) =>
        RubyBase64.StrictDecode(message)
        ?? RubyBase64.UrlSafeDecode(message)
        ?? throw new RichTextRaisedException("ArgumentError: invalid base64");

    /// <summary><c>ActionText::Attachment::OpengraphEmbed.from_node</c></summary>
    public static OpengraphEmbed? OpengraphEmbedFromNode(HtmlElement node, RenderContext context)
    {
        var contentType = node.GetAttribute("content-type");
        if (contentType is null || !OpengraphContentType().IsMatch(contentType))
        {
            return null;
        }

        var host = context.RequestHost ?? "";
        if (!RubyText.IsBlank(node.GetAttribute("filename")))
        {
            // Trix serialized the embed's details as attributes of the node
            return new OpengraphEmbed(
                OpengraphEmbedUrl.WebUrl(node.GetAttribute("href"), host),
                OpengraphEmbedUrl.WebUrl(node.GetAttribute("url"), host),
                node.GetAttribute("filename"),
                node.GetAttribute("caption"));
        }
        return EmbedFromContent(node.GetAttribute("content") ?? "", host);
    }

    /// <summary><c>attributes_from_content</c>: the details Lexxy serializes as the embed's content markup.</summary>
    static OpengraphEmbed EmbedFromContent(string content, string host)
    {
        var fragment = HtmlParser.ParseFragment(content);
        var elements = fragment.Descendants().OfType<HtmlElement>().ToList();
        var title = elements.FirstOrDefault(e => RubyText.HasClass(e.GetAttribute("class"), "og-embed__title"));
        var link = title?.Descendants().OfType<HtmlElement>().FirstOrDefault(e => e.IsHtml("a"));
        var image = elements.FirstOrDefault(e => e.IsHtml("img") && HasAncestorWithClass(e, "og-embed__image"));
        var description = elements.FirstOrDefault(e => RubyText.HasClass(e.GetAttribute("class"), "og-embed__description"));

        return new OpengraphEmbed(
            OpengraphEmbedUrl.WebUrl(link?.GetAttribute("href"), host),
            OpengraphEmbedUrl.WebUrl(image?.GetAttribute("src"), host),
            (link ?? title) is { } named ? RubyString.Strip(named.TextContent) : null,
            description is null ? null : RubyString.Strip(description.TextContent));
    }

    static bool HasAncestorWithClass(HtmlNode node, string name)
    {
        for (var ancestor = node.Parent; ancestor is HtmlElement element; ancestor = element.Parent)
        {
            if (RubyText.HasClass(element.GetAttribute("class"), name))
            {
                return true;
            }
        }
        return false;
    }
}
