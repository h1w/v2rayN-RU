namespace ServiceLib.Manager;

/// <summary>Bounded readiness and per-target ownership; never chooses a replacement route.</summary>
public static class ChainCoreLifecycle
{
    public static async Task<bool> WaitReadyAsync(Func<bool> hasExited,
        Func<CancellationToken, Task<bool>> probe, TimeSpan timeout, TimeSpan retryDelay)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            while (!hasExited())
            {
                deadline.Token.ThrowIfCancellationRequested();
                try
                {
                    if (await probe(deadline.Token).WaitAsync(deadline.Token))
                        return !hasExited();
                }
                catch (Exception ex) when (ex is IOException or SocketException) { }
                await Task.Delay(retryDelay, deadline.Token);
            }
        }
        catch (OperationCanceledException) { }
        return false;
    }

    public static async Task<bool> ProbeSocksAsync(int port, CancellationToken token)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(Global.Loopback, port, token);
        var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 0 }, token);
        var reply = new byte[2];
        await stream.ReadExactlyAsync(reply, token);
        return IsReadyReply(reply);
    }

    public static bool IsReadyReply(ReadOnlySpan<byte> reply) => reply.Length == 2 && reply[0] == 5 && reply[1] == 0;

    public static async Task<T?> StartReadyAsync<T>(Func<Task<T?>> start, Func<T, Task<bool>> ready,
        Func<T, Task> stop, Func<Task> cleanup) where T : class
    {
        T? process = null;
        try
        {
            process = await start();
            if (process != null && await ready(process)) return process;
        }
        catch (Exception ex) { Logging.SaveLog("ChainCoreLifecycle", ex); }
        try
        {
            if (process != null) await stop(process);
        }
        catch (Exception ex) { Logging.SaveLog("ChainCoreLifecycle", ex); }
        finally
        {
            try { await cleanup(); }
            catch (Exception ex) { Logging.SaveLog("ChainCoreLifecycle", ex); }
        }
        return null;
    }
}
