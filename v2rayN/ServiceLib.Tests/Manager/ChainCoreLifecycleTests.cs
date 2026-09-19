using AwesomeAssertions;
using Xunit;

namespace ServiceLib.Tests.Manager;

public class ChainCoreLifecycleTests
{
    [Theory]
    [InlineData(5, 0, true)]
    [InlineData(5, 255, false)]
    [InlineData(4, 0, false)]
    public void Socks_readiness_requires_version_and_accepted_auth(byte version, byte method, bool expected)
    {
        ChainCoreLifecycle.IsReadyReply([version, method]).Should().Be(expected);
    }

    [Fact]
    public async Task Readiness_retries_until_socks_handshake_succeeds()
    {
        var calls = 0;
        var ready = await ChainCoreLifecycle.WaitReadyAsync(() => false,
            _ => Task.FromResult(++calls == 3), TimeSpan.FromSeconds(1), TimeSpan.Zero);
        ready.Should().BeTrue();
        calls.Should().Be(3);
    }

    [Fact]
    public async Task Readiness_bounds_a_hung_probe()
    {
        var ready = await ChainCoreLifecycle.WaitReadyAsync(() => false,
            _ => new TaskCompletionSource<bool>().Task, TimeSpan.FromMilliseconds(30), TimeSpan.Zero);
        ready.Should().BeFalse();
    }

    [Fact]
    public async Task Readiness_rejects_exit_even_after_successful_probe()
    {
        var exited = false;
        var ready = await ChainCoreLifecycle.WaitReadyAsync(() => exited,
            _ => { exited = true; return Task.FromResult(true); }, TimeSpan.FromSeconds(1), TimeSpan.Zero);
        ready.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_target_is_cleaned_without_stopping_independent_target(bool throws)
    {
        var stopped = new List<string>();
        var deleted = new List<string>();
        var failed = await ChainCoreLifecycle.StartReadyAsync(
            () => Task.FromResult<string?>("failed"),
            _ => throws ? Task.FromException<bool>(new IOException("probe failed")) : Task.FromResult(false),
            p => { stopped.Add(p); return Task.CompletedTask; },
            () => { deleted.Add("failed"); return Task.CompletedTask; });
        var healthy = await ChainCoreLifecycle.StartReadyAsync(
            () => Task.FromResult<string?>("healthy"), _ => Task.FromResult(true),
            p => { stopped.Add(p); return Task.CompletedTask; },
            () => { deleted.Add("healthy"); return Task.CompletedTask; });
        failed.Should().BeNull();
        healthy.Should().Be("healthy");
        stopped.Should().Equal("failed");
        deleted.Should().Equal("failed");
    }

    [Fact]
    public async Task Failed_start_can_be_retried_on_reload_without_retaining_failed_ownership()
    {
        var attempts = 0;
        var cleaned = 0;
        var stopped = new List<string>();
        async Task<string?> Launch() => await ChainCoreLifecycle.StartReadyAsync(
            () => Task.FromResult(++attempts == 1 ? null : "generation-" + attempts),
            _ => Task.FromResult(true), p => { stopped.Add(p); return Task.CompletedTask; },
            () => { cleaned++; return Task.CompletedTask; });
        (await Launch()).Should().BeNull();
        var second = await Launch();
        second.Should().Be("generation-2");
        // Simulate ownership release at reload; no failed process was registered.
        stopped.Add(second!);
        (await Launch()).Should().Be("generation-3");
        cleaned.Should().Be(1);
        stopped.Should().Equal("generation-2");
    }

    [Fact]
    public async Task Cleanup_runs_even_when_stop_throws_and_start_failure_has_no_process()
    {
        var deleted = 0;
        var result = await ChainCoreLifecycle.StartReadyAsync(
            () => Task.FromResult<object?>(new object()), _ => Task.FromResult(false),
            _ => Task.FromException(new IOException("stop failed")),
            () => { deleted++; return Task.CompletedTask; });
        result.Should().BeNull();
        await ChainCoreLifecycle.StartReadyAsync<object>(
            () => Task.FromException<object?>(new IOException("start failed")), _ => Task.FromResult(true),
            _ => throw new Exception("no process to stop"),
            () => { deleted++; return Task.CompletedTask; });
        deleted.Should().Be(2);
    }
}
