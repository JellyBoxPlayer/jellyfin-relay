using System.Threading.Channels;

namespace Jellyfin.Plugin.JellyboxRemote.Tunnel;

internal sealed class MuxStream(uint id, TunnelSession session) : Stream
{
    private const int GrantBatch = 16 * 1024;

    private readonly Channel<ReadOnlyMemory<byte>> _incoming = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private readonly FlowWindow _sendWindow = new(Frame.InitialWindow);
    private ReadOnlyMemory<byte> _current;
    private long _received;
    private long _granted;
    private int _ungranted;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public bool Deliver(ReadOnlyMemory<byte> data)
    {
        if (Interlocked.Add(ref _received, data.Length) - Interlocked.Read(ref _granted) > Frame.InitialWindow)
        {
            return false;
        }

        _incoming.Writer.TryWrite(data);
        return true;
    }

    public void Finish() => _incoming.Writer.TryComplete();

    public void Grant(int bytes) => _sendWindow.Grant(bytes);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_current.IsEmpty)
        {
            if (!await _incoming.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            _incoming.Reader.TryRead(out _current);
        }

        var count = Math.Min(buffer.Length, _current.Length);
        _current[..count].CopyTo(buffer);
        _current = _current[count..];
        Acknowledge(count);
        return count;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var wanted = Math.Min(buffer.Length - offset, Frame.MaxData);
            var allowed = await _sendWindow.TakeAsync(wanted, cancellationToken).ConfigureAwait(false);
            session.Send(Frame.Encode(FrameType.Data, id, buffer.Span.Slice(offset, allowed)));
            offset += allowed;
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    private void Acknowledge(int count)
    {
        _ungranted += count;
        if (_ungranted < GrantBatch && !_current.IsEmpty)
        {
            return;
        }

        var bytes = _ungranted;
        _ungranted = 0;
        Interlocked.Add(ref _granted, bytes);
        session.Send(Frame.EncodeWindow(id, bytes));
    }
}
