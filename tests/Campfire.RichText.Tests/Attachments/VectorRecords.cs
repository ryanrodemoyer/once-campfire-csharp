using Campfire.RichText.Attachments;
using Campfire.Vectors;

namespace Campfire.RichText.Tests.Attachments;

/// <summary>
/// Stands in for the app over the records <c>reference-tools/richtext/generate.rb</c> created:
/// SGIDs Rails minted verify by exact match (as <c>signed</c> lists them), and GIDs are found by
/// model and id, ignoring the app as GlobalID's default locator does.
/// </summary>
sealed class VectorRecords : IAttachableResolver
{
    public static VectorRecords Instance { get; } = new(RichTextVectors.File);

    readonly RichTextFile file;

    VectorRecords(RichTextFile file) => this.file = file;

    public static RenderContext Context(string? host) => new(Instance, host);

    public MentionUser? User(long id) =>
        file.Users.FirstOrDefault(u => u.Id == id) is { } u
            ? new MentionUser(u.Id, u.Name, u.Title, u.AttachableSgid, u.UserPath, u.AvatarPath)
            : null;

    public SignedLookup LocateSigned(string sgid) =>
        file.SignedRecords.FirstOrDefault(s => s.Sgid == sgid) switch
        {
            { Model: "User", Exists: true } signed => new SignedLookup.User(User(signed.Id)!),
            { } signed => new SignedLookup.MissingRecord(signed.Model),
            null => SignedLookup.None,
        };

    public MentionUser? FindGid(string gid, out GidLookupResult result)
    {
        result = GidLookupResult.NotFound;
        if (!gid.StartsWith("gid://", StringComparison.Ordinal) || !gid.All(char.IsAscii))
        {
            return null;
        }
        var path = gid["gid://".Length..].Split('?')[0].Split('/');
        if (path.Length != 3 || !long.TryParse(path[2], out var id))
        {
            return null;
        }
        switch (path[1])
        {
            case "User":
                return User(id);
            case "Room" when file.Rooms.Contains(id):
                result = GidLookupResult.OtherModel;
                return null;
            default:
                return null;
        }
    }
}
