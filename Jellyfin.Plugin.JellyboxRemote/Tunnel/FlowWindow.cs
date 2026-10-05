namespace Jellyfin.Plugin.JellyboxRemote.Tunnel;

internal sealed class FlowWindow(int initial)
{
    private readonly object _gate = new();
    private long _available = initial;
    private TaskCompletionSource? _waiter;

    public void Grant(int bytes)
    {
        TaskCompletionSource? waiter;
        lock (_gate)
        {
            _available += bytes;
            waiter = _waiter;
            _waiter = null;
        }

        waiter?.TrySetResult();
    }

    public async ValueTask<int> TakeAsync(int wanted, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (_available > 0)
                {
                    var taken = (int)Math.Min(_available, wanted);
                    _available -= taken;
                    return taken;
                }

                _waiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _waiter.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
