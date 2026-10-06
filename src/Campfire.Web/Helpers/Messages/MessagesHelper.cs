using Campfire.RailsCompat.Formatting;
using Campfire.RichText.Attachments;
using Campfire.Storage.Blobs;
using Campfire.Storage.Variants;

namespace Campfire.Web.Helpers;

// The message presentation half of reference/app/helpers/messages_helper.rb (the tag helpers are
// in RoomsHelper.cs) and rooms_helper.rb's room_display_name.
public partial class View
{
    readonly Dictionary<MessageView, Presentation> presentations = new(ReferenceEqualityComparer.Instance);

    RepresentationUrls? representationUrls;

    /// <summary>
    /// The app's records and <c>Current.request_host</c>, which rendering a text message's rich
    /// text body reads.
    /// </summary>
    public RenderContext? RichTextContext { get; init; }

    /// <summary>Active Storage, for attachment and representation URLs.</summary>
    public BlobStorage? Storage { get; init; }

    internal BlobUrls BlobUrlsForAttachments => (Storage ?? throw new InvalidOperationException("View.Storage isn't set")).Urls;

    internal RepresentationUrls RepresentationUrlsForAttachments => representationUrls ??= new RepresentationUrls(Storage ?? throw new InvalidOperationException("View.Storage isn't set"));

    /// <summary>
    /// <c>message_tag(message) do ... end</c> with its rescue: <c>messages/_unrenderable</c> in
    /// place of a message whose block raises. It raises when the creator or a booster is gone
    /// (<c>user_path(nil)</c>, <c>nil.id</c>), and when presenting the body raised out of
    /// <c>message_presentation</c>'s own rescue.
    /// </summary>
    public IHtml MessageTag(MessageView message, Action body) =>
        IsUnrenderable(message) ? Render(MessagesUnrenderable) : MessageTag(message.TagInfo, body);

    bool IsUnrenderable(MessageView message) =>
        message.Creator is null || message.Boosts.Any(boost => boost.Booster is null) || MessagePresentation(message) is Presentation.Unrenderable;

    /// <summary>
    /// <c>message_presentation(message)</c>: the attachment, the sound, or the rich text body
    /// auto-linked. An exception renders as "" (<see cref="Presentation.Unrenderable"/> when even
    /// logging it raises). Each message is presented once per view.
    /// </summary>
    public Presentation MessagePresentation(MessageView message)
    {
        if (!presentations.TryGetValue(message, out var presentation))
        {
            presentation = PresentMessage(message);
            presentations[message] = presentation;
        }
        return presentation;
    }

    /// <summary><c>message_presentation(message)</c> as the <c>_presentation</c> partial outputs it.</summary>
    public SafeString MessagePresentationHtml(MessageView message) =>
        MessagePresentation(message) is Presentation.Html html ? new SafeString(html.Value) : SafeString.Empty;

    /// <summary>
    /// <c>room_display_name(room, for_user:)</c>: a direct room shows its members' names but
    /// <paramref name="forUserId"/>'s, in <c>room.users</c> order, as a sentence, falling back to
    /// <paramref name="forUserName"/>; other rooms show their name.
    /// </summary>
    /// <param name="roomName">The room's name; ignored for a direct room.</param>
    /// <param name="isDirect">Whether it is a direct room.</param>
    /// <param name="members">A direct room's users, as <c>room.users</c> lists them.</param>
    /// <param name="forUserId">The user left out, or null.</param>
    /// <param name="forUserName">That user's name.</param>
    public static string? RoomDisplayName(string? roomName, bool isDirect, IEnumerable<(long Id, string Name)> members, long? forUserId = null, string? forUserName = null)
    {
        if (!isDirect)
        {
            return roomName;
        }
        var names = members.Where(member => member.Id != forUserId).Select(member => member.Name).ToList();
        var sentence = TextHelpers.ToSentence(names);
        return sentence.Length > 0 ? sentence : forUserName;
    }

    /// <summary>
    /// <c>cache key do ... end</c>. A cached fragment holds exactly the bytes its block renders,
    /// so the block renders every time.
    /// </summary>
    public static void FragmentCache(Action body) => body();

    Presentation PresentMessage(MessageView message)
    {
        switch (message.ContentType)
        {
            case "attachment":
                return Rescued(() => new AttachmentPresentation(this, message.Attachment!).Render());
            case "sound":
                return Rescued(() => MessageSoundPresentation(message.Sound!));
            default:
                var context = RichTextContext ?? throw new InvalidOperationException("View.RichTextContext isn't set");
                return Campfire.RichText.Attachments.MessagePresentation.Present(message.Body ?? "", context);
        }
    }

    // `rescue Exception => e` around the attachment and sound branches: Rails logs it and shows "".
    static Presentation.Html Rescued(Func<SafeString> render)
    {
        try
        {
            return new Presentation.Html(render().ToString());
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException or KeyNotFoundException)
        {
            return new Presentation.Html("");
        }
    }

    /// <summary><c>message_sound_presentation(message)</c>: a play button and the sound's image or text.</summary>
    public SafeString MessageSoundPresentation(Sound sound) =>
        Tag.Div(
            OutputSafety.Concat(PlayButton(), sound.Image is { } image ? SoundImageTag(image) : sound.Text),
            new()
            {
                { "class", "sound" },
                { "data", new HtmlOptions { { "controller", "sound" }, { "action", "messages:play->sound#play" }, { "sound_url_value", AssetPath(sound.AssetPath) } } },
            });

    static SafeString PlayButton() =>
        Tag.Button("🔊", new() { { "class", "btn btn--plain" }, { "data", new HtmlOptions { { "action", "sound#play" } } } });

    SafeString SoundImageTag(SoundImage image) =>
        ImageTag(image.AssetPath, new() { { "width", image.Width }, { "height", image.Height }, { "class", "align--middle" } });
}
