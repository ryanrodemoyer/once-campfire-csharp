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

    // ActionView::Helpers::AssetUrlHelper::URI_REGEXP. RemoteImage.from_node requires it, so a
    // rooted path or a javascript: URL is not a remote image.
    [GeneratedRegex("^[-a-z]+://|^(?:cid|data):|^//", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex RemoteUri();

    [GeneratedRegex("^video(/.+|$)", RegexOptions.Multiline)]
    private static partial Regex VideoContentType();

    [GeneratedRegex("(gid://campfire/[^/]+/[0-9]+)")]
    private static partial Regex MarshaledGid();

    public static bool IsAttachment(HtmlNode node) => node is HtmlElement element && element.Name == TagName;

    /// <summary>
    /// The attachment elements under <paramref name="root"/>, in document order
    /// (<c>css("action-text-attachment")</c>). Nokogiri matches that selector by local name in
    /// every namespace, so an attachment inside SVG or MathML counts.
    /// </summary>
    public static List<HtmlElement> AttachmentNodes(HtmlParentNode root) =>
        root.Descendants().OfType<HtmlElement>().Where(e => e.Name == TagName).ToList();

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
        if (signed is SignedLookup.StoredBlob blob)
        {
            return blob.Value;
        }

        // locate_signed wins over a content attachment. A Message renders messages/_message;
        // its plain text is the caption, because the model defines none of its own.
        if (signed is SignedLookup.Record located)
        {
            return new MissingAttachable(located.ModelName, located.ModelId);
        }

        var contentType = node.GetAttribute("content-type");
        var content = node.GetAttribute("content");
        if (contentType is not null && contentType.Contains("html", StringComparison.Ordinal) && !RubyText.IsBlank(content))
        {
            return new ContentAttachment(content!);
        }

        if (node.GetAttribute("url") is { } url)
        {
            // RemoteImage.from_node also requires URI_REGEXP. A rooted path or javascript: URL
            // is a missing attachable. Lexxy's RemoteVideo only requires a url.
            if (ImageContentType().IsMatch(contentType ?? "") && RemoteUri().IsMatch(url))
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

        var root = RubyJsonParser.Parse(DecodeBase64(message));
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

        return AttributesFromNode(node, context.RequestHost ?? "");
    }

    /// <summary><c>attributes_from_node</c>: the embed's details, from Trix's attributes or Lexxy's content markup.</summary>
    public static OpengraphEmbed AttributesFromNode(HtmlElement node, string host)
    {
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
        var (href, image, title, description) = ContentDetails(content);
        return new OpengraphEmbed(OpengraphEmbedUrl.WebUrl(href, host), OpengraphEmbedUrl.WebUrl(image, host), title, description);
    }

    /// <summary>
    /// What <c>attributes_from_content</c> reads from the content markup before checking its URLs.
    /// Rails parses it with <c>Nokogiri::HTML.fragment</c>, libxml2's legacy parser, not HTML5.
    /// </summary>
    public static (string? Href, string? Image, string? Title, string? Description) ContentDetails(string content)
    {
        var elements = LegacyHtmlParser.ParseFragment(content).Descendants().OfType<HtmlElement>().ToList();
        var title = elements.FirstOrDefault(e => RubyText.HasClass(e.GetAttribute("class"), "og-embed__title"));
        var link = title?.Descendants().OfType<HtmlElement>().FirstOrDefault(e => e.IsHtml("a"));
        var image = elements.FirstOrDefault(e => e.IsHtml("img") && HasAncestorWithClass(e, "og-embed__image"));
        var description = elements.FirstOrDefault(e => RubyText.HasClass(e.GetAttribute("class"), "og-embed__description"));

        return (
            link?.GetAttribute("href"),
            image?.GetAttribute("src"),
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
