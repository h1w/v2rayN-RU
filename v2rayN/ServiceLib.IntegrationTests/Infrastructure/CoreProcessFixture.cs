namespace ServiceLib.IntegrationTests.Infrastructure;

internal sealed class CoreProcessFixture : IAsyncDisposable
{
    private readonly Process _process;
    private readonly string _directory;
    private readonly ConcurrentQueue<string> _output = new();
    private readonly Task _stdout;
    private readonly Task _stderr;
    public string Output => string.Join(Environment.NewLine, _output);

    private CoreProcessFixture(Process process, string directory)
    {
        _process = process;
        _directory = directory;
        _stdout = CaptureAsync(process.StandardOutput, "stdout");
        _stderr = CaptureAsync(process.StandardError, "stderr");
    }

    public static string RequireExecutable(ECoreType type)
    {
        var variable = type == ECoreType.sing_box ? "V2RAYN_TEST_SINGBOX_PATH" : "V2RAYN_TEST_XRAY_PATH";
        var defaultName = type == ECoreType.sing_box ? "sing-box.exe" : "xray.exe";
        var path = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(path))
        {
            var repoLocalCores = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "local-cores"));
            if (Directory.Exists(repoLocalCores))
            {
                var found = Directory.GetFiles(repoLocalCores, defaultName, SearchOption.AllDirectories).FirstOrDefault();
                if (found != null && File.Exists(found))
                {
                    return found;
                }
            }
        }
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new InvalidOperationException($"Integration preparation failed: {variable} must name an existing absolute core executable or binary must be present in 'local-cores' repository directory ({defaultName}). Tests are not skipped; no download is attempted.");
        return path;
    }

    public static async Task<CoreProcessFixture> StartAsync(string executable, ECoreType type, string json, int port)
    {
        var directory = Path.Combine(Path.GetTempPath(), "v2rayN-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Process? process = null;
        CoreProcessFixture? fixture = null;
        try
        {
            var configPath = Path.Combine(directory, "client.json");
            await File.WriteAllTextAsync(configPath, json);
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = directory, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            };
            start.ArgumentList.Add("run");
            start.ArgumentList.Add(type == ECoreType.sing_box ? "-c" : "-config");
            start.ArgumentList.Add(configPath);
            process = Process.Start(start) ?? throw new IOException("Core did not start");
            fixture = new CoreProcessFixture(process, directory);
            await fixture.WaitReadyAsync(port);
            return fixture;
        }
        catch (Exception ex)
        {
            var output = fixture?.Output ?? string.Empty;
            if (fixture != null) await fixture.DisposeAsync();
            else
            {
                process?.Dispose();
                Directory.Delete(directory, true);
            }
            throw new InvalidOperationException($"Core startup failed: {ex.Message}\n{output}", ex);
        }
    }

    private async Task CaptureAsync(StreamReader reader, string source)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            _output.Enqueue($"{source}: {line}");
            while (_output.Count > 300) _output.TryDequeue(out _);
        }
    }

    public void AssertRunning()
    {
        if (_process.HasExited)
            throw new IOException($"Core exited ({_process.ExitCode}):\n{Output}");
    }

    private async Task WaitReadyAsync(int port)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            AssertRunning();
            deadline.Token.ThrowIfCancellationRequested();
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            attempt.CancelAfter(TimeSpan.FromMilliseconds(500));
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, attempt.Token);
                await SocksWire.NegotiateAsync(client.GetStream(), attempt.Token);
                AssertRunning();
                return;
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
            {
                await Task.Delay(50, deadline.Token);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(_stdout, _stderr).WaitAsync(deadline.Token);
        }
        finally
        {
            _process.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
