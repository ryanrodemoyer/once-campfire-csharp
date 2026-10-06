using System.Text.Json;
using Campfire.RichText.Attachments;
using Campfire.Vectors;

namespace Campfire.RichText.Tests.Attachments;

/// <summary>
/// Campfire's invalid-signature fallback for mentions (<c>reference/lib/rails_ext/action_text_attachables.rb</c>)
/// against the reference's answers in <c>unverified_sgids</c> (<c>vectors/rails_compat.json</c>).
/// </summary>
public class PossiblyExpiredSgidTests
{
    public static TheoryData<UnverifiedSgidCase> Cases() => RailsCompatVectors.UnverifiedSgids();

    [Theory]
    [MemberData(nameof(Cases))]
    public void Finds_users_as_rails_does(UnverifiedSgidCase vector)
    {
        var context = new RenderContext(new UsersOneAndTwo(), null);

        switch (vector.Expected.ValueKind)
        {
            case JsonValueKind.Null:
                Assert.Null(AttachmentResolution.AttachableFromPossiblyExpiredSgid(vector.Sgid, context));
                break;
            case JsonValueKind.String:
                var user = AttachmentResolution.AttachableFromPossiblyExpiredSgid(vector.Sgid, context);
                Assert.Equal(long.Parse(vector.Expected.GetString()!.Split('/')[^1]), user?.Id);
                break;
            default:
                Assert.Throws<RichTextRaisedException>(() => AttachmentResolution.AttachableFromPossiblyExpiredSgid(vector.Sgid, context));
                break;
        }
    }

    /// <summary>The records the generator made: users 1 and 2; GlobalID's default locator ignores the GID's app.</summary>
    sealed class UsersOneAndTwo : IAttachableResolver
    {
        public SignedLookup LocateSigned(string sgid) => SignedLookup.None;

        public MentionUser? FindGid(string gid, out GidLookupResult result)
        {
            result = GidLookupResult.NotFound;
            var path = gid.Split('?')[0].Split('/');
            return path is ["gid:", "", _, "User", "1" or "2"]
                ? new MentionUser(long.Parse(path[4]), "", "", "", "", "")
                : null;
        }
    }
}
