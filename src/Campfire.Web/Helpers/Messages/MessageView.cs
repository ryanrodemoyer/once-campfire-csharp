using Campfire.Data.Records;
using Campfire.RailsCompat.Formatting;
using Campfire.Storage.Blobs;

namespace Campfire.Web.Helpers;

/// <summary>
/// What <c>messages/_message</c> and the message JSON partial read from a message, loaded as
/// <c>Message.with_presentation</c> loads it: the creator, the room, the rich text body, the
/// attachment's blob and the boosts with their boosters.
/// </summary>
/// <param name="Id">The <c>messages.id</c>.</param>
/// <param name="ClientMessageId"><c>to_key</c>, which DOM ids use; URLs use the id.</param>
/// <param name="CreatedAt"><c>created_at</c></param>
/// <param name="UpdatedAt"><c>updated_at</c></param>
/// <param name="CreatorId"><c>creator_id</c></param>
/// <param name="Room">The room, with the name <c>room_display_name(room, for_user: nil)</c> shows.</param>
/// <param name="Creator">The creator, null when the user is gone (the message is then unrenderable).</param>
/// <param name="Body">The <c>body</c> rich text's HTML (<c>message.body.body</c>), null when it has none.</param>
/// <param name="PlainTextBody"><c>plain_text_body</c>: the body's plain text, else the attachment's filename, else "".</param>
/// <param name="Attachment">The <c>attachment</c>'s blob, null when nothing is attached.</param>
/// <param name="Boosts"><c>message.boosts.ordered</c>.</param>
public sealed record MessageView(
    long Id,
    string ClientMessageId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long CreatorId,
    MessageRoom Room,
    MessageUser? Creator,
    string? Body,
    string PlainTextBody,
    Blob? Attachment,
    IReadOnlyList<BoostView> Boosts)
{
    /// <summary>The record <c>dom_id</c> and form helpers read.</summary>
    public RecordKey Record => new(Message.ModelName, ClientMessageId);

    /// <summary>
    /// <c>content_type</c>: "attachment", else "sound" for <c>/play name</c> with a known sound,
    /// else "text".
    /// </summary>
    public string ContentType => Attachment is not null ? "attachment" : Sound is not null ? "sound" : "text";

    /// <summary>
    /// <c>sound</c>: <c>plain_text_body.match(/\A\/play (?&lt;name&gt;\w+)\z/)</c> looked up by name.
    /// Every sound's name is ASCII word characters, so the name is simply the rest of the body.
    /// </summary>
    public Sound? Sound => PlainTextBody.StartsWith("/play ", StringComparison.Ordinal) ? Sound.FindByName(PlainTextBody[6..]) : null;

    /// <summary><c>plain_text_body.all_emoji?</c></summary>
    public bool IsAllEmoji => StringExtensions.AllEmoji(PlainTextBody);

    /// <summary>What <c>message_tag</c> reads.</summary>
    public MessageTagInfo TagInfo => new(Id, ClientMessageId, CreatorId, CreatedAt, UpdatedAt, IsAllEmoji);
}

/// <summary>A message's room.</summary>
/// <param name="Id">The room's id.</param>
/// <param name="DisplayName"><c>room_display_name(room, for_user: nil)</c>: see <see cref="View.RoomDisplayName"/>.</param>
public sealed record MessageRoom(long Id, string? DisplayName);

/// <summary>A message's creator or a boost's booster, as the partials and <c>users/_user.json</c> read them.</summary>
/// <param name="Id">The user's id.</param>
/// <param name="Name"><c>name</c></param>
/// <param name="Title"><c>title</c>: the name and bio joined with " – ".</param>
/// <param name="Role">The role, which the JSON shows as its name.</param>
/// <param name="AvatarToken">The avatar's signed id (<c>user.avatar_token</c>), for <c>fresh_user_avatar_path</c>.</param>
/// <param name="UpdatedAt"><c>updated_at</c>, the avatar URL's cache buster.</param>
public sealed record MessageUser(long Id, string Name, string Title, UserRole Role, string AvatarToken, DateTimeOffset UpdatedAt)
{
    public AvatarUser Avatar => new(Id, Title, AvatarToken, UpdatedAt);

    /// <summary>The user's fields, from its row and its avatar token.</summary>
    public static MessageUser From(User user, string avatarToken) =>
        new(user.Id, user.Name, user.Title, user.Role, avatarToken, user.UpdatedAt);
}

/// <summary>A boost, with what its partials read from its message and booster.</summary>
/// <param name="Id">The boost's id.</param>
/// <param name="Content">What the booster wrote.</param>
/// <param name="CreatedAt"><c>created_at</c></param>
/// <param name="Booster">The booster, null when the user is gone.</param>
/// <param name="MessageId">The boosted message's id.</param>
/// <param name="RoomId">The boosted message's room.</param>
public sealed record BoostView(
    long Id,
    string Content,
    DateTimeOffset CreatedAt,
    MessageUser? Booster,
    long MessageId,
    long RoomId)
{
    /// <summary>The record <c>dom_id</c> reads.</summary>
    public RecordKey Record => new("Boost", Id);
}
