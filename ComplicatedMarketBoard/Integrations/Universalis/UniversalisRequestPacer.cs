using System.Threading;

namespace ComplicatedMarketBoard.Integrations.Universalis;

// One service-wide admission timeline; faster preferences never bypass recovery cooldowns.
internal sealed class UniversalisRequestPacer(
    Func<int> configuredSpacingMilliseconds,
    Func<DateTimeOffset> cooldownUntil,
    Func<DateTimeOffset>? utcNow = null,
    Func<TimeSpan, CancellationToken, Task>? delayAsync = null) : IDisposable
{
    internal const int DefaultSpacingMilliseconds = 200;
    internal const int MinimumSpacingMilliseconds = 50;
    internal const int MaximumSpacingMilliseconds = 1000;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Func<DateTimeOffset> now = utcNow ?? (() => DateTimeOffset.UtcNow);
    private readonly Func<TimeSpan, CancellationToken, Task> delay = delayAsync ?? Task.Delay;
    private DateTimeOffset? lastStartAt;

    internal static int Normalize(int value) => Math.Clamp(value, MinimumSpacingMilliseconds, MaximumSpacingMilliseconds);
    internal int SpacingMilliseconds => Normalize(configuredSpacingMilliseconds());

    internal async Task WaitAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = now();
                var allowedAt = cooldownUntil();
                if (lastStartAt is { } previous)
                {
                    var spacedAt = previous.AddMilliseconds(SpacingMilliseconds);
                    if (spacedAt > allowedAt)
                        allowedAt = spacedAt;
                }

                var remaining = allowedAt - current;
                if (remaining <= TimeSpan.Zero)
                {
                    lastStartAt = current;
                    return;
                }

                // Re-read both preferences and shared cooldown after every wait.
                await delay(remaining, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    // The request budget invokes this only after registered requests have drained.
    public void Dispose() => gate.Dispose();
}
