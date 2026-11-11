using System.Threading.Channels;
using ComplicatedMarketBoard.Integrations.Universalis;

namespace ComplicatedMarketBoard.Tests;

public sealed class UniversalisRequestPacerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(200)]
    [InlineData(100)]
    [InlineData(50)]
    public async Task ConcurrentWaitersShareOneSpacingTimeline(int spacing)
    {
        var clock = new Clock();
        using var pacer = clock.Create(() => spacing);
        await pacer.WaitAsync(default);
        var second = pacer.WaitAsync(default);
        var third = pacer.WaitAsync(default);
        Assert.False(second.IsCompleted);
        Assert.False(third.IsCompleted);
        await clock.CompleteDelay(spacing);
        await second.WaitAsync(Timeout);
        Assert.False(third.IsCompleted);
        await clock.CompleteDelay(spacing);
        await third.WaitAsync(Timeout);
        Assert.Equal(spacing * 2, clock.ElapsedMilliseconds);
    }

    [Fact]
    public async Task SettingIncreaseIsRecheckedDuringWaitAndDecreaseAppliesWithoutReload()
    {
        var spacing = 200;
        var clock = new Clock();
        using var pacer = clock.Create(() => spacing);
        await pacer.WaitAsync(default);
        var waiting = pacer.WaitAsync(default);
        spacing = 500;
        await clock.CompleteDelay(200);
        await clock.CompleteDelay(300);
        await waiting.WaitAsync(Timeout);
        Assert.Equal(500, clock.ElapsedMilliseconds);
        spacing = 50;
        waiting = pacer.WaitAsync(default);
        await clock.CompleteDelay(50);
        await waiting.WaitAsync(Timeout);
        Assert.Equal(550, clock.ElapsedMilliseconds);
    }

    [Fact]
    public async Task SharedCooldownExtendedDuringPacingWinsOverFasterPreference()
    {
        var clock = new Clock();
        var spacing = 200;
        var cooldown = DateTimeOffset.MinValue;
        using var pacer = clock.Create(() => spacing, () => cooldown);
        await pacer.WaitAsync(default);
        var waiting = pacer.WaitAsync(default);
        spacing = 50;
        cooldown = clock.Now.AddSeconds(1);
        await clock.CompleteDelay(200);
        await clock.CompleteDelay(800);
        await waiting.WaitAsync(Timeout);
        Assert.Equal(1000, clock.ElapsedMilliseconds);
    }

    [Fact]
    public async Task CooldownAppliesEvenToTheFirstRequest()
    {
        var clock = new Clock();
        var cooldown = clock.Now.AddSeconds(2);
        using var pacer = clock.Create(() => 50, () => cooldown);
        var waiting = pacer.WaitAsync(default);
        await clock.CompleteDelay(2000);
        await waiting.WaitAsync(Timeout);
        Assert.Equal(2000, clock.ElapsedMilliseconds);
    }

    [Fact]
    public async Task CancelledPacingAndQueuedWaitsDoNotReserveFutureStarts()
    {
        var clock = new Clock();
        using var pacer = clock.Create(() => 200);
        await pacer.WaitAsync(default);
        using var cancellation = new CancellationTokenSource();
        var waiting = pacer.WaitAsync(cancellation.Token);
        var abandonedDelay = await clock.NextDelay();
        Assert.Equal(200, abandonedDelay.Milliseconds);
        using var queuedCancellation = new CancellationTokenSource();
        var queued = pacer.WaitAsync(queuedCancellation.Token);
        queuedCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        waiting = pacer.WaitAsync(default);
        await clock.CompleteDelay(200);
        await waiting.WaitAsync(Timeout);
        Assert.Equal(200, clock.ElapsedMilliseconds);
    }

    [Theory]
    [InlineData(-1, 50)]
    [InlineData(0, 50)]
    [InlineData(200, 200)]
    [InlineData(2000, 1000)]
    public async Task RuntimeBoundsCannotDisablePacing(int configured, int expected)
    {
        var clock = new Clock();
        using var pacer = clock.Create(() => configured);
        Assert.Equal(expected, pacer.SpacingMilliseconds);
        await pacer.WaitAsync(default);
        var waiting = pacer.WaitAsync(default);
        await clock.CompleteDelay(expected);
        await waiting.WaitAsync(Timeout);
    }

    private sealed class Clock
    {
        private readonly Channel<PendingDelay> delays = Channel.CreateUnbounded<PendingDelay>();
        private long elapsedMilliseconds;
        internal long ElapsedMilliseconds => Interlocked.Read(ref elapsedMilliseconds);
        internal DateTimeOffset Now => DateTimeOffset.UnixEpoch.AddMilliseconds(ElapsedMilliseconds);
        internal UniversalisRequestPacer Create(Func<int> spacing, Func<DateTimeOffset>? cooldown = null) =>
            new(spacing, cooldown ?? (() => DateTimeOffset.MinValue), () => Now, Delay);

        private Task Delay(TimeSpan duration, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            delays.Writer.TryWrite(new PendingDelay((long)duration.TotalMilliseconds, completion));
            return completion.Task.WaitAsync(cancellationToken);
        }

        internal Task<PendingDelay> NextDelay() => delays.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
        internal async Task CompleteDelay(long expectedMilliseconds)
        {
            var pending = await NextDelay();
            Assert.Equal(expectedMilliseconds, pending.Milliseconds);
            Interlocked.Add(ref elapsedMilliseconds, pending.Milliseconds);
            pending.Completion.SetResult(true);
        }
    }

    private sealed record PendingDelay(long Milliseconds, TaskCompletionSource<bool> Completion);
}
