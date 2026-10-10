using System.Text;
using System.Text.RegularExpressions;
using static Campfire.RailsCompat.Ruby.RubyEscape;

namespace Campfire.RichText.Attachments;

/// <summary>
/// <c>render_action_text_attachment</c>: each attachable's partial, as Action Text, Lexxy and
/// Campfire render it, chomped.
/// </summary>
public static partial class AttachmentPartials
{
    [GeneratedRegex(@"^[-a-z]+://|^(?:cid|data):|^//", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex AssetUri();

    /// <summary>
    /// Renders <paramref name="attachment"/>'s partial. <paramref name="renderContent"/> renders a
    /// content attachment's own content (<c>ContentAttachment#to_html</c>).
    /// </summary>
    public static string Render(Attachment attachment, Func<string, string> renderContent, RenderContext context)
    {
        var html = attachment.Attachable switch
        {
            Mention mention => RenderMention(mention.User),
            OpengraphEmbed embed => RenderOpengraphEmbed(embed),
            ContentAttachment content => RenderContentAttachment(content, renderContent),
            RemoteImage image => RenderRemoteImage(image, attachment.Caption),
            RemoteVideo video => RenderRemoteVideo(video, attachment.Caption),
            MissingAttachable missing => RenderMissingAttachable(missing, context),
            _ => throw new InvalidOperationException($"Unknown attachable {attachment.Attachable}"),
        };
        return RubyText.Chomp(html);
    }

    /// <summary><c>reference/app/views/users/_mention.html.erb</c>, with <c>avatar_tag</c> (<c>reference/app/helpers/users/avatars_helper.rb</c>).</summary>
    public static string RenderMention(MentionUser user) =>
        $"<span class=\"mention\" sgid=\"{HtmlEscape(user.AttachableSgid)}\">" +
        $"<a title=\"{HtmlEscape(user.Title)}\" class=\"btn avatar\" data-turbo-frame=\"_top\" href=\"{HtmlEscape(user.UserPath)}\">" +
        $"<img aria-hidden=\"true\" src=\"{HtmlEscape(user.AvatarPath)}\" width=\"48\" height=\"48\" /></a> {HtmlEscape(user.Name)}</span>\n";

    /// <summary><c>reference/app/views/action_text/attachables/_opengraph_embed.html.erb</c></summary>
    public static string RenderOpengraphEmbed(OpengraphEmbed embed)
    {
        var filename = embed.Filename is null ? null : HtmlEscape(RubyText.Truncate(embed.Filename, 280, "…"));
        // link_to_if: a link named after its URL when there's no filename; the escaped name when there's no link
        var title = embed.Href is { } href
            ? $"<a rel=\"noreferrer\" target=\"_blank\" href=\"{HtmlEscape(href)}\">{filename ?? HtmlEscape(href)}</a>"
            : filename ?? "";

        var html = new StringBuilder();
        html.Append("<figure class=\"attachment attachment--content attachment--og\">\n  <actiontext-opengraph-embed>\n");
        html.Append($"    <div class=\"og-embed gap {(embed.TwitterAvatar ? "og-embed--twitter-avatar" : "")}\">\n");
        html.Append("      <div class=\"og-embed__content\">\n        <div class=\"og-embed__title\">\n");
        html.Append($"          {title}\n        </div>\n");
        html.Append($"        <div class=\"og-embed__description\">{HtmlEscape(RubyText.Truncate(embed.Description ?? "", 560, "…"))}</div>\n");
        html.Append("      </div>\n");
        if (embed.Url is { } url)
        {
            html.Append($"        <div class=\"og-embed__image\">\n          <img src=\"{HtmlEscape(url)}\" class=\"image center\" alt=\"\">\n        </div>\n");
        }
        html.Append("    </div>\n  </actiontext-opengraph-embed>\n</figure>\n");
        return html.ToString();
    }

    /// <summary>Action Text's <c>action_text/attachables/_content_attachment.html.erb</c></summary>
    static string RenderContentAttachment(ContentAttachment content, Func<string, string> renderContent) =>
        $"<figure class=\"attachment attachment--content\">\n  {renderContent(content.Content)}\n</figure>\n";

    /// <summary>Action Text's <c>action_text/attachables/_remote_image.html.erb</c></summary>
    static string RenderRemoteImage(RemoteImage image, string? caption) =>
        $"<figure class=\"attachment attachment--preview\">\n  {ImageTag(image.Url, image.Width, image.Height)}\n{Figcaption(caption)}</figure>\n";

    /// <summary>Lexxy's <c>action_text/attachables/_remote_video.html.erb</c></summary>
    static string RenderRemoteVideo(RemoteVideo video, string? caption)
    {
        var html = new StringBuilder("<figure class=\"attachment attachment--preview attachment--video\">\n  <video controls=\"controls\"");
        AppendAttribute(html, "width", video.Width);
        AppendAttribute(html, "height", video.Height);
        html.Append($">\n    <source src=\"{HtmlEscape(video.Url)}\" type=\"{HtmlEscape(video.ContentType)}\">\n</video>");
        return html.Append(Figcaption(caption)).Append("</figure>\n").ToString();
    }

    static string Figcaption(string? caption) =>
        caption is null ? "" : $"    <figcaption class=\"attachment__caption\">\n      {HtmlEscape(caption)}\n    </figcaption>\n";

    /// <summary>
    /// Action Text's <c>_missing_attachable.html.erb</c>, a ☒. When the SGID still verified, Rails
    /// asks its model for the partial, which only models including <c>ActionText::Attachable</c>
    /// as a concern answer; <c>User</c> includes it through a plain module, so a mention of a
    /// deleted user raises (and <c>message_presentation</c> renders nothing).
    /// </summary>
    static string RenderMissingAttachable(MissingAttachable missing, RenderContext context)
    {
        // The record exists: render its partial (messages/_message). The caller sanitizes it.
        if (missing.RenderModelId is not null && context.RenderLocatedModel is { } render)
        {
            return render(missing.SignedModel ?? "", missing.RenderModelId, context);
        }

        return missing.SignedModel is null
            ? "☒"
            : throw new RichTextRaisedException($"NoMethodError: undefined method 'to_missing_attachable_partial_path' for class {missing.SignedModel}");
    }

    /// <summary>
    /// <c>image_tag(url, width:, height:)</c> for a remote image. A source that isn't a URL goes
    /// through the asset pipeline, which raises for anything it doesn't know; a rooted path passes.
    /// </summary>
    static string ImageTag(string url, string? width, string? height)
    {
        string source;
        if (RubyText.IsBlank(url))
        {
            source = "";
        }
        else if (AssetUri().IsMatch(url) || url.StartsWith('/'))
        {
            source = url;
        }
        else
        {
            throw new RichTextRaisedException("Propshaft::MissingAssetError");
        }

        var html = new StringBuilder("<img");
        AppendAttribute(html, "width", width);
        AppendAttribute(html, "height", height);
        return html.Append($" src=\"{HtmlEscape(source)}\" />").ToString();
    }

    static void AppendAttribute(StringBuilder html, string name, string? value)
    {
        if (value is not null)
        {
            html.Append($" {name}=\"{HtmlEscape(value)}\"");
        }
    }
}
