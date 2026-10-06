using System.Text;
using Campfire.Storage.Blobs;
using Campfire.Vectors;

namespace Campfire.Storage.Tests.Blobs;

public class FilenameTests
{
    static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    public static TheoryData<FilenameCase> Filenames() => StorageVectors.Filenames();

    [Theory]
    [MemberData(nameof(Filenames))]
    public void Sanitizes_like_active_storage(FilenameCase vector)
    {
        var filename = Filename.FromBytes(Convert.FromHexString(vector.InputHex));

        Assert.Equal(vector.Sanitized, filename.Sanitized);
    }

    [Theory]
    [MemberData(nameof(Filenames))]
    public void Splits_base_and_extension_like_ruby(FilenameCase vector)
    {
        var bytes = Convert.FromHexString(vector.InputHex);
        // Rails splits the raw bytes; a .NET string can't hold an invalid byte, so that one case is
        // only checked through Sanitized.
        Assert.SkipWhen(!IsUtf8(bytes), "invalid UTF-8 input");
        var filename = new Filename(StrictUtf8.GetString(bytes));

        Assert.Equal(vector.BaseHex, Convert.ToHexStringLower(Encoding.UTF8.GetBytes(filename.Base)));
        Assert.Equal(vector.ExtensionHex, Convert.ToHexStringLower(Encoding.UTF8.GetBytes(filename.Extension)));
    }

    [Theory]
    [MemberData(nameof(Filenames))]
    public void Formats_content_disposition_like_action_dispatch(FilenameCase vector)
    {
        Assert.Equal(vector.Inline, ContentDisposition.Format("inline", vector.Sanitized));
        Assert.Equal(vector.Attachment, ContentDisposition.Format("attachment", vector.Sanitized));
    }

    [Theory]
    [MemberData(nameof(Filenames))]
    public void Escapes_the_filename_glob_like_journey(FilenameCase vector) =>
        Assert.Equal(vector.EscapedPath, RouteEscaping.EscapePath(vector.Sanitized));

    [Theory]
    [InlineData("foo.", ".", "foo")]
    [InlineData("a.b.", ".", "a.b")]
    [InlineData(".bashrc", "", ".bashrc")]
    [InlineData("..", "", "..")]
    [InlineData("...", "", "...")]
    [InlineData(".a.b", ".b", ".a")]
    [InlineData("a..b", ".b", "a.")]
    [InlineData("a/b.c/d", "", "d")]
    [InlineData("a.b/", ".b", "a")]
    [InlineData("x.tar.gz", ".gz", "x.tar")]
    [InlineData("a/.b", "", ".b")]
    [InlineData(".", "", ".")]
    [InlineData("", "", "")]
    [InlineData("é.png", ".png", "é")]
    [InlineData("a. b", ". b", "a")]
    public void Follows_rubys_file_extname_and_basename(string name, string extension, string baseName)
    {
        var filename = new Filename(name);

        Assert.Equal(extension, filename.ExtensionWithDelimiter);
        Assert.Equal(baseName, filename.Base);
    }

    [Fact]
    public void Content_disposition_falls_back_to_inline_and_has_no_filename_without_one()
    {
        Assert.Equal("inline", ContentDisposition.Format("inline", null));
        Assert.StartsWith("inline;", ContentDisposition.For("sideways", new Filename("a.png")), StringComparison.Ordinal);
        Assert.StartsWith("inline;", ContentDisposition.For(null, new Filename("a.png")), StringComparison.Ordinal);
        Assert.StartsWith("attachment;", ContentDisposition.For("attachment", new Filename("a.png")), StringComparison.Ordinal);
    }

    static bool IsUtf8(byte[] bytes)
    {
        try
        {
            StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
