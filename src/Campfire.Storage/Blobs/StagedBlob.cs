namespace Campfire.Storage.Blobs;

/// <summary>
/// A new blob whose file is already in the service but whose row may not be committed yet.
/// Disposing it deletes the file unless the row was committed (<see cref="BlobStorage"/> marks it
/// kept after commit), so a rolled-back or abandoned upload leaves no orphan file behind.
/// </summary>
public sealed class StagedBlob : IDisposable
{
    readonly DiskService service;

    internal StagedBlob(DiskService service, NewBlob blob)
    {
        this.service = service;
        Blob = blob;
    }

    public NewBlob Blob { get; }

    public bool Kept { get; private set; }

    /// <summary>The row is committed: keep the file.</summary>
    public void Keep() => Kept = true;

    public void Dispose()
    {
        if (!Kept)
        {
            service.Delete(Blob.Key);
        }
    }
}
