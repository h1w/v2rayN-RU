namespace ServiceLib.IntegrationTests.Infrastructure;

// Authoritative, loopback-only UDP fixture. Never forwards unknown names upstream.
internal sealed class LocalDnsFixture : IAsyncDisposable
{
    private readonly UdpClient _socket = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;
    public ConcurrentQueue<string> Queries { get; } = new();
    public int Port => ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;
    public LocalDnsFixture() => _run = RunAsync();

    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var packet = await _socket.ReceiveAsync(_stop.Token);
                var q = packet.Buffer;
                if (q.Length < 17) throw new IOException("Invalid DNS query");
                var pos = 12;
                var labels = new List<string>();
                while (q[pos] != 0)
                {
                    var length = q[pos++];
                    if (length > 63 || pos + length >= q.Length) throw new IOException("Invalid DNS label");
                    labels.Add(Encoding.ASCII.GetString(q, pos, length)); pos += length;
                }
                pos++;
                var type = (q[pos] << 8) | q[pos + 1];
                pos += 4;
                var name = string.Join('.', labels);
                Queries.Enqueue(name);
                var known = name == "target.test";
                var answer = known && type == 1;
                using var response = new MemoryStream();
                response.Write(q, 0, pos);
                var bytes = response.GetBuffer();
                bytes[2] = 0x81; bytes[3] = known ? (byte)0x80 : (byte)0x83;
                bytes[6] = 0; bytes[7] = answer ? (byte)1 : (byte)0;
                bytes[8] = bytes[9] = bytes[10] = bytes[11] = 0;
                if (answer)
                    response.Write(new byte[] { 0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 0, 0, 4, 127, 0, 0, 1 });
                await _socket.SendAsync(response.ToArray(), packet.RemoteEndPoint, _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _run; }
        finally { _socket.Dispose(); _stop.Dispose(); }
    }
}
