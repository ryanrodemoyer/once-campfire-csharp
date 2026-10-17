using System.Buffers;

namespace Campfire.Templates;

/// <summary>
/// An <see cref="IBufferWriter{T}"/> that rents from <see cref="ArrayPool{T}.Shared"/>
/// and returns the buffer on <see cref="Dispose"/>. Use for rendering page bodies that are
/// read once and discarded; avoids the repeated resizes of <see cref="ArrayBufferWriter{T}"/>
/// when the output is large.
/// <para>
/// Typical page bodies are 30-460 KB, so the initial capacity is set to avoid most resizes.
/// </para>
/// </summary>
public sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
{
    const int initialCapacity = 65536; // 64 KiB — covers messages page, grows for room page

    byte[] buffer;
    int written;

    /// <summary>
    /// Overwrite what was written with 0xFF on <see cref="Dispose"/>. Tests turn this on, so that
    /// anything still reading <see cref="WrittenMemory"/> after the buffer went back to the pool
    /// sees invalid UTF-8 at once. In production it would instead race whichever request rents
    /// the array next, and fail about once in 300,000 responses.
    /// </summary>
    public static bool PoisonOnDispose { get; set; }

    public PooledBufferWriter()
    {
        buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
    }

    /// <summary>The bytes written so far. Valid until <see cref="Dispose"/>; copy them to keep them.</summary>
    public ReadOnlySpan<byte> WrittenSpan => buffer.AsSpan(0, written);

    /// <summary>
    /// The bytes written so far. Valid until <see cref="Dispose"/>: never return this from the
    /// <c>using</c> scope that owns the writer. Return <c>WrittenMemory.ToArray()</c> instead.
    /// </summary>
    public ReadOnlyMemory<byte> WrittenMemory => buffer.AsMemory(0, written);

    public void Advance(int count)
    {
        if (count < 0 || written + count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
        written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return buffer.AsMemory(written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return buffer.AsSpan(written);
    }

    void EnsureCapacity(int sizeHint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        var needed = written + Math.Max(sizeHint, 1);
        if (needed <= buffer.Length)
        {
            return;
        }
        var newSize = Math.Max(needed, buffer.Length * 2);
        var newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
        buffer.AsSpan(0, written).CopyTo(newBuffer);
        ArrayPool<byte>.Shared.Return(buffer);
        buffer = newBuffer;
    }

    public void Dispose()
    {
        if (buffer.Length > 0)
        {
            if (PoisonOnDispose)
            {
                buffer.AsSpan(0, written).Fill(0xFF);
            }
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = [];
        }
    }
}
