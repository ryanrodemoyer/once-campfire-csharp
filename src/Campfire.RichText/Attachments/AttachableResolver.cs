namespace Campfire.RichText.Attachments;

/// <summary>
/// What a mention renders from a user (<c>reference/app/views/users/_mention.html.erb</c> with
/// <c>avatar_tag</c>). Plain text and mentions only read <see cref="Id"/> and <see cref="Name"/>.
/// </summary>
/// <param name="Id">The user's id.</param>
/// <param name="Name"><c>User#name</c></param>
/// <param name="Title"><c>User#title</c>: the name and bio joined with " – ".</param>
/// <param name="AttachableSgid"><c>user.attachable_sgid</c>, freshly minted for the "attachable" purpose.</param>
/// <param name="UserPath"><c>user_path(user)</c></param>
/// <param name="AvatarPath"><c>fresh_user_avatar_path(user)</c></param>
public sealed record MentionUser(long Id, string Name, string Title, string AttachableSgid, string UserPath, string AvatarPath);

/// <summary>What a signed GlobalID verified for the "attachable" purpose points at.</summary>
public abstract record SignedLookup
{
    private SignedLookup() { }

    /// <summary>The signature verified and the user exists.</summary>
    public sealed record User(MentionUser Value) : SignedLookup;

    /// <summary>The signature verified (<c>SignedGlobalID.parse</c> succeeds) but the record is gone.</summary>
    public sealed record MissingRecord(string ModelName) : SignedLookup;

    /// <summary>Bad signature, wrong purpose, expired, or not an SGID at all.</summary>
    public sealed record Invalid : SignedLookup;

    public static SignedLookup None { get; } = new Invalid();
}

/// <summary>What <c>GlobalID.find</c> makes of a <c>gid://</c> URI, with no signature involved.</summary>
public enum GidLookupResult
{
    /// <summary>Found, but not a <c>User</c>: the invalid-signature fallback ignores it.</summary>
    OtherModel,

    /// <summary><c>GlobalID.find</c> returned nil or raised <c>ActiveRecord::RecordNotFound</c>.</summary>
    NotFound,

    /// <summary><c>GlobalID.find</c> raised anything else (an unknown model constant, say).</summary>
    Raises,
}

/// <summary>The app's records, as Action Text looks them up. Campfire's only attachables are users.</summary>
public interface IAttachableResolver
{
    /// <summary>
    /// <c>GlobalID::Locator.locate_signed(sgid, for: "attachable")</c>, and when that finds nothing,
    /// whether <c>SignedGlobalID.parse(sgid, for: "attachable")</c> still verifies.
    /// </summary>
    SignedLookup LocateSigned(string sgid);

    /// <summary>
    /// <c>GlobalID.find(gid)</c>: the user it names, or null with <paramref name="result"/> saying why not.
    /// </summary>
    MentionUser? FindGid(string gid, out GidLookupResult result);
}

/// <summary>Everything rendering reads from the request and the app.</summary>
/// <param name="Resolver">The app's records.</param>
/// <param name="RequestHost"><c>Current.request_host</c></param>
public sealed record RenderContext(IAttachableResolver Resolver, string? RequestHost);
