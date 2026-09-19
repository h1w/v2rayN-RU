namespace ServiceLib.IntegrationTests.Infrastructure;

internal sealed class LoopbackTargetServer : LoopbackListener
{
    public LoopbackTargetServer(IPAddress address, int port = 0) : base(address, port) => Start();

    protected override async Task HandleAsync(TcpClient client, CancellationToken token)
    {
        var stream = client.GetStream();
        var request = await SocksWire.ReadHeadersAsync(stream, token);
        var id = SocksWire.RequestId(request);
        RequestIds.Enqueue(id);
        var body = Encoding.ASCII.GetBytes(id);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{id}"), token);
    }
}

// Tracks every accepted connection and surfaces background errors; disposal cancels and joins
// the accept loop and all handlers before releasing the fixture.
internal abstract class LoopbackListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _handlers = [];
    private readonly ConcurrentQueue<Exception> _errors = new();
    private Task _accept = Task.CompletedTask;
    public ConcurrentQueue<string> RequestIds { get; } = new();
    public IPEndPoint Endpoint { get; }
    public int Port => Endpoint.Port;

    protected LoopbackListener(IPAddress address, int port)
    {
        _listener = new TcpListener(address, port);
        _listener.Start();
        Endpoint = (IPEndPoint)_listener.LocalEndpoint;
    }

    protected void Start() => _accept = AcceptAsync();
    protected abstract Task HandleAsync(TcpClient client, CancellationToken token);

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _handlers.Add(ServeAsync(client));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { _errors.Enqueue(ex); }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            try { await HandleAsync(client, deadline.Token); }
            catch (Exception) when (_stop.IsCancellationRequested) { }
            catch (Exception ex) { _errors.Enqueue(ex); }
        }
    }

    public void ThrowIfFaulted()
    {
        if (!_errors.IsEmpty)
            throw new AggregateException("Loopback fixture failed", _errors);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _accept.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.WhenAll(_handlers).WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { _stop.Dispose(); }
    }
}
