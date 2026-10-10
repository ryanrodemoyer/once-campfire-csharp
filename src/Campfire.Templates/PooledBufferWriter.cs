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

    public PooledBufferWriter()
    {
        buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
    }

    public ReadOnlySpan<byte> WrittenSpan => buffer.AsSpan(0, written);

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
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = [];
        }
    }
}
