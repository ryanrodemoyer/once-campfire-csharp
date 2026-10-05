namespace Campfire.RailsCompat.Params;

/// <summary>
/// <c>ActionDispatch::Http::UploadedFile</c>: a multipart file part spooled to a temp file. Disposing
/// it deletes the file, as <c>Rack::TempfileReaper</c> does once the request is done.
/// </summary>
public sealed class UploadedFile : IDisposable
{
    public UploadedFile(string originalFilename, string? contentType, string headers, string path, long size)
    {
        OriginalFilename = originalFilename;
        ContentType = contentType;
        Headers = headers;
        Path = path;
        Size = size;
    }

    public string OriginalFilename { get; }

    public string? ContentType { get; }

    /// <summary>The part's raw header lines (<c>UploadedFile#headers</c>).</summary>
    public string Headers { get; }

    public string Path { get; }

    public long Size { get; }

    /// <summary>Spools <paramref name="bytes"/> to a temp file; for tests and raw-body uploads.</summary>
    public static UploadedFile FromBytes(string originalFilename, string? contentType, ReadOnlySpan<byte> bytes, string? directory = null)
    {
        var path = TempFiles.Create(directory, originalFilename);
        File.WriteAllBytes(path, bytes.ToArray());
        return new UploadedFile(originalFilename, contentType, "", path, bytes.Length);
    }

    public FileStream OpenRead() => new(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);

    public byte[] ReadAllBytes() => File.ReadAllBytes(Path);

    public void Dispose() => File.Delete(Path);

    public override string ToString() => $"#<ActionDispatch::Http::UploadedFile {OriginalFilename}>";
}

static class TempFiles
{
    /// <summary>
    /// Creates an empty temp file named like Rack's (<c>RackMultipart...</c>, keeping the upload's
    /// extension) and returns its path.
    /// </summary>
    public static string Create(string? directory, string filename)
    {
        directory ??= System.IO.Path.GetTempPath();
        var extension = SafeExtension(filename);
        while (true)
        {
            var path = System.IO.Path.Combine(directory, $"RackMultipart{Guid.NewGuid():N}{extension}");
            try
            {
                using (new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                {
                    return path;
                }
            }
            catch (IOException) when (File.Exists(path))
            {
                // Name collision; try another.
            }
        }
    }

    // Rack keeps File.extname(filename)[0, 129]; only plain extensions are kept here, since the
    // temp path is never shown to anyone.
    static string SafeExtension(string filename)
    {
        var extension = System.IO.Path.GetExtension(filename.Replace("\0", "%00", StringComparison.Ordinal));
        if (extension.Length > 129)
        {
            extension = extension[..129];
        }
        return extension.Skip(1).All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? extension : "";
    }
}
