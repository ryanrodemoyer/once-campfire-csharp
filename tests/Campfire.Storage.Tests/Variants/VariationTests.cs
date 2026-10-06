using Campfire.Storage.Blobs;
using Campfire.Storage.Tests.Blobs;
using Campfire.Storage.Variants;
using Campfire.Vectors;

namespace Campfire.Storage.Tests.Variants;

/// <summary>Marshal dumps, digests and signed keys against every variation the reference recorded.</summary>
public class VariationTests
{
    static readonly BlobStorage Storage = StorageFixture.Storage("/rails/storage/files");
    static readonly DateTimeOffset Now = StorageFixture.Time("2026-01-01T00:00:00Z");

    public static TheoryData<VariationCase> Variations() => StorageVectors.Variations();

    public static TheoryData<StoredVariant> Variants() => StorageVectors.Variants();

    [Theory]
    [MemberData(nameof(Variations))]
    public void Marshal_digest_and_key_equal_the_reference(VariationCase vector)
    {
        var variation = new Variation(TypedRuby.Transformations(vector.Typed));

        Assert.Equal(vector.Inspect, variation.Transformations.Inspect());
        Assert.Equal(vector.MarshalHex, Convert.ToHexStringLower(variation.Marshal()));
        Assert.Equal(vector.Digest, variation.Digest);
        Assert.Equal(vector.Key, variation.Key(Storage.Verifier));
    }

    [Theory]
    [MemberData(nameof(Variations))]
    public void Decoded_keys_carry_strings_and_digest_as_the_reference(VariationCase vector)
    {
        var decoded = Variation.Decode(Storage.Verifier, vector.Key, Now);

        Assert.NotNull(decoded);
        Assert.Equal(vector.DecodedInspect, decoded.Transformations.Inspect());
        Assert.Equal(vector.DecodedMarshalHex, Convert.ToHexStringLower(decoded.Marshal()));
        Assert.Equal(vector.DecodedDigest, decoded.Digest);
        Assert.Equal(vector.DecodedDigest, new Variation(TypedRuby.Transformations(vector.DecodedTyped)).Digest);
        // A decoded variation signs back to the same key.
        Assert.Equal(vector.Key, decoded.Key(Storage.Verifier));
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void Every_variant_records_digest_equals_the_reference(StoredVariant vector)
    {
        var variation = new Variation(TypedRuby.Transformations(vector.TransformationsTyped));

        Assert.Equal(vector.TransformationsInspect, variation.Transformations.Inspect());
        Assert.Equal(vector.VariationDigest, variation.Digest);
        Assert.Equal(vector.VariationDigest, vector.VariantRecord[2].GetString());
        Assert.Equal(vector.SourceBlobId, vector.VariantRecord[1].GetInt64());
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void Variant_filename_and_content_type_equal_the_reference(StoredVariant vector)
    {
        var variation = new Variation(TypedRuby.Transformations(vector.TransformationsTyped));
        var source = StorageFixture.BlobFrom(SourceBlob(vector.SourceBlobId));
        var variant = new VariantWithRecord(source, variation);

        Assert.Equal(vector.Blob.Filename, variant.Filename.Value);
        Assert.Equal(vector.Blob.ContentType, variant.ContentType);
        Assert.Equal(VariantRecords.RecordType, vector.Attachment.RecordType);
        Assert.Equal(VariantRecords.ImageName, vector.Attachment.Name);
    }

    [Theory]
    [InlineData("eyJfcmFpbHMiOnsiZGF0YSI6eyJmb3JtYXQiOiJ3ZWJwIn0sInB1ciI6InZhcmlhdGlvbiJ9fQ==--0000000000000000000000000000000000000000")]
    [InlineData("not a key")]
    public void Tampered_keys_do_not_decode(string key) => Assert.Null(Variation.Decode(Storage.Verifier, key, Now));

    [Fact]
    public void Keys_signed_for_another_purpose_do_not_decode()
    {
        var blobId = Storage.Urls.SignedId(1);

        Assert.Null(Variation.Decode(Storage.Verifier, blobId, Now));
    }

    [Fact]
    public void Format_defaults_to_png_and_must_be_an_extension_marcel_knows()
    {
        Assert.Equal("png", new Variation(new Transformations(("resize_to_limit", new[] { 1L, 1L }))).Format);
        Assert.Equal("image/png", new Variation(Transformations.Empty).ContentType);
        Assert.Equal("image/jpeg", new Variation(new Transformations(("format", "JPG"))).ContentType);
        Assert.Equal("JPG", new Variation(new Transformations(("format", "JPG"))).Format);
        Assert.Equal("image/webp", new Variation(NamedVariants.AvatarSquare).ContentType);

        var error = Assert.Throws<ArgumentException>(() => new Variation(new Transformations(("format", new RubySymbol("nope")))).Format);
        Assert.Equal("Invalid variant format (:nope)", error.Message);
        error = Assert.Throws<ArgumentException>(() => new Variation(new Transformations(("format", "nope"))).Format);
        Assert.Equal("Invalid variant format (\"nope\")", error.Message);
    }

    [Theory]
    [InlineData(1073741823L, "69 04 ff ff ff 3f")]
    [InlineData(1073741824L, "6c 2b 07 00 00 00 40")]
    [InlineData(-1073741824L, "69 fc 00 00 00 c0")]
    [InlineData(-1073741825L, "6c 2d 07 01 00 00 40")]
    [InlineData(long.MaxValue, "6c 2b 09 ff ff ff ff ff ff ff 7f")]
    public void Large_integers_marshal_as_ruby_does(long value, string hex)
    {
        // Values checked with `ruby -e 'puts Marshal.dump({a: [n]}).unpack1("H*")'`.
        var dumped = Convert.ToHexStringLower(new Variation(new Transformations(("a", new[] { value }))).Marshal());

        Assert.Equal("04087b063a06615b06" + hex.Replace(" ", "", StringComparison.Ordinal), dumped);
    }

    static StoredBlob SourceBlob(long id) =>
        StorageVectors.File.Messages.SelectMany(m => new[] { m.Blob, m.PreviewImage?.Blob })
            .Concat(StorageVectors.File.Avatars.Concat(StorageVectors.File.Logos).Select(i => i.Blob))
            .First(b => b?.Id == id)!;
}
