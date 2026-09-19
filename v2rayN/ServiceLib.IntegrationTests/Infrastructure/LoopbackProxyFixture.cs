namespace ServiceLib.IntegrationTests.Infrastructure;

// Deliberately only SOCKS5 CONNECT + one bounded HTTP request, not a general proxy framework.
internal sealed class LoopbackProxyFixture : LoopbackListener
{
    private readonly IPEndPoint[] _permitted;
    public LoopbackProxyFixture(IPEndPoint[] permitted) : base(IPAddress.Loopback, 0)
    {
        _permitted = permitted;
        Start();
    }

    protected override async Task HandleAsync(TcpClient client, CancellationToken token)
    {
        var incoming = client.GetStream();
        var greeting = await SocksWire.ReadAsync(incoming, 2, token);
        if (greeting[0] != 5) throw new IOException("Not SOCKS5");
        var methods = await SocksWire.ReadAsync(incoming, greeting[1], token);
        if (!methods.Contains((byte)0)) throw new IOException("No anonymous SOCKS method");
        await incoming.WriteAsync(new byte[] { 5, 0 }, token);
        var header = await SocksWire.ReadAsync(incoming, 4, token);
        if (header[0] != 5 || header[1] != 1)
            throw new IOException("Only SOCKS CONNECT is allowed");
        IPAddress address;
        if (header[3] == 1)
            address = new IPAddress(await SocksWire.ReadAsync(incoming, 4, token));
        else if (header[3] == 3)
        {
            var length = (await SocksWire.ReadAsync(incoming, 1, token))[0];
            var name = Encoding.ASCII.GetString(await SocksWire.ReadAsync(incoming, length, token));
            if (name != "target.test") throw new IOException("Non-fixture domain refused");
            address = IPAddress.Loopback;
        }
        else throw new IOException("Unsupported address type");
        var portBytes = await SocksWire.ReadAsync(incoming, 2, token);
        var destination = new IPEndPoint(address, (portBytes[0] << 8) | portBytes[1]);
        if (!_permitted.Contains(destination))
            throw new IOException($"Refusing non-fixture destination {destination}");
        using var upstream = new TcpClient();
        await upstream.ConnectAsync(address, destination.Port, token);
        await incoming.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, token);
        var request = await SocksWire.ReadHeadersAsync(incoming, token);
        RequestIds.Enqueue(SocksWire.RequestId(request));
        await upstream.GetStream().WriteAsync(request, token);
        await upstream.GetStream().CopyToAsync(incoming, token);
    }
}

internal static class SocksWire
{
    public static async Task<byte[]> ReadAsync(Stream stream, int length, CancellationToken token)
    {
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token);
        return bytes;
    }

    public static async Task NegotiateAsync(NetworkStream stream, CancellationToken token)
    {
        await stream.WriteAsync(new byte[] { 5, 1, 0 }, token);
        var reply = await ReadAsync(stream, 2, token);
        if (reply[0] != 5 || reply[1] != 0) throw new IOException("SOCKS negotiation failed");
    }

    public static async Task<TcpClient> ConnectAsync(int proxyPort, IPEndPoint target, CancellationToken token, string? domain = null)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, proxyPort, token);
            var stream = client.GetStream();
            await NegotiateAsync(stream, token);
            var request = new byte[] { 5, 1, 0, 1, 0, 0, 0, 0,
                (byte)(target.Port >> 8), (byte)(target.Port & 255) };
            target.Address.GetAddressBytes().CopyTo(request, 4);
            if (domain != null)
            {
                var name = Encoding.ASCII.GetBytes(domain);
                request = [5, 1, 0, 3, (byte)name.Length, .. name, (byte)(target.Port >> 8), (byte)(target.Port & 255)];
            }
            await stream.WriteAsync(request, token);
            var reply = await ReadAsync(stream, 4, token);
            if (reply[0] != 5 || reply[1] != 0) throw new IOException($"SOCKS CONNECT failed: {reply[1]}");
            var addressLength = reply[3] switch
            {
                1 => 4,
                4 => 16,
                3 => (await ReadAsync(stream, 1, token))[0],
                _ => throw new IOException("Invalid SOCKS reply address"),
            };
            await ReadAsync(stream, addressLength + 2, token);
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    public static async Task<byte[]> ReadHeadersAsync(Stream stream, CancellationToken token)
    {
        using var data = new MemoryStream();
        while (data.Length < 8192)
        {
            var one = await ReadAsync(stream, 1, token);
            data.WriteByte(one[0]);
            if (data.Length >= 4)
            {
                var buffer = data.GetBuffer();
                var n = (int)data.Length;
                if (buffer[n - 4] == 13 && buffer[n - 3] == 10 && buffer[n - 2] == 13 && buffer[n - 1] == 10)
                    return data.ToArray();
            }
        }
        throw new IOException("HTTP headers exceeded fixture limit");
    }

    public static string RequestId(byte[] request)
    {
        var firstLine = Encoding.ASCII.GetString(request).Split("\r\n")[0].Split(' ');
        if (firstLine.Length != 3 || firstLine[0] != "GET"
            || !Guid.TryParseExact(firstLine[1].TrimStart('/'), "N", out var id))
            throw new IOException("Missing request correlation ID");
        return id.ToString("N");
    }
}
