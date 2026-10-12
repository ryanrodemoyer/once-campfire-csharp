using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Campfire.Web.Helpers;

namespace Campfire.Web.ResponseCache;

/// <summary>
/// One request's fragment namespace (reference/app/controllers/concerns/cached_responses.rb
/// <c>combined_fragment_cache_key</c> and <c>MessagesHelper</c>). A content digest omits the
/// epoch, so an unrelated commit leaves it; every other key includes the captured version.
/// The viewer and the session digest are in the context either way. The raw session token is not.
/// </summary>
public sealed class FragmentRendering
{
    public required FragmentStore Store { get; init; }

    public required string Version { get; init; }

    public required string Context { get; init; }

    public required string BaseUrl { get; init; }

    /// <summary>The collection or inner key for <paramref name="message"/>. A missing creator on a content-keyed message raises, as <c>creator.attributes</c> does.</summary>
    public string CollectionKey(MessageView message) => Key("collection", message, presentation: false);

    /// <summary><c>cache [message_fragment_cache_key, "presentation-v7"]</c>.</summary>
    public string InnerKey(MessageView message) => Key("inner", message, presentation: true);

    /// <summary><c>cache_message_actions</c>: no epoch and no viewer, so controls survive a creator edit.</summary>
    public string ActionsKey(MessageView message) =>
        string.Create(CultureInfo.InvariantCulture, $"message-actions-v1\n{BaseUrl}\n\nen\n{message.Id}\n{message.Room.Id}");

    /// <summary><c>text-presentation-v1</c>: the plain HTML's digest, with no epoch.</summary>
    public static string TextKey(string html) => "text-presentation-v1\nen\n" + Digest(html);

    /// <summary><c>json.cache! message</c>: the message's cache key, with the epoch and the request context.</summary>
    public string JsonKey(MessageView message) =>
        $"json\n{Version}\n{Context}\n" + EpochIdentity(message);

    /// <summary><c>cache membership</c> for a sidebar direct: the epoch, so any commit rebuilds it.</summary>
    public string DirectKey(long roomId) =>
        string.Create(CultureInfo.InvariantCulture, $"direct\n{Version}\n{Context}\n{roomId}");

    public void NoteHit(string key)
    {
        if (Store.Track)
        {
            Store.HitKeys.Add(key);
        }
    }

    public void NoteCollection(string identifier, int count, int hits)
    {
        if (Store.Track)
        {
            Store.Collections.Add((identifier, count, hits));
        }
    }

    public void NoteActionRender()
    {
        if (Store.Track)
        {
            Store.ActionRenders++;
        }
    }

    string Key(string kind, MessageView message, bool presentation)
    {
        var suffix = presentation ? "\npresentation-v7" : "";
        if (!IsContentKeyed(message))
        {
            return $"{kind}\n{Version}\n{Context}\n{EpochIdentity(message)}{suffix}";
        }
        // message.creator.attributes runs outside message_tag's rescue. A gone creator or booster
        // fails the page (reference/app/helpers/messages_helper.rb).
        if (message.Creator is null || message.Boosts.Any(boost => boost.Booster is null))
        {
            throw new InvalidOperationException("undefined method 'attributes' for nil");
        }
        return $"{kind}\n{Context}\n{ContentDigest(message)}{suffix}";
    }

    // Direct rooms, attachments and embedded attachments keep the epoch: their HTML depends on
    // rows the content digest does not cover. A direct's display name is its members.
    static bool IsContentKeyed(MessageView message) =>
        !message.Room.IsDirect
        && message.Attachment is null
        && message.Body is { Length: > 0 } body
        && !body.Contains("<action-text-attachment", StringComparison.Ordinal);

    static string EpochIdentity(MessageView message) =>
        string.Create(CultureInfo.InvariantCulture, $"messages/{message.Id}-{message.UpdatedAt.UtcTicks}");

    // The fields message_fragment_cache_key fingerprints: the message, the room's id, type and
    // name, the creator, the body HTML and each boost with its booster.
    static string ContentDigest(MessageView message)
    {
        var creator = message.Creator!;
        var builder = new StringBuilder();
        builder.Append(message.Id).Append('\n')
            .Append(message.ClientMessageId).Append('\n')
            .Append(message.CreatorId).Append('\n')
            .Append(message.CreatedAt.UtcTicks).Append('\n')
            .Append(message.UpdatedAt.UtcTicks).Append('\n')
            .Append(message.Room.Id).Append('\n')
            .Append(message.Room.TypeName).Append('\n')
            .Append(message.Room.DisplayName).Append('\n')
            .Append(creator.Id).Append('\n')
            .Append(creator.Name).Append('\n')
            .Append(creator.Title).Append('\n')
            .Append(creator.Role).Append('\n')
            .Append(creator.AvatarToken).Append('\n')
            .Append(creator.UpdatedAt.UtcTicks).Append('\n')
            .Append(message.Body).Append('\n');
        foreach (var boost in message.Boosts.OrderBy(item => item.CreatedAt.UtcTicks).ThenBy(item => item.Id))
        {
            var booster = boost.Booster!;
            builder.Append(boost.Id).Append('\n')
                .Append(boost.Content).Append('\n')
                .Append(boost.CreatedAt.UtcTicks).Append('\n')
                .Append(booster.Id).Append('\n')
                .Append(booster.Name).Append('\n')
                .Append(booster.Title).Append('\n')
                .Append(booster.UpdatedAt.UtcTicks).Append('\n');
        }
        return Digest(builder.ToString());
    }

    static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
