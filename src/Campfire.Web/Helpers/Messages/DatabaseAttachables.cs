using Campfire.Data.Queries;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.GlobalId;
using Campfire.RailsCompat.Signing;
using Campfire.RichText.Attachments;

namespace Campfire.Web.Helpers;

/// <summary>
/// Action Text's record lookups over the app's database: signed GlobalIDs verified with the app's
/// keys for the "attachable" purpose, and unsigned ones found as <c>GlobalID.find</c> finds them.
/// Campfire's only attachables are users.
/// </summary>
/// <param name="session">The request's database session.</param>
/// <param name="keys">The app's key generator.</param>
/// <param name="now">The time SGID expiry is checked against.</param>
public sealed class DatabaseAttachables(SqliteSession session, KeyGenerator keys, DateTimeOffset now) : IAttachableResolver
{
    // The models GlobalID.find can look up with `find`: the app's Active Record classes, by
    // the model name a GID carries. Anything else raises (NameError, NoMethodError).
    static readonly Dictionary<string, Func<SqliteSession, long, bool>> OtherModels = new()
    {
        ["Room"] = (s, id) => Rooms.Find(s, id) is not null,
        ["Rooms::Open"] = (s, id) => Rooms.Find(s, id) is { IsOpen: true },
        ["Rooms::Closed"] = (s, id) => Rooms.Find(s, id) is { IsClosed: true },
        ["Rooms::Direct"] = (s, id) => Rooms.Find(s, id) is { IsDirect: true },
        ["Message"] = (s, id) => Messages.Find(s, id) is not null,
        ["Boost"] = (s, id) => Boosts.Find(s, id) is not null,
        ["Membership"] = (s, id) => Memberships.Find(s, id) is not null,
        ["Account"] = (s, id) => Accounts.Find(s, id) is not null,
        ["Session"] = (s, id) => Sessions.Find(s, id) is not null,
        ["Webhook"] = (s, id) => Webhooks.Find(s, id) is not null,
    };

    /// <summary>
    /// <c>GlobalID::Locator.locate_signed(sgid, for: "attachable")</c>, rescuing
    /// <c>RecordNotFound</c> as <c>ActionText::Attachable.from_node</c> does.
    /// </summary>
    public SignedLookup LocateSigned(string sgid)
    {
        if (SignedGlobalId.LocateSigned(keys, sgid, SignedGlobalId.AttachablePurpose, now) is not { } gid)
        {
            return SignedLookup.None;
        }
        if (gid.ModelName == "User" && FindUser(gid.Id) is { } user)
        {
            return new SignedLookup.User(user);
        }
        return new SignedLookup.MissingRecord(gid.ModelName);
    }

    /// <summary><c>GlobalID.find(gid)</c>.</summary>
    public MentionUser? FindGid(string gid, out GidLookupResult result)
    {
        result = GidLookupResult.NotFound;
        if (GlobalId.Parse(gid) is not { } parsed)
        {
            return null;
        }
        if (parsed.ModelName == "User")
        {
            return FindUser(parsed.Id);
        }
        if (!OtherModels.TryGetValue(parsed.ModelName, out var exists))
        {
            result = GidLookupResult.Raises;
        }
        else if (long.TryParse(parsed.Id, out var id) && exists(session, id))
        {
            result = GidLookupResult.OtherModel;
        }
        return null;
    }

    /// <summary>What a mention renders of the user, freshly signed.</summary>
    public MentionUser? FindUser(string id)
    {
        if (!long.TryParse(id, out var userId) || Users.Find(session, userId) is not { } user)
        {
            return null;
        }
        return new MentionUser(
            user.Id,
            user.Name,
            user.Title,
            SignedGlobalId.AttachableSgid(keys, GlobalId.Create("User", user.Id)),
            Routes.UserPath(user.Id),
            Routes.FreshUserAvatarPath(TransferableUser.GenerateAvatarSignedId(keys, user.Id), user.UpdatedAt));
    }
}
