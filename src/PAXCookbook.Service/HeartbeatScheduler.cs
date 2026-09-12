namespace PAXCookbook.Service;

/// <summary>
/// CYCLE 64. The heartbeat scheduling seam, and nothing more.
///
/// THE SEAM IS DELIBERATELY CLOSED. It has exactly one operation and it takes no
/// interval, no path, no callback, no configuration key and no environment value.
/// A caller cannot retune the cadence through it, cannot learn the cadence from
/// it, and cannot make it do anything except wait for the next beat. It is the
/// smallest surface that lets a focused test advance time without a real clock.
/// </summary>
internal interface IHeartbeatScheduler
{
    /// <summary>
    /// Completes when the next heartbeat is due. Throws
    /// <see cref="OperationCanceledException"/> when the token is cancelled first,
    /// which is exactly the observable contract the worker already relied on.
    /// </summary>
    Task WaitForNextHeartbeatAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The ONE production scheduler. It captures the fixed interval supplied by the
/// composition root at construction and waits on the real clock, exactly as the
/// worker's previous inline Task.Delay did. The interval is private and immutable:
/// there is no property, no setter and no accessor through which it can be read
/// back or changed after construction.
/// </summary>
internal sealed class FixedIntervalHeartbeatScheduler : IHeartbeatScheduler
{
    private readonly TimeSpan _interval;

    internal FixedIntervalHeartbeatScheduler(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        _interval = interval;
    }

    public Task WaitForNextHeartbeatAsync(CancellationToken cancellationToken) =>
        Task.Delay(_interval, cancellationToken);
}
