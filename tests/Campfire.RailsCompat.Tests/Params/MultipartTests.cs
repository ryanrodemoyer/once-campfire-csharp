using System.Text;
using Campfire.RailsCompat.Params;

namespace Campfire.RailsCompat.Tests.Params;

public class MultipartTests
{
    const string boundary = "XyZ";

    static byte[] Body(params (string Headers, string Content)[] parts)
    {
        var body = new StringBuilder();
        foreach (var (headers, content) in parts)
        {
            body.Append($"--{boundary}\r\n{headers}\r\n\r\n{content}\r\n");
        }
        body.Append($"--{boundary}--\r\n");
        return Encoding.UTF8.GetBytes(body.ToString());
    }

    static Task<ParsedBody> Parse(byte[] body, Stream? stream = null, string contentType = $"multipart/form-data; boundary={boundary}", bool withLength = true) =>
        RequestBody.ParseAsync("POST", contentType, withLength ? body.Length : null, stream ?? new MemoryStream(body), cancellationToken: TestContext.Current.CancellationToken);

    static string Field(string name) => $"Content-Disposition: form-data; name=\"{name}\"";

    [Fact]
    public async Task FieldsAndFiles()
    {
        var body = Body(
            (Field("_method"), "patch"),
            (Field("user[name]"), "Jo"),
            ("Content-Disposition: form-data; name=\"user[avatar]\"; filename=\"me.png\"\r\nContent-Type: image/png", "PNGDATA"),
            ("Content-Disposition: form-data; name=\"user[empty]\"; filename=\"\"", ""),
            (Field("tags[]"), "a"),
            (Field("tags[]"), "b"));
        using var parsed = await Parse(body);

        var parameters = parsed.Params;
        Assert.Equal("patch", parameters.GetString("_method"));
        var user = parameters.GetHash("user")!;
        Assert.Equal("Jo", user.GetString("name"));
        Assert.False(user.ContainsKey("empty"));
        var avatar = user.GetFile("avatar")!;
        Assert.Equal("me.png", avatar.OriginalFilename);
        Assert.Equal("image/png", avatar.ContentType);
        Assert.Equal(7, avatar.Size);
        Assert.Equal("PNGDATA"u8.ToArray(), avatar.ReadAllBytes());
        Assert.Contains("Content-Type: image/png\r\n", avatar.Headers, StringComparison.Ordinal);
        Assert.StartsWith("RackMultipart", Path.GetFileName(avatar.Path), StringComparison.Ordinal);
        Assert.EndsWith(".png", avatar.Path, StringComparison.Ordinal);
        Assert.Equal("""["a","b"]""", parameters.ToJson()["tags"]!.ToJsonString());
        Assert.Empty(parsed.Raw);
    }

    [Fact]
    public async Task DisposingTheBodyDeletesItsFiles()
    {
        string path;
        using (var parsed = await Parse(Body(("Content-Disposition: form-data; name=\"f\"; filename=\"a.txt\"", "x"))))
        {
            path = parsed.Params.GetFile("f")!.Path;
            Assert.True(File.Exists(path));
        }
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("""form-data; name="attachment"; filename="C:\Users\me\cat.png" """, "attachment", "cat.png")]
    [InlineData("""form-data; name="a"; filename="with \"quotes\".txt" """, "a", "with \"quotes\".txt")]
    [InlineData("form-data; name=file; filename*=UTF-8''r%C3%A9sum%C3%A9.pdf", "file", "résumé.pdf")]
    [InlineData("form-data; name=file; filename*=ISO-8859-1''caf%E9.txt", "file", "café.txt")]
    [InlineData("""form-data; name="a"; filename="100%.txt" """, "a", "100%.txt")]
    [InlineData("""form-data; name="a"; filename="caf%C3%A9.txt" """, "a", "café.txt")]
    [InlineData("""form-data; name="a"; filename="dir/" """, "a", "dir")]
    [InlineData("""form-data; filename="named.txt" """, "named.txt", "named.txt")]
    public async Task Dispositions(string disposition, string name, string filename)
    {
        using var parsed = await Parse(Body(($"Content-Disposition: {disposition.TrimEnd()}", "x")));
        Assert.Equal(filename, parsed.Params.GetFile(name)!.OriginalFilename);
    }

    [Fact]
    public async Task NamelessTextPartsAreNamedAfterTheirType()
    {
        using var parsed = await Parse(Body(("Content-Type: text/csv", "a,b"), ("Content-ID: <part2>", "c")));
        Assert.Equal("""{"text/csv":["a,b"],"<part2>":"c"}""", parsed.Params.ToString());
    }

    [Fact]
    public async Task FoldedHeadersAreUnfolded()
    {
        using var parsed = await Parse(Body(
            ("Content-Disposition: form-data;\r\n name=\"a\"; filename=\"f.txt\"\r\nContent-Type: \r\n text/plain", "x"),
            ("Content-Disposition: form-data; name=\"b\"; filename=\"f.txt\"\r\nContent-Type:\r\n text/plain", "x")));
        Assert.Equal("text/plain", parsed.Params.GetFile("a")!.ContentType);
        // Without whitespace before the fold, the fold's whitespace is part of the value.
        Assert.Equal(" text/plain", parsed.Params.GetFile("b")!.ContentType);
    }

    [Fact]
    public async Task TextPartsInOtherCharsets()
    {
        var body = Encoding.Latin1.GetBytes($"--{boundary}\r\n{Field("a")}\r\nContent-Type: text/plain; charset=\"ISO-8859-1\"\r\n\r\ncaf\u00e9\r\n--{boundary}--\r\n");
        using var parsed = await Parse(body);
        Assert.Equal("café", parsed.Params.GetString("a"));

        var invalid = Encoding.Latin1.GetBytes($"--{boundary}\r\n{Field("a")}\r\n\r\ncaf\u00e9\r\n--{boundary}--\r\n");
        using var utf8 = await Parse(invalid);
        Assert.Equal(ParamErrorKind.Invalid, utf8.Error!.Kind);
    }

    [Fact]
    public async Task PreambleAndEpilogueAreIgnored()
    {
        var body = Encoding.UTF8.GetBytes($"preamble\r\n--{boundary}\r\n{Field("a")}\r\n\r\n1\r\n--{boundary}--\r\nepilogue");
        using var parsed = await Parse(body);
        Assert.Equal("""{"a":"1"}""", parsed.Params.ToString());
    }

    [Fact]
    public async Task OnlyAnEndBoundaryIsEmpty()
    {
        using var parsed = await Parse(Encoding.UTF8.GetBytes($"--{boundary}--\r\n"));
        Assert.Equal("{}", parsed.Params.ToString());
    }

    [Fact]
    public async Task BoundariesSplitAcrossReadsAreFound()
    {
        var file = string.Concat(Enumerable.Repeat("0123456789\r\n-", 1000));
        var body = Body(("Content-Disposition: form-data; name=\"f\"; filename=\"f.bin\"", file), (Field("a"), "1"));
        using var parsed = await Parse(body, new TrickleStream(body, 7));
        Assert.Equal(Encoding.UTF8.GetBytes(file), parsed.Params.GetFile("f")!.ReadAllBytes());
        Assert.Equal("1", parsed.Params.GetString("a"));
    }

    [Fact]
    public async Task ChunkedBodiesWithoutALength()
    {
        var body = Body((Field("a"), "1"));
        using var parsed = await Parse(body, withLength: false);
        Assert.Equal("""{"a":"1"}""", parsed.Params.ToString());
    }

    [Fact]
    public async Task TruncatedBodiesAreErrors()
    {
        var body = Body(("Content-Disposition: form-data; name=\"f\"; filename=\"f.bin\"", "data"));
        var truncated = body[..^10];
        using var parsed = await RequestBody.ParseAsync("POST", $"multipart/form-data; boundary={boundary}", body.Length, new MemoryStream(truncated), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ParamErrorKind.Parse, parsed.Error!.Kind);
        using var unbounded = await Parse(truncated, withLength: false);
        Assert.Equal(ParamErrorKind.Parse, unbounded.Error!.Kind);
    }

    [Fact]
    public async Task FailedParsesLeaveNoTempFiles()
    {
        var directory = Directory.CreateTempSubdirectory("campfire-multipart-");
        try
        {
            var body = Body(("Content-Disposition: form-data; name=\"f\"; filename=\"f.bin\"", "data"))[..^10];
            using var parsed = await RequestBody.ParseAsync("POST", $"multipart/form-data; boundary={boundary}", null, new MemoryStream(body), directory.FullName, cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(parsed.Error);
            Assert.Empty(directory.GetFiles());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PartLimits()
    {
        // Rack raises when the count reaches the limit, so 127 files fit and 128 don't.
        var files = Enumerable.Range(0, MultipartParser.FileLimit).Select(i => ($"Content-Disposition: form-data; name=\"f[]\"; filename=\"{i}.txt\"", "x")).ToArray();
        using var fits = await Parse(Body(files[1..]));
        Assert.Equal(MultipartParser.FileLimit - 1, fits.Params.GetArray("f")!.Count);
        using var tooMany = await Parse(Body(files));
        Assert.Equal(ParamErrorKind.Limit, tooMany.Error!.Kind);

        var fields = Enumerable.Repeat((Field("a[]"), "x"), MultipartParser.TotalPartLimit).ToArray();
        using var tooManyParts = await Parse(Body(fields));
        Assert.Equal(ParamErrorKind.Limit, tooManyParts.Error!.Kind);
    }

    [Fact]
    public async Task TextFieldsAreCappedTogether()
    {
        var half = new string('x', MultipartParser.BufferedUploadByteSizeLimit / 2);
        var file = "Content-Disposition: form-data; name=\"f\"; filename=\"x\"";
        using var fits = await Parse(Body((Field("a"), half[..^1000]), (Field("b"), half[..^1000]), (file, half)));
        Assert.Null(fits.Error);
        using var over = await Parse(Body((Field("a"), half), (Field("b"), half)));
        Assert.Equal(ParamErrorKind.Parse, over.Error!.Kind);
    }

    [Theory]
    [InlineData("multipart/form-data; boundary =x")]
    [InlineData("multipart/form-data; boundary=x; boundary=y")]
    public async Task MalformedBoundariesAreErrors(string contentType)
    {
        using var parsed = await Parse(Body((Field("a"), "1")), contentType: contentType);
        Assert.Equal(ParamErrorKind.Parse, parsed.Error!.Kind);
    }

    [Fact]
    public async Task BoundariesLongerThan70AreErrors()
    {
        var tooLong = new string('b', 71);
        using var parsed = await Parse("--"u8.ToArray(), contentType: $"multipart/form-data; boundary={tooLong}");
        Assert.Equal(ParamErrorKind.Parse, parsed.Error!.Kind);
    }

    [Fact]
    public async Task WithoutABoundaryTheBodyIsReadAsAForm()
    {
        using var parsed = await Parse("a=1"u8.ToArray(), contentType: "multipart/form-data");
        Assert.Equal("""{"a":"1"}""", parsed.Params.ToString());
    }

    [Fact]
    public async Task ADispositionWithoutParametersCrashesLikeRack()
    {
        // Rack calls nil + 1 here: a 500 in Rails.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Parse(Body(("Content-Disposition: form-data", "1"))));
    }

    /// <summary>Returns at most <c>chunk</c> bytes per read.</summary>
    sealed class TrickleStream(byte[] data, int chunk) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, chunk));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], cancellationToken);
    }
}

/// <summary>
/// Measures allocations, so it runs alone: other tests allocating at the same time would count.
/// <c>GC.GetTotalAllocatedBytes</c> still includes the test host, so the bound leaves room for that.
/// </summary>
[CollectionDefinition(DisableParallelization = true)]
public class AllocationSensitive;

[Collection(typeof(AllocationSensitive))]
public class MultipartStreamingTests
{
    const string boundary = "XyZ";

    /// <summary>
    /// A 64 MB upload, generated as it's read: the parser must stream it to disk rather than hold
    /// it, so it allocates a small fraction of its size.
    /// </summary>
    [Fact]
    public async Task UploadsStreamWithoutBufferingTheBody()
    {
        const long fileSize = 64L * 1024 * 1024;
        var head = Encoding.ASCII.GetBytes($"--{boundary}\r\nContent-Disposition: form-data; name=\"f\"; filename=\"big.bin\"\r\n\r\n");
        var tail = Encoding.ASCII.GetBytes($"\r\n--{boundary}--\r\n");
        var stream = new GeneratedBodyStream(head, fileSize, tail);

        var total = GC.GetTotalAllocatedBytes(precise: true);
        using var parsed = await RequestBody.ParseAsync("POST", $"multipart/form-data; boundary={boundary}", stream.Length, stream, cancellationToken: TestContext.Current.CancellationToken);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - total;

        var file = parsed.Params.GetFile("f")!;
        Assert.Equal(fileSize, file.Size);
        Assert.Equal(fileSize, new FileInfo(file.Path).Length);
        // Buffering the body would allocate at least fileSize. Half of that still proves streaming,
        // and stays above the host noise that made fileSize/8 flake in a full bin/check (see #10).
        Assert.True(allocated < fileSize / 2, $"allocated {allocated:N0} bytes for a {fileSize:N0} byte upload");
    }

    /// <summary>A body of head, then <c>size</c> filler bytes, then tail, made up as it's read.</summary>
    sealed class GeneratedBodyStream(byte[] head, long size, byte[] tail) : Stream
    {
        long position;

        public override long Length => head.Length + size + tail.Length;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var written = 0;
            while (written < buffer.Length && position < Length)
            {
                var span = buffer[written..];
                int n;
                if (position < head.Length)
                {
                    n = Math.Min(span.Length, head.Length - (int)position);
                    head.AsSpan((int)position, n).CopyTo(span);
                }
                else if (position < head.Length + size)
                {
                    n = (int)Math.Min(span.Length, head.Length + size - position);
                    span[..n].Fill((byte)'z');
                }
                else
                {
                    var at = (int)(position - head.Length - size);
                    n = Math.Min(span.Length, tail.Length - at);
                    tail.AsSpan(at, n).CopyTo(span);
                }
                written += n;
                position += n;
            }
            return written;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(Read(buffer.Span));

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Position { get => position; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
