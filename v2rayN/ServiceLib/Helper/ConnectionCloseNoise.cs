using System.Text.RegularExpressions;

namespace ServiceLib.Helper;

/// <summary>
/// Collapses only sing-box's terminal TCP download-close diagnostic into a single
/// count across all addresses. A nonempty explicit message filter bypasses collapse
/// for future messages; earlier originals remain in the diagnostic log, not a replay buffer.
/// Originals are saved through the existing diagnostic logger before the caller
/// applies display/filter decisions. Disabled diagnostic logging disables collapse
/// too: the original remains visible, respecting the user's persistence preference.
/// </summary>
public sealed partial class ConnectionCloseNoise
{
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private long _count;
    private long _windowStart;

    public ConnectionCloseNoise(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static bool IsNoise(string message)
    {
        // Anchor the complete line, including only known sing-box metadata. Never
        // collapse an arbitrary error that happens to contain the close diagnostic.
        return CloseMessage().IsMatch(message.AsSpan().TrimEnd("\r\n"));
    }

    public bool TryCollapse(string message, bool explicitFilter)
    {
        if (!NLog.LogManager.IsLoggingEnabled() || !IsNoise(message))
        {
            return false;
        }

        // Called on the message producer, never from the UI flush timer. Reuse
        // the diagnostic logger and its enable/disable policy; persist no extra data.
        Logging.SaveLog(message);
        if (explicitFilter)
        {
            return false;
        }

        lock (_gate)
        {
            if (_count == 0)
            {
                _windowStart = _timeProvider.GetTimestamp();
            }
            // Saturation keeps even a prolonged hidden/paused view bounded.
            if (_count < long.MaxValue)
            {
                _count++;
            }
        }
        return true;
    }

    /// <summary>
    /// Drains a completed window, including its final partial burst when no new
    /// messages arrive. Call from the existing UI flush timer; no I/O occurs here.
    /// </summary>
    public long TakeSummaryCount()
    {
        lock (_gate)
        {
            if (_count == 0 || _timeProvider.GetElapsedTime(_windowStart) < SummaryInterval)
            {
                return 0;
            }

            var count = _count;
            _count = 0;
            return count;
        }
    }

    [GeneratedRegex(@"\A(?:[+-][0-9]{4} [0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2} )?(?:(?:TRACE|DEBUG|INFO|WARN|ERROR|FATAL) (?:\[[0-9]+ [0-9.]+(?:ns|µs|ms|s|m|h)\] )?)?(?:connection: )?connection download closed: close tcp (?:[0-9.]+|\[[0-9a-fA-F:.%]+\]):[0-9]+->(?:[0-9.]+|\[[0-9a-fA-F:.%]+\]):[0-9]+: endpoint not connected\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex CloseMessage();
}
