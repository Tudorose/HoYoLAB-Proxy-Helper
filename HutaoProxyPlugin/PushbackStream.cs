using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace HutaoProxyPlugin;

/// <summary>Reads from a leftover buffer first, then from the underlying stream.</summary>
internal sealed class PushbackStream : Stream
{
    private readonly byte[] leftover;
    private int position;
    private readonly Stream inner;

    public PushbackStream(byte[] leftover, Stream inner)
    {
        this.leftover = leftover;
        this.inner = inner;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (position < leftover.Length)
        {
            int n = Math.Min(count, leftover.Length - position);
            Array.Copy(leftover, position, buffer, offset, n);
            position += n;
            return n;
        }
        return await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
    }
}
