using System.Text.Json.Nodes;
using Campfire.RailsCompat.Crypto;
using Campfire.Storage.Media;
using Campfire.Storage.Tests.Blobs;
using Campfire.Storage.Variants;
using Campfire.Vectors;

namespace Campfire.Storage.Tests.Media;

/// <summary>The pieces around libvips and ffmpeg: arguments, operations, analyzers' parsing, failures.</summary>
public sealed class MediaToolTests : IDisposable
{
    readonly string directory = Directory.CreateTempSubdirectory("campfire-media-tests").FullName;

    [Fact]
    public void Video_preview_arguments_split_as_ruby_splits_them()
    {
        Assert.Equal(VideoPreviewer.DefaultArguments, StorageVectors.File.VideoPreviewArguments);

        // ruby -rshellwords -e 'p Shellwords.split(ActiveStorage.video_preview_arguments)'
        Assert.Equal(
            ["-vf", @"select=eq(n\,0)+eq(key\,1)+gt(scene\,0.015),loop=loop=-1:size=2,trim=start_frame=1", "-frames:v", "1", "-f", "image2"],
            Shellwords.Split(VideoPreviewer.DefaultArguments));
    }

    [Fact]
    public void Shellwords_handles_quotes_and_escapes_as_ruby_does()
    {
        // ruby -rshellwords -e 'p Shellwords.split(%q{a "b c" d\ e '"'"'f g'"'"' "h\"i" "j\\k" "$x"})'
        Assert.Equal(["a", "b c", "d e", "f g", "h\"i", @"j\k", "$x"], Shellwords.Split("""a "b c" d\ e 'f g' "h\"i" "j\\k" "$x" """));
        Assert.Empty(Shellwords.Split("  "));
        var error = Assert.Throws<ArgumentException>(() => Shellwords.Split("a \"b"));
        Assert.Equal("Unmatched quote: a \"b", error.Message);
    }

    [Fact]
    public void Operations_skip_format_and_blank_arguments_and_default_missing_dimensions()
    {
        var transformations = new Transformations(
            ("format", new RubySymbol("webp")), ("resize_to_limit", new object?[] { 512, null }), ("resize_to_fill", null), ("quality", ""));

        Assert.Equal([(512, ImageTransformer.MaxCoord)], ImageTransformer.Operations(transformations));
    }

    [Fact]
    public void Operations_refuse_what_rails_or_campfire_refuses()
    {
        Assert.Throws<ArgumentException>(() => ImageTransformer.Operations(new(("combine_options", new object?[] { 1 }))));
        Assert.Throws<ArgumentException>(() => ImageTransformer.Operations(new(("resize_to_limit", new object?[] { null, null }))));
        Assert.Throws<NotSupportedException>(() => ImageTransformer.Operations(new(("rotate", 90L))));
        Assert.Throws<NotSupportedException>(() => ImageTransformer.Operations(new(("resize_to_limit", new object?[] { (long)int.MaxValue + 1, 1 }))));
    }

    [Fact]
    public void Video_metadata_is_the_analyzers()
    {
        var probe = Probe("""{"streams":[{"codec_type":"video","width":320,"height":180,"display_aspect_ratio":"16:9","duration":"65.840000"}],"format":{"duration":"65.9"}}""");

        // The reference's own analysis of alpha-centuri.mov has this shape (vectors/storage.json).
        Assert.Equal("""{"width":320.0,"height":180.0,"duration":65.84,"display_aspect_ratio":[16,9],"audio":false,"video":true}""",
            RailsJson.Encode(BlobAnalyzer.VideoMetadata(probe)));
    }

    [Fact]
    public void A_video_turned_on_its_side_swaps_its_dimensions()
    {
        var probe = Probe("""
            {"streams":[{"codec_type":"video","width":1920,"height":1080,"side_data_list":[{"side_data_type":"Display Matrix","rotation":-90}]},
            {"codec_type":"audio"}],"format":{"duration":"3.5"}}
            """);

        Assert.Equal("""{"width":1080.0,"height":1920.0,"duration":3.5,"angle":-90,"audio":true,"video":true}""",
            RailsJson.Encode(BlobAnalyzer.VideoMetadata(probe)));
    }

    [Fact]
    public void A_rotate_tag_and_a_zero_aspect_ratio_are_handled_as_rails_handles_them()
    {
        var probe = Probe("""{"streams":[{"codec_type":"video","width":"640","height":480,"display_aspect_ratio":"0:1","tags":{"rotate":"90"}}],"format":{}}""");

        Assert.Equal("""{"width":480.0,"height":640.0,"angle":90,"audio":false,"video":true}""", RailsJson.Encode(BlobAnalyzer.VideoMetadata(probe)));
        Assert.Equal("""{"audio":false,"video":false}""", RailsJson.Encode(BlobAnalyzer.VideoMetadata([])));
    }

    [Fact]
    public void Audio_metadata_is_the_analyzers()
    {
        var probe = Probe("""{"streams":[{"codec_type":"audio","duration":"2.5","bit_rate":"128000","sample_rate":"44100","tags":{"title":"x"}}]}""");

        Assert.Equal("""{"duration":2.5,"bit_rate":128000,"sample_rate":44100,"tags":{"title":"x"}}""", RailsJson.Encode(BlobAnalyzer.AudioMetadata(probe)));
        Assert.Equal("{}", RailsJson.Encode(BlobAnalyzer.AudioMetadata([])));
    }

    [Fact]
    public void Analysis_keeps_existing_keys_in_place_and_marks_the_blob_analyzed()
    {
        var blob = StorageFixture.BlobFrom(StorageVectors.File.Messages[0].Blob) with
        {
            ContentType = "application/zip",
            Metadata = new JsonObject { ["identified"] = true, ["analyzed"] = false, ["custom"] = 1 },
        };

        Assert.Equal(AnalyzerKind.Null, BlobAnalyzer.For(blob));
        Assert.False(BlobAnalyzer.AnalyzesLater(blob));
        Assert.Equal("""{"identified":true,"analyzed":true,"custom":1}""", RailsJson.Encode(BlobAnalyzer.Analyze(blob, "")));
    }

    [Fact]
    public void Untrusted_loaders_are_blocked_as_vips_rb_blocks_them()
    {
        Toolchain.RequireLibVips();
        // The reference couldn't read pixel.bmp either: its analysis is {} (vectors/storage.json),
        // because the loader that reads BMP (ImageMagick's) is untrusted.
        Assert.Equal("{}", RailsJson.Encode(BlobAnalyzer.ImageMetadata(StorageFixture.Fixture("pixel.bmp"))));
        var output = Path.Combine(directory, "pixel.png");
        Assert.Throws<MediaException>(() => ImageTransformer.Transform(StorageFixture.Fixture("pixel.bmp"), Thumb("png"), output));
    }

    [Fact]
    public void A_failing_variant_reports_its_own_error_after_many_variants()
    {
        Toolchain.RequireLibVips();
        var jpeg = Path.Combine(directory, "moon.jpg");
        ImageTransformer.Transform(StorageFixture.Fixture("moon.jpg"), Thumb("jpg", 8), jpeg);
        for (var i = 0; i < 100; i++)
        {
            ImageTransformer.Transform(jpeg, Thumb("webp", 4), Path.Combine(directory, "small.webp"));
        }
        var corrupt = Path.Combine(directory, "corrupt.jpg");
        File.WriteAllBytes(corrupt, [0xff, 0xd8, 0xff, 0xe0, .. "not really a JPEG"u8]);

        var error = Assert.Throws<MediaException>(() => ImageTransformer.Transform(corrupt, Thumb("webp", 4), Path.Combine(directory, "corrupt.webp")));

        Assert.DoesNotContain("no property named", error.Message, StringComparison.Ordinal);
        Assert.Contains("jpeg", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_failing_ffmpeg_raises_a_preview_error_with_its_status_and_stderr()
    {
        Toolchain.RequireFfmpeg();
        var notVideo = Path.Combine(directory, "not-a-video.mov");
        File.WriteAllText(notVideo, "hello");
        using var output = new MemoryStream();

        var error = Assert.Throws<PreviewException>(() => VideoPreviewer.DrawRelevantFrame(notVideo, output));

        Assert.StartsWith("ffmpeg failed (status ", error.Message, StringComparison.Ordinal);
        Assert.Contains(notVideo, error.Message, StringComparison.Ordinal);
        Assert.False(error.Message.EndsWith('\n'));
    }

    [Fact]
    public void A_tool_that_overruns_its_timeout_is_killed()
    {
        var started = DateTime.UtcNow;

        var result = Subprocess.Run(["sleep", "30"], Stream.Null, TimeSpan.FromMilliseconds(200));

        Assert.True(result.TimedOut);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Subprocess_captures_stdout_and_stderr_without_a_shell()
    {
        using var output = new MemoryStream();

        var result = Subprocess.Run(["sh", "-c", "echo out; echo err >&2; exit 3"], output, null);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("out\n", System.Text.Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal("err\n", result.Stderr);
    }

    static Variation Thumb(string format, int size = 1200) =>
        new(new Transformations(("format", format), ("resize_to_limit", new object?[] { size, size })));

    static JsonObject Probe(string json) => (JsonObject)JsonNode.Parse(json)!;

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
