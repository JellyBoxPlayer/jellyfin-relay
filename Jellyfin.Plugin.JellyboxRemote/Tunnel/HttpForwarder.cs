using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Jellyfin.Plugin.JellyboxRemote.Tunnel;

internal sealed class HttpForwarder(HttpClient upstream, Uri target, string clientIp)
{
    private const int MaxHead = 64 * 1024;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(90);

    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection", "keep-alive", "proxy-authenticate", "proxy-authorization", "proxy-connection",
        "te", "trailer", "transfer-encoding", "upgrade", "host", "content-length",
        "x-forwarded-for", "x-forwarded-proto", "x-forwarded-host", "x-real-ip", "forwarded",
    };

    public async Task RunAsync(Stream app, CancellationToken cancellationToken)
    {
        var reader = new HeadReader(app);
        while (true)
        {
            RequestHead head;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                idle.CancelAfter(IdleTimeout);
                var raw = await reader.ReadHeadAsync(idle.Token).ConfigureAwait(false);
                if (raw is null)
                {
                    return;
                }

                head = RequestHead.Parse(raw);
            }

            var keepAlive = await ForwardAsync(head, reader, app, cancellationToken).ConfigureAwait(false);
            if (!keepAlive)
            {
                return;
            }
        }
    }

    private async Task<bool> ForwardAsync(RequestHead head, HeadReader reader, Stream app, CancellationToken cancellationToken)
    {
        if (head.Chunked)
        {
            await WriteErrorAsync(app, 411, "Length Required", cancellationToken).ConfigureAwait(false);
            return false;
        }

        using var request = new HttpRequestMessage(new HttpMethod(head.Method), new Uri(target.GetLeftPart(UriPartial.Path).TrimEnd('/') + head.Target));
        Stream? body = null;
        if (head.ContentLength > 0)
        {
            body = new BoundedStream(reader, head.ContentLength);
            request.Content = new StreamContent(body);
            request.Content.Headers.ContentLength = head.ContentLength;
        }

        foreach (var (name, value) in head.Headers)
        {
            if (HopByHop.Contains(name))
            {
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                request.Content?.Headers.TryAddWithoutValidation(name, value);
            }
        }

        request.Headers.TryAddWithoutValidation("X-Forwarded-For", clientIp);
        request.Headers.TryAddWithoutValidation("X-Real-IP", clientIp);
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        if (head.Host is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-Host", head.Host);
        }

        HttpResponseMessage response;
        try
        {
            response = await upstream.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            await WriteErrorAsync(app, 502, "Bad Gateway", cancellationToken).ConfigureAwait(false);
            return false;
        }

        using (response)
        {
            if (body is not null)
            {
                await ((BoundedStream)body).DrainAsync(cancellationToken).ConfigureAwait(false);
            }

            var noBody = head.Method == "HEAD" || (int)response.StatusCode is 204 or 304 || (int)response.StatusCode < 200;
            var length = response.Content.Headers.ContentLength;
            var chunked = !noBody && length is null;

            var builder = new StringBuilder();
            builder.Append("HTTP/1.1 ").Append((int)response.StatusCode).Append(' ')
                .Append(response.ReasonPhrase ?? ((HttpStatusCode)response.StatusCode).ToString()).Append("\r\n");
            Append(builder, response.Headers);
            Append(builder, response.Content.Headers);
            if (!noBody && length is not null)
            {
                builder.Append("Content-Length: ").Append(length.Value.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            }

            if (chunked)
            {
                builder.Append("Transfer-Encoding: chunked\r\n");
            }

            builder.Append(head.Close ? "Connection: close\r\n" : "Connection: keep-alive\r\n").Append("\r\n");
            await app.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString()), cancellationToken).ConfigureAwait(false);

            if (!noBody)
            {
                var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (content.ConfigureAwait(false))
                {
                    if (chunked)
                    {
                        await CopyChunkedAsync(content, app, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await content.CopyToAsync(app, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            await app.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return !head.Close;
    }

    private static void Append(StringBuilder builder, HttpHeaders headers)
    {
        foreach (var (name, values) in headers.NonValidated)
        {
            if (HopByHop.Contains(name))
            {
                continue;
            }

            foreach (var value in values)
            {
                builder.Append(name).Append(": ").Append(value).Append("\r\n");
            }
        }
    }

    private static async Task CopyChunkedAsync(Stream from, Stream to, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await to.WriteAsync(Encoding.ASCII.GetBytes(read.ToString("x", CultureInfo.InvariantCulture) + "\r\n"), cancellationToken).ConfigureAwait(false);
                await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                await to.WriteAsync("\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            }

            await to.WriteAsync("0\r\n\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static Task WriteErrorAsync(Stream app, int status, string reason, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes("{\"error\":\"" + reason.ToLowerInvariant() + "\"}");
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        return app.WriteAsync(Encoding.ASCII.GetBytes(head).Concat(body).ToArray(), cancellationToken).AsTask();
    }

    private sealed record RequestHead(
        string Method,
        string Target,
        string? Host,
        List<(string Name, string Value)> Headers,
        long ContentLength,
        bool Chunked,
        bool Close)
    {
        public static RequestHead Parse(string raw)
        {
            var lines = raw.Split("\r\n");
            var parts = lines[0].Split(' ', 3);
            if (parts.Length != 3 || !parts[1].StartsWith('/'))
            {
                throw new InvalidDataException("malformed request line");
            }

            var headers = new List<(string, string)>();
            string? host = null;
            long length = 0;
            var chunked = false;
            var close = parts[2] == "HTTP/1.0";
            foreach (var line in lines.Skip(1))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0)
                {
                    throw new InvalidDataException("malformed header");
                }

                var name = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                headers.Add((name, value));

                if (name.Equals("host", StringComparison.OrdinalIgnoreCase))
                {
                    host = value;
                }
                else if (name.Equals("content-length", StringComparison.OrdinalIgnoreCase))
                {
                    length = long.Parse(value, CultureInfo.InvariantCulture);
                }
                else if (name.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase))
                {
                    chunked = value.Contains("chunked", StringComparison.OrdinalIgnoreCase);
                }
                else if (name.Equals("connection", StringComparison.OrdinalIgnoreCase))
                {
                    close = value.Contains("close", StringComparison.OrdinalIgnoreCase);
                }
            }

            return new RequestHead(parts[0], parts[1], host, headers, length, chunked, close);
        }
    }

    private sealed class HeadReader(Stream inner)
    {
        private readonly byte[] _buffer = new byte[MaxHead];
        private int _filled;

        public async Task<string?> ReadHeadAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var end = _buffer.AsSpan(0, _filled).IndexOf("\r\n\r\n"u8);
                if (end >= 0)
                {
                    var head = Encoding.ASCII.GetString(_buffer, 0, end);
                    var consumed = end + 4;
                    _buffer.AsSpan(consumed, _filled - consumed).CopyTo(_buffer);
                    _filled -= consumed;
                    return head;
                }

                if (_filled == _buffer.Length)
                {
                    throw new InvalidDataException("request head too large");
                }

                var read = await inner.ReadAsync(_buffer.AsMemory(_filled), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    if (_filled > 0)
                    {
                        throw new InvalidDataException("connection closed mid-request");
                    }

                    return null;
                }

                _filled += read;
            }
        }

        public async ValueTask<int> ReadBodyAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            if (_filled > 0)
            {
                var count = Math.Min(destination.Length, _filled);
                _buffer.AsMemory(0, count).CopyTo(destination);
                _buffer.AsSpan(count, _filled - count).CopyTo(_buffer);
                _filled -= count;
                return count;
            }

            return await inner.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class BoundedStream(HeadReader reader, long length) : Stream
    {
        private readonly long _length = length;
        private long _remaining = length;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {
            get => _length - _remaining;
            set => throw new NotSupportedException();
        }

        public async Task DrainAsync(CancellationToken cancellationToken)
        {
            var scratch = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                while (_remaining > 0 && await ReadAsync(scratch, cancellationToken).ConfigureAwait(false) > 0)
                {
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(scratch);
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining == 0)
            {
                return 0;
            }

            var want = (int)Math.Min(buffer.Length, _remaining);
            var read = await reader.ReadBodyAsync(buffer[..want], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException("connection closed mid-body");
            }

            _remaining -= read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
