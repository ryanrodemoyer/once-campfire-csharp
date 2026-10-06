using System.Text.Json.Nodes;
using Campfire.Storage.Blobs;
using Campfire.Storage.Tests.Blobs;
using Campfire.Storage.Variants;
using Campfire.Vectors;

namespace Campfire.Storage.Tests.Variants;

/// <summary>
/// The representations the app asks for (thumbs, video posters, avatars, logos), their defaulted
/// transformations and their URLs, against what the reference generated. No image work is done.
/// </summary>
public class RepresentationTests
{
    static readonly BlobStorage Storage = StorageFixture.Storage("/rails/storage/files");
    static readonly RepresentationUrls Urls = new(Storage);
    static readonly DateTimeOffset Now = StorageFixture.Time("2026-01-01T00:00:00Z");

    public static TheoryData<StoredMessageAttachment> Messages() => StorageVectors.Messages();

    [Theory]
    [MemberData(nameof(Messages))]
    public void Message_attachments_are_representable_as_the_reference_says(StoredMessageAttachment vector)
    {
        var blob = StorageFixture.BlobFrom(vector.Blob);

        Assert.Equal(vector.Variable, blob.IsVariable);
        Assert.Equal(vector.Previewable, Representable.IsPreviewable(blob));
        if (!vector.Variable && !vector.Previewable)
        {
            Assert.Throws<UnrepresentableException>(() => Representable.Representation(blob, NamedVariants.MessageThumb));
            Assert.Throws<InvariableException>(() => Representable.Variant(blob, NamedVariants.MessageThumb));
            Assert.Throws<UnpreviewableException>(() => Representable.Preview(blob, NamedVariants.VideoPoster));
        }
    }

    [Theory]
    [MemberData(nameof(Messages))]
    public void Thumbs_are_the_references_variants_and_paths(StoredMessageAttachment vector)
    {
        if (vector.ThumbPath is null)
        {
            return;
        }
        var thumb = Representable.Representation(StorageFixture.BlobFrom(vector.Blob), NamedVariants.MessageThumb);

        var variant = Assert.IsType<VariantWithRecord>(thumb);
        var expected = Assert.Single(vector.Variants!);
        Assert.Equal(expected.TransformationsInspect, variant.Variation.Transformations.Inspect());
        Assert.Equal(expected.VariationDigest, variant.Variation.Digest);
        Assert.Equal(expected.Blob.Filename, variant.Filename.Value);
        Assert.Equal(vector.ThumbPath, Urls.RedirectPath(thumb));
        Assert.Equal(vector.ThumbProxyPath, Urls.ProxyPath(thumb));
    }

    [Theory]
    [MemberData(nameof(Messages))]
    public void Video_posters_are_the_references_previews_and_paths(StoredMessageAttachment vector)
    {
        if (vector.PosterPath is null)
        {
            return;
        }
        var video = StorageFixture.BlobFrom(vector.Blob);
        var previewImage = StorageFixture.BlobFrom(vector.PreviewImage!.Blob);
        var expected = vector.Variants!.ToDictionary(v => v.Label[(Path.GetFileNameWithoutExtension(vector.Fixture).Length + 1)..]);

        var poster = Assert.IsType<Preview>(Representable.Representation(video, NamedVariants.VideoPoster));
        Assert.Equal(vector.PosterPath, Urls.RedirectPath(poster));
        Assert.Equal(vector.PosterProxyPath, Urls.ProxyPath(poster));
        Assert.Equal(expected["poster"].VariationDigest, poster.VariantOf(previewImage).Variation.Digest);

        // Message#process_attachment's preview(format: :webp).
        var preview = Representable.Preview(video, NamedVariants.VideoPreview);
        Assert.True(preview.HasVariant);
        Assert.Equal(expected["preview-webp"].VariationDigest, preview.VariantOf(previewImage).Variation.Digest);

        // The representations controller decodes the poster's key, whose format is then a String.
        var key = Uri.UnescapeDataString(vector.PosterPath.Split('/')[^2]);
        var decoded = Variation.Decode(Storage.Verifier, key, Now)!;
        var fromKey = Representable.Representation(video, decoded);
        Assert.Equal(expected["poster-from-key"].VariationDigest, Assert.IsType<Preview>(fromKey).VariantOf(previewImage).Variation.Digest);
        Assert.Equal(vector.PosterPath, Urls.RedirectPath(fromKey));

        Assert.Equal(VariantProcessor.PreviewImageRecordType, vector.PreviewImage.Attachment.RecordType);
        Assert.Equal(VariantProcessor.PreviewImageName, vector.PreviewImage.Attachment.Name);
        Assert.Equal(video.Id, vector.PreviewImage.Attachment.RecordId);
    }

    [Fact]
    public void Avatars_are_the_references_square_variants()
    {
        foreach (var avatar in StorageVectors.File.Avatars)
        {
            var square = Representable.Variant(StorageFixture.BlobFrom(avatar.Blob), NamedVariants.AvatarSquare);

            var expected = Assert.Single(avatar.Variants);
            Assert.Equal(expected.TransformationsInspect, square.Variation.Transformations.Inspect());
            Assert.Equal(expected.VariationDigest, square.Variation.Digest);
            Assert.Equal(expected.Blob.Filename, square.Filename.Value);
            Assert.Equal(expected.Blob.ContentType, square.ContentType);
            if (avatar.Path is not null)
            {
                Assert.Equal(avatar.Path, Urls.RedirectPath(square));
            }
        }
    }

    [Fact]
    public void Logos_are_the_references_large_and_small_variants()
    {
        var logo = Assert.Single(StorageVectors.File.Logos);
        var blob = StorageFixture.BlobFrom(logo.Blob);

        foreach (var (named, expected) in new[] { NamedVariants.LogoLarge, NamedVariants.LogoSmall }.Zip(logo.Variants))
        {
            var variant = Representable.Variant(blob, named);
            Assert.Equal(expected.TransformationsInspect, variant.Variation.Transformations.Inspect());
            Assert.Equal(expected.VariationDigest, variant.Variation.Digest);
            Assert.Equal(expected.Blob.Filename, variant.Filename.Value);
        }
    }

    [Theory]
    // A web image keeps its own extension, case and all, when Marcel maps it to the content type.
    [InlineData("photo.jpeg", "image/jpeg", "{format: \"jpeg\", resize_to_limit: [1200, 800]}")]
    [InlineData("PHOTO.JPG", "image/jpeg", "{format: \"JPG\", resize_to_limit: [1200, 800]}")]
    [InlineData("anim.gif", "image/gif", "{format: \"gif\", resize_to_limit: [1200, 800]}")]
    // Otherwise the content type's first extension.
    [InlineData("photo.png", "image/jpeg", "{format: \"jpg\", resize_to_limit: [1200, 800]}")]
    [InlineData("photo", "image/png", "{format: \"png\", resize_to_limit: [1200, 800]}")]
    [InlineData("photo. ", "image/gif", "{format: \"gif\", resize_to_limit: [1200, 800]}")]
    // Variable but not web images become :png.
    [InlineData("photo.webp", "image/webp", "{format: :png, resize_to_limit: [1200, 800]}")]
    [InlineData("photo.heic", "image/heic", "{format: :png, resize_to_limit: [1200, 800]}")]
    public void Variants_default_to_the_blobs_format(string filename, string contentType, string inspect)
    {
        var blob = new Blob(1, "key", new Filename(filename), contentType, new JsonObject(), "local", 1, null, Now);

        Assert.Equal(inspect, Representable.Variant(blob, NamedVariants.MessageThumb).Variation.Transformations.Inspect());
    }

    [Fact]
    public void Keys_are_signed_once_per_variation()
    {
        var urls = new RepresentationUrls(Storage);
        var variation = new Variation(NamedVariants.AvatarSquare);

        Assert.Same(urls.Key(variation), urls.Key(new Variation(NamedVariants.AvatarSquare)));
        Assert.Equal(variation.Key(Storage.Verifier), urls.Key(variation));
    }
}
