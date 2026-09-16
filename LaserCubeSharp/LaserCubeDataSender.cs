using System.Threading.Channels;

namespace LaserCubeSharp;

internal sealed class LaserCubeDataSender : IAsyncDisposable
{
    private abstract record SenderItem;

    private sealed record SamplesItem(LaserPoint[] Samples) : SenderItem;

    private sealed record ResetItem(
        Func<CancellationToken, ValueTask> Action,
        CancellationToken CancellationToken,
        TaskCompletionSource Completion) : SenderItem;

    private static readonly TimeSpan MinimumAggregationDelay = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan MaximumAggregationDelay = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan RecoveryDelay = TimeSpan.FromMilliseconds(10);

    private readonly Channel<SenderItem> input;
    private readonly LaserCubePacketQueue packets = new();
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sendPacketAsync;
    private readonly Func<uint> getDacRate;
    private readonly Action<int> onSamplesSent;
    private readonly Action<int> onSamplesDropped;
    private readonly Action<Exception> onFault;
    private readonly TimeProvider timeProvider;
    private readonly CancellationTokenSource stopCancellation = new();
    private readonly object feedbackGate = new();
    private readonly Task workerTask;

    private TaskCompletionSource feedbackChanged = CreateSignal();
    private volatile Exception? fault;
    private long feedbackCount;
    private long burstFeedbackStart;
    private long partialStartedAt;
    private long lastPacketSentAt;
    private int stopStarted;
    private volatile bool accepting = true;

    internal LaserCubeDataSender(
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sendPacketAsync,
        Func<uint> getDacRate,
        Action<int> onSamplesSent,
        Action<int> onSamplesDropped,
        Action<Exception> onFault,
        TimeProvider? timeProvider = null)
    {
        this.sendPacketAsync = sendPacketAsync;
        this.getDacRate = getDacRate;
        this.onSamplesSent = onSamplesSent;
        this.onSamplesDropped = onSamplesDropped;
        this.onFault = onFault;
        this.timeProvider = timeProvider ?? TimeProvider.System;

        input = Channel.CreateUnbounded<SenderItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            AllowSynchronousContinuations = false,
        });

        workerTask = RunAsync(stopCancellation.Token);
    }

    internal Exception? Fault => fault;

    internal bool TryEnqueue(LaserPoint[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        return accepting && input.Writer.TryWrite(new SamplesItem(samples));
    }

    internal async ValueTask ResetAsync(
        Func<CancellationToken, ValueTask> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();

        var completion = CreateSignal();
        if (!accepting || !input.Writer.TryWrite(new ResetItem(action, cancellationToken, completion)))
        {
            throw new InvalidOperationException("The LaserCube data sender is no longer running.", fault);
        }

        await completion.Task.ConfigureAwait(false);
    }

    internal void ReportDataFeedback()
    {
        TaskCompletionSource signal;
        lock (feedbackGate)
        {
            feedbackCount++;
            signal = feedbackChanged;
            feedbackChanged = CreateSignal();
        }

        signal.TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref stopStarted, 1) != 0)
        {
            await workerTask.ConfigureAwait(false);
            return;
        }

        accepting = false;
        input.Writer.TryComplete();
        stopCancellation.Cancel();

        try
        {
            await workerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopCancellation.IsCancellationRequested)
        {
        }

        stopCancellation.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await DrainInputAsync(cancellationToken).ConfigureAwait(false);

                if (packets.Count >= LaserCubeProtocol.MaximumPointsPerPacket)
                {
                    await SendNextPacketAsync(flushPartial: false, cancellationToken).ConfigureAwait(false);
                    await RecoverCompletedBurstAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (packets.Count > 0)
                {
                    var aggregationRemaining = Remaining(partialStartedAt, GetAggregationDelay());
                    var recoveryRemaining = packets.PacketsInBurst > 0
                        ? Remaining(lastPacketSentAt, RecoveryDelay)
                        : Timeout.InfiniteTimeSpan;
                    var wait = Minimum(aggregationRemaining, recoveryRemaining);

                    if (await WaitForInputAsync(wait, cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    if (recoveryRemaining <= aggregationRemaining && packets.PacketsInBurst > 0)
                    {
                        packets.CompleteBurst();
                        continue;
                    }

                    await SendNextPacketAsync(flushPartial: true, cancellationToken).ConfigureAwait(false);
                    await RecoverCompletedBurstAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                partialStartedAt = 0;

                if (packets.PacketsInBurst > 0)
                {
                    var recoveryRemaining = Remaining(lastPacketSentAt, RecoveryDelay);
                    if (await WaitForInputAsync(recoveryRemaining, cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    packets.CompleteBurst();
                    continue;
                }

                if (!await input.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            fault = exception;
            accepting = false;
            input.Writer.TryComplete(exception);
            onFault(exception);
        }
        finally
        {
            accepting = false;
            var dropped = packets.Clear();
            while (input.Reader.TryRead(out var item))
            {
                if (item is SamplesItem queued)
                {
                    dropped += queued.Samples.Length;
                }
                else if (item is ResetItem reset)
                {
                    reset.Completion.TrySetCanceled(cancellationToken);
                }
            }

            if (dropped > 0)
            {
                onSamplesDropped(dropped);
            }
        }
    }

    private async ValueTask DrainInputAsync(CancellationToken cancellationToken)
    {
        while (input.Reader.TryRead(out var item))
        {
            if (item is SamplesItem queued)
            {
                if (packets.Count == 0)
                {
                    partialStartedAt = timeProvider.GetTimestamp();
                }

                packets.Enqueue(queued.Samples);
                continue;
            }

            var reset = (ResetItem)item;
            var dropped = packets.Clear();
            partialStartedAt = 0;
            lastPacketSentAt = 0;

            if (dropped > 0)
            {
                onSamplesDropped(dropped);
            }

            try
            {
                await reset.Action(reset.CancellationToken).ConfigureAwait(false);
                reset.Completion.TrySetResult();
            }
            catch (Exception exception)
            {
                reset.Completion.TrySetException(exception);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private async ValueTask SendNextPacketAsync(bool flushPartial, CancellationToken cancellationToken)
    {
        if (packets.PacketsInBurst == 0)
        {
            lock (feedbackGate)
            {
                burstFeedbackStart = feedbackCount;
            }
        }

        if (!packets.TryCreatePacket(flushPartial, out var packet, out var sampleCount))
        {
            return;
        }

        await sendPacketAsync(packet!, cancellationToken).ConfigureAwait(false);
        onSamplesSent(sampleCount);
        lastPacketSentAt = timeProvider.GetTimestamp();
        partialStartedAt = packets.Count > 0 ? lastPacketSentAt : 0;
    }

    private async ValueTask RecoverCompletedBurstAsync(CancellationToken cancellationToken)
    {
        if (packets.PacketsInBurst < LaserCubeProtocol.MaximumPacketsPerBurst)
        {
            return;
        }

        var feedbackTarget = burstFeedbackStart + packets.PacketsInBurst;
        var recoveryStartedAt = timeProvider.GetTimestamp();

        while (true)
        {
            Task feedbackTask;
            lock (feedbackGate)
            {
                if (feedbackCount >= feedbackTarget)
                {
                    break;
                }

                feedbackTask = feedbackChanged.Task;
            }

            var remaining = Remaining(recoveryStartedAt, RecoveryDelay);
            if (remaining == TimeSpan.Zero)
            {
                break;
            }

            try
            {
                await feedbackTask.WaitAsync(remaining, timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                break;
            }
        }

        packets.CompleteBurst();
    }

    private async ValueTask<bool> WaitForInputAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout == TimeSpan.Zero)
        {
            return false;
        }

        try
        {
            return await input.Reader.WaitToReadAsync(cancellationToken)
                .AsTask()
                .WaitAsync(timeout, timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private TimeSpan GetAggregationDelay()
    {
        var rate = getDacRate();
        if (rate == 0)
        {
            return MaximumAggregationDelay;
        }

        var delay = TimeSpan.FromSeconds(LaserCubeProtocol.MaximumPointsPerPacket / (double)rate);
        return delay < MinimumAggregationDelay
            ? MinimumAggregationDelay
            : delay > MaximumAggregationDelay
                ? MaximumAggregationDelay
                : delay;
    }

    private TimeSpan Remaining(long startedAt, TimeSpan duration)
    {
        if (startedAt == 0)
        {
            return duration;
        }

        var remaining = duration - timeProvider.GetElapsedTime(startedAt, timeProvider.GetTimestamp());
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static TimeSpan Minimum(TimeSpan left, TimeSpan right)
    {
        if (left == Timeout.InfiniteTimeSpan)
        {
            return right;
        }

        if (right == Timeout.InfiniteTimeSpan)
        {
            return left;
        }

        return left <= right ? left : right;
    }

    private static TaskCompletionSource CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
