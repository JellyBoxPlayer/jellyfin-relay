using System.Buffers.Binary;

namespace Jellyfin.Plugin.JellyboxRemote.Tunnel;

internal enum FrameType : byte
{
    Request = 1,
    Response = 2,
    Data = 3,
    End = 4,
    Reset = 5,
    Window = 6,
}

internal readonly record struct Frame(FrameType Type, uint Stream, ReadOnlyMemory<byte> Payload)
{
    public const int HeaderSize = 5;
    public const int MaxData = 64 * 1024;
    public const int InitialWindow = 256 * 1024;

    public static byte[] Encode(FrameType type, uint stream, ReadOnlySpan<byte> payload = default)
    {
        var buffer = new byte[HeaderSize + payload.Length];
        buffer[0] = (byte)type;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(1), stream);
        payload.CopyTo(buffer.AsSpan(HeaderSize));
        return buffer;
    }

    public static byte[] EncodeWindow(uint stream, int bytes)
    {
        Span<byte> payload = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)bytes);
        return Encode(FrameType.Window, stream, payload);
    }

    public static bool TryDecode(ReadOnlyMemory<byte> message, out Frame frame)
    {
        frame = default;
        if (message.Length < HeaderSize || message.Span[0] is < 1 or > 6)
        {
            return false;
        }

        var stream = BinaryPrimitives.ReadUInt32BigEndian(message.Span[1..]);
        frame = new Frame((FrameType)message.Span[0], stream, message[HeaderSize..]);
        return true;
    }

    public int WindowBytes => (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(Payload.Span), int.MaxValue);
}
