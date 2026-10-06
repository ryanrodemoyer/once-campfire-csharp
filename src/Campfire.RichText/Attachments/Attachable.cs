namespace Campfire.RichText.Attachments;

/// <summary>What an <c>&lt;action-text-attachment&gt;</c> attaches.</summary>
public abstract record Attachable
{
    public const string MentionContentType = "application/vnd.campfire.mention";
    public const string OpengraphEmbedContentType = "application/vnd.actiontext.opengraph-embed";

    private protected Attachable() { }

    /// <summary>
    /// <c>attachable_content_type</c>, which only mentions and opengraph embeds define here;
    /// asking any other attachable raises <c>NoMethodError</c>.
    /// </summary>
    public string AttachableContentType => this switch
    {
        Mention => MentionContentType,
        OpengraphEmbed => OpengraphEmbedContentType,
        _ => throw new RichTextRaisedException("NoMethodError: attachable_content_type"),
    };
}

/// <summary>A <c>User</c> (<c>reference/app/models/user/mentionable.rb</c>).</summary>
public sealed record Mention(MentionUser User) : Attachable;

/// <summary><c>ActionText::Attachment::OpengraphEmbed</c> (<c>reference/lib/rails_ext/actiontext_opengraph_embeds.rb</c>).</summary>
public sealed record OpengraphEmbed(string? Href, string? Url, string? Filename, string? Description) : Attachable
{
    public const string TwitterAvatarUrlPrefix = "https://pbs.twimg.com/profile_images";

    public bool TwitterAvatar => (Url ?? "").StartsWith(TwitterAvatarUrlPrefix, StringComparison.Ordinal);
}

/// <summary><c>ActionText::Attachables::ContentAttachment</c>: HTML carried in the node's <c>content</c>.</summary>
public sealed record ContentAttachment(string Content) : Attachable;

/// <summary><c>ActionText::Attachables::RemoteImage</c></summary>
public sealed record RemoteImage(string Url, string? Width, string? Height) : Attachable;

/// <summary>Lexxy's <c>ActionText::Attachables::RemoteVideo</c></summary>
public sealed record RemoteVideo(string Url, string ContentType, string? Width, string? Height, string? Filename) : Attachable;

/// <summary>
/// <c>ActionText::Attachables::MissingAttachable</c>, remembering the model a still-valid SGID
/// named (<c>SignedGlobalID.parse(sgid).model_name</c>).
/// </summary>
public sealed record MissingAttachable(string? SignedModel) : Attachable;

/// <summary><c>ActionText::Attachment</c>: the resolved attachable and the node's caption.</summary>
public sealed record Attachment(Attachable Attachable, string? Caption);
