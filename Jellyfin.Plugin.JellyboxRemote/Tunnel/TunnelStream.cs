using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace Jellyfin.Plugin.JellyboxRemote.Tunnel;

internal sealed record RequestHead(
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("headers")] List<List<string>> Headers);

internal sealed record ResponseHead(
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("headers")] List<List<string>> Headers);

internal sealed class TunnelStream : IDisposable
{
    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(60);

    private readonly uint _id;
    private readonly RequestHead _head;
    private readonly TunnelSession _session;
    private readonly HttpClient _client;
    private readonly Uri _target;
    private readonly Channel<ReadOnlyMemory<byte>> _body = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private readonly TaskCompletionSource<bool> _bodyStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly FlowWindow _sendWindow = new(Frame.InitialWindow);
    private readonly CancellationTokenSource _cancel = new();
    private long _received;
    private long _granted;
    private bool _responded;

    public TunnelStream(uint id, RequestHead head, TunnelSession session, HttpClient client, Uri target)
    {
        _id = id;
        _head = head;
        _session = session;
        _client = client;
        _target = target;
    }

    public void OnData(ReadOnlyMemory<byte> data)
    {
        if (Interlocked.Add(ref _received, data.Length) - Interlocked.Read(ref _granted) > Frame.InitialWindow)
        {
            _session.Send(Frame.Encode(FrameType.Reset, _id, "flow control window exceeded"u8));
            Cancel();
            return;
        }

        _bodyStarted.TrySetResult(true);
        _body.Writer.TryWrite(data);
    }

    public void OnEnd()
    {
        _bodyStarted.TrySetResult(false);
        _body.Writer.TryComplete();
    }

    public void OnWindow(int bytes) => _sendWindow.Grant(bytes);

    public void Cancel()
    {
        try
        {
            _cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _body.Writer.TryComplete();
    }

    public void Dispose() => _cancel.Dispose();

    public async Task RunAsync()
    {
        var token = _cancel.Token;
        try
        {
            var hasBody = await _bodyStarted.Task.WaitAsync(token).ConfigureAwait(false);
            using var request = BuildRequest(hasBody);
            using var response = await SendAsync(request, token).ConfigureAwait(false);

            _responded = true;
            var head = new ResponseHead((int)response.StatusCode, ResponseHeaders(response));
            _session.Send(Frame.Encode(FrameType.Response, _id, JsonSerializer.SerializeToUtf8Bytes(head)));

            if (request.Method != HttpMethod.Head)
            {
                await PumpAsync(response.Content, token).ConfigureAwait(false);
            }

            _session.Send(Frame.Encode(FrameType.End, _id));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            if (_responded)
            {
                _session.Send(Frame.Encode(FrameType.Reset, _id, Encoding.UTF8.GetBytes("upstream: " + e.Message)));
            }
            else
            {
                var error = JsonSerializer.SerializeToUtf8Bytes(new { error = "server unreachable: " + e.Message });
                var head = new ResponseHead(502, [["content-type", "application/json"], ["content-length", error.Length.ToString(CultureInfo.InvariantCulture)]]);
                _session.Send(Frame.Encode(FrameType.Response, _id, JsonSerializer.SerializeToUtf8Bytes(head)));
                _session.Send(Frame.Encode(FrameType.Data, _id, error));
                _session.Send(Frame.Encode(FrameType.End, _id));
            }
        }
        finally
        {
            _session.Forget(_id);
            Dispose();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var headers = CancellationTokenSource.CreateLinkedTokenSource(token);
        headers.CancelAfter(HeaderTimeout);
        try
        {
            return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("no response headers within " + HeaderTimeout.TotalSeconds + "s");
        }
    }

    private HttpRequestMessage BuildRequest(bool hasBody)
    {
        var uri = new Uri(_target.GetLeftPart(UriPartial.Path).TrimEnd('/') + _head.Path);
        var request = new HttpRequestMessage(new HttpMethod(_head.Method), uri);

        if (hasBody)
        {
            request.Content = new StreamContent(new RequestBody(_body.Reader, Grant));
        }

        foreach (var pair in _head.Headers)
        {
            if (pair.Count != 2 || !Headers.Forwardable(pair[0]))
            {
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(pair[0], pair[1]))
            {
                request.Content?.Headers.TryAddWithoutValidation(pair[0], pair[1]);
            }
        }

        return request;
    }

    private void Grant(int bytes)
    {
        Interlocked.Add(ref _granted, bytes);
        _session.Send(Frame.EncodeWindow(_id, bytes));
    }

    private async Task PumpAsync(HttpContent content, CancellationToken token)
    {
        var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[Frame.MaxData];
            int read;
            while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                var offset = 0;
                while (offset < read)
                {
                    var allowed = await _sendWindow.TakeAsync(read - offset, token).ConfigureAwait(false);
                    _session.Send(Frame.Encode(FrameType.Data, _id, buffer.AsSpan(offset, allowed)));
                    offset += allowed;
                }
            }
        }
    }

    private static List<List<string>> ResponseHeaders(HttpResponseMessage response)
    {
        var headers = new List<List<string>>();
        Add(response.Headers);
        Add(response.Content.Headers);
        return headers;

        void Add(HttpHeaders source)
        {
            foreach (var (name, values) in source.NonValidated)
            {
                if (!Headers.Forwardable(name))
                {
                    continue;
                }

                foreach (var value in values)
                {
                    headers.Add([name.ToLowerInvariant(), value]);
                }
            }
        }
    }
}
