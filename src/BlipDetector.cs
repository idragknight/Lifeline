using System.Net.NetworkInformation;

namespace Lifeline;

/// <summary>
/// Pings a host on an interval and counts consecutive failures.
/// Emits:
///   <see cref="EarlyWarning"/>        — on the first failed ping (lets the service
///                                       suppress VibeSkua's auto-relogin before its
///                                       own connectionLost handler fires)
///   <see cref="EarlyWarningCleared"/> — if a ping succeeds again before the threshold
///   <see cref="BlipDetected"/>        — when the configured failure threshold is reached
///   <see cref="ConnectionRestored"/>  — when a ping succeeds after a blip
/// </summary>
internal sealed class BlipDetector : IDisposable
{
    private readonly Func<bool> _enabled;
    private readonly Func<string> _target;
    private readonly Func<int> _threshold;
    private readonly Func<int> _timeoutMs;
    private readonly System.Timers.Timer _timer;

    private int _consecutiveFailures;
    private volatile bool _blipActive;
    private volatile bool _earlyWarningFired;
    private bool _disposed;

    public event Action? EarlyWarning;
    public event Action? EarlyWarningCleared;
    public event Action? BlipDetected;
    public event Action? ConnectionRestored;

    public bool IsBlipActive => _blipActive;

    public BlipDetector(
        Func<bool> enabled,
        Func<string> target,
        Func<int> threshold,
        Func<int> timeoutMs,
        int intervalMs = 3000)
    {
        _enabled = enabled;
        _target = target;
        _threshold = threshold;
        _timeoutMs = timeoutMs;
        _timer = new System.Timers.Timer(intervalMs) { AutoReset = true };
        _timer.Elapsed += (_, _) => _ = TickAsync();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    private async Task TickAsync()
    {
        if (_disposed || !_enabled()) return;
        try
        {
            bool ok = await PingAsync(_target(), _timeoutMs());
            if (!ok)
            {
                int failures = Interlocked.Increment(ref _consecutiveFailures);
                if (failures == 1 && !_earlyWarningFired)
                {
                    _earlyWarningFired = true;
                    try { EarlyWarning?.Invoke(); } catch { }
                }
                if (failures >= Math.Max(1, _threshold()) && !_blipActive)
                {
                    _blipActive = true;
                    try { BlipDetected?.Invoke(); } catch { }
                }
                return;
            }

            Interlocked.Exchange(ref _consecutiveFailures, 0);
            bool wasWarned = _earlyWarningFired;
            _earlyWarningFired = false;
            bool wasBlip = _blipActive;
            _blipActive = false;

            if (wasBlip)
            {
                try { ConnectionRestored?.Invoke(); } catch { }
            }
            else if (wasWarned)
            {
                try { EarlyWarningCleared?.Invoke(); } catch { }
            }
        }
        catch { }
    }

    private static async Task<bool> PingAsync(string target, int timeoutMs)
    {
        try
        {
            using Ping p = new();
            PingReply reply = await p.SendPingAsync(target, timeoutMs);
            return reply.Status == IPStatus.Success;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
    }
}
