using System.Threading;

namespace ComplicatedMarketBoard.Integrations.Universalis;

// One service-wide admission timeline. Spacing applies to every request; the recovery cooldown
// applies unless the caller explicitly ignores it (user-initiated refresh).
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

    internal async Task WaitAsync(bool ignoreCooldown, CancellationToken cancellationToken)
    {
        while (true)
        {
            // Wait out the cooldown without holding the gate, so a cooldown-exempt
            // request is never queued behind one that is sleeping through it.
            if (!ignoreCooldown)
            {
                var cooldownRemaining = cooldownUntil() - now();
                if (cooldownRemaining > TimeSpan.Zero)
                {
                    await delay(cooldownRemaining, cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var current = now();
                    // A cooldown started while we queued: go back and wait it out off the gate.
                    if (!ignoreCooldown && cooldownUntil() > current)
                        break;

                    var remaining = lastStartAt is { } previous
                        ? previous.AddMilliseconds(SpacingMilliseconds) - current
                        : TimeSpan.Zero;
                    if (remaining <= TimeSpan.Zero)
                    {
                        lastStartAt = current;
                        return;
                    }

                    // Re-read spacing preference and cooldown after every wait.
                    await delay(remaining, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                gate.Release();
            }
        }
    }

    // The request budget invokes this only after registered requests have drained.
    public void Dispose() => gate.Dispose();
}
