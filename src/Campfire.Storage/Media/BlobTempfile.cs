using Campfire.Storage.Blobs;

namespace Campfire.Storage.Media;

/// <summary>
/// <c>blob.open(tmpdir:)</c>: a copy of the blob's file in a tempfile named
/// <c>ActiveStorage-&lt;id&gt;-&lt;random&gt;&lt;.ext&gt;</c>, its checksum verified, deleted on dispose.
/// libvips and ffmpeg see the same kind of path they see under Rails, extension included, which
/// some loaders and demuxers look at.
/// </summary>
public sealed class BlobTempfile : IDisposable
{
    BlobTempfile(string path) => Path = path;

    public string Path { get; }

    /// <summary>Downloads <paramref name="blob"/>; a mismatched checksum raises <see cref="BlobIntegrityException"/>.</summary>
    public static BlobTempfile Open(BlobStorage storage, Blob blob)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(blob);
        var tempfile = Create($"ActiveStorage-{blob.Id}-", blob.Filename.ExtensionWithDelimiter);
        try
        {
            using (var source = storage.Service.OpenRead(blob.Key))
            using (var target = new FileStream(tempfile.Path, FileMode.Truncate, FileAccess.Write))
            {
                source.CopyTo(target);
            }
            if (!blob.IsComposed && BlobKey.ChecksumFile(tempfile.Path) != blob.Checksum)
            {
                throw new BlobIntegrityException(blob.Key);
            }
            return tempfile;
        }
        catch
        {
            tempfile.Dispose();
            throw;
        }
    }

    /// <summary>
    /// <c>Tempfile.new([prefix, suffix])</c> in <c>Dir.tmpdir</c>: an empty file, created exclusively,
    /// with a random part between the prefix and the suffix.
    /// </summary>
    public static BlobTempfile Create(string prefix, string suffix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(suffix);
        while (true)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"{prefix}{DateTime.UtcNow:yyyyMMdd}-{Environment.ProcessId}-{Random.Shared.Next():x}{suffix}");
            try
            {
                using (new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                {
                }
                return new BlobTempfile(path);
            }
            catch (IOException) when (File.Exists(path))
            {
            }
        }
    }

    public void Dispose()
    {
        try
        {
            File.Delete(Path);
        }
        catch (IOException)
        {
        }
    }
}
