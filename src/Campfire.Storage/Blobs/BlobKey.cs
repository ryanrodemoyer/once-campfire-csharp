using System.Security.Cryptography;

namespace Campfire.Storage.Blobs;

/// <summary>Blob keys and checksums (activestorage/app/models/active_storage/blob.rb).</summary>
public static class BlobKey
{
    /// <summary><c>ActiveStorage::Blob::MINIMUM_TOKEN_LENGTH</c>.</summary>
    public const int Length = 28;

    const string base36 = "0123456789abcdefghijklmnopqrstuvwxyz";

    /// <summary><c>has_secure_token :key, length: 28</c>: <c>SecureRandom.base36(28)</c>.</summary>
    public static string Generate() => RandomNumberGenerator.GetString(base36, Length);

    // Active Storage's checksums are MD5, so matching them needs it.
#pragma warning disable CA5351
    /// <summary><c>compute_checksum_in_chunks</c>: <c>OpenSSL::Digest::MD5#base64digest</c> of the content.</summary>
    public static string Checksum(ReadOnlySpan<byte> data) => Convert.ToBase64String(MD5.HashData(data));

    /// <summary><see cref="Checksum(ReadOnlySpan{byte})"/> of a stream, read to its end.</summary>
    public static string Checksum(Stream stream) => Convert.ToBase64String(MD5.HashData(stream));
#pragma warning restore CA5351

    /// <summary><c>OpenSSL::Digest::MD5.file(path).base64digest</c>.</summary>
    public static string ChecksumFile(string path)
    {
        using var file = File.OpenRead(path);
        return Checksum(file);
    }
}
