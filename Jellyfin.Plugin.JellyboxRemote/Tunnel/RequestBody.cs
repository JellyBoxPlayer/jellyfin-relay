using System.Threading.Channels;

namespace Jellyfin.Plugin.JellyboxRemote.Tunnel;

internal sealed class RequestBody(ChannelReader<ReadOnlyMemory<byte>> chunks, Action<int> consumed) : Stream
{
    private ReadOnlyMemory<byte> _current;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_current.IsEmpty)
        {
            if (!await chunks.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            chunks.TryRead(out _current);
        }

        var count = Math.Min(buffer.Length, _current.Length);
        _current[..count].CopyTo(buffer);
        _current = _current[count..];
        consumed(count);
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
