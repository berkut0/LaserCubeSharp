using System.Net;
using System.Net.Sockets;

namespace LaserCubeSharp;

public sealed class LaserCube : IDisposable, IAsyncDisposable
{
    private const int SocketBufferSize = 5_250_000;
    private const int CommandRepeatCount = 2;

    private readonly IPAddress deviceAddress;
    private readonly IPEndPoint commandEndPoint;
    private readonly IPEndPoint dataEndPoint;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim commandSendGate = new(1, 1);
    private readonly object stateGate = new();

    private UdpClient? commandClient;
    private UdpClient? dataClient;
    private CancellationTokenSource? receiveCancellation;
    private Task? commandReceiveTask;
    private Task? dataReceiveTask;
    private LaserCubeDataSender? dataSender;
    private LaserCubeStatus? status;
    private Exception? lastTransportError;
    private int estimatedBufferFree;
    private int queuedSampleCount;
    private int minimumBufferFree = 1000;
    private uint dacRate;
    private bool isStarted;
    private bool isDisposed;

    public LaserCube(string deviceAddress)
        : this(IPAddress.Parse(deviceAddress))
    {
    }

    public LaserCube(IPAddress deviceAddress)
    {
        ArgumentNullException.ThrowIfNull(deviceAddress);

        if (deviceAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("LaserCube network transport currently requires an IPv4 address.", nameof(deviceAddress));
        }

        this.deviceAddress = deviceAddress;
        commandEndPoint = new IPEndPoint(deviceAddress, LaserCubeProtocol.CommandPort);
        dataEndPoint = new IPEndPoint(deviceAddress, LaserCubeProtocol.DataPort);
    }

    public IPAddress DeviceAddress => deviceAddress;

    public bool IsStarted
    {
        get
        {
            lock (stateGate)
            {
                return isStarted;
            }
        }
    }

    public LaserCubeStatus? Status
    {
        get
        {
            lock (stateGate)
            {
                return status;
            }
        }
    }

    public Exception? LastTransportError
    {
        get
        {
            lock (stateGate)
            {
                return lastTransportError;
            }
        }
    }

    public int EstimatedBufferFree
    {
        get
        {
            lock (stateGate)
            {
                return estimatedBufferFree;
            }
        }
    }

    public int MinimumBufferFree
    {
        get => minimumBufferFree;
        set
        {
            if (value < 0 || value > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            minimumBufferFree = value;
        }
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (isStarted)
            {
                return;
            }

            var newCommandClient = CreateClient(LaserCubeProtocol.CommandPort);
            UdpClient? newDataClient = null;

            try
            {
                newDataClient = CreateClient(LaserCubeProtocol.DataPort);
            }
            catch
            {
                newCommandClient.Dispose();
                throw;
            }

            commandClient = newCommandClient;
            dataClient = newDataClient;
            receiveCancellation = new CancellationTokenSource();
            commandReceiveTask = ReceiveResponsesAsync(newCommandClient, isDataSocket: false, receiveCancellation.Token);
            dataReceiveTask = ReceiveResponsesAsync(newDataClient, isDataSocket: true, receiveCancellation.Token);
            dataSender = new LaserCubeDataSender(
                (packet, token) => SendDataPacketAsync(newDataClient, packet, token),
                GetDacRate,
                OnSamplesSent,
                OnSamplesDropped,
                RecordTransportError);

            lock (stateGate)
            {
                status = null;
                estimatedBufferFree = 0;
                queuedSampleCount = 0;
                dacRate = 0;
                lastTransportError = null;
                isStarted = true;
            }

            try
            {
                await SendCommandAsync(
                    LaserCubeProtocol.CreateBooleanCommand(LaserCubeProtocol.SetOutput, false),
                    cancellationToken).ConfigureAwait(false);
                await SendCommandAsync(
                    new byte[] { LaserCubeProtocol.ClearRingBuffer },
                    cancellationToken).ConfigureAwait(false);
                await SetBufferRepliesEnabledCoreAsync(true, cancellationToken).ConfigureAwait(false);
                await SendCommandAsync(new byte[] { LaserCubeProtocol.GetFullInfo }, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await StopCoreAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask RequestStatusAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfNotStarted();
        await SendCommandAsync(new byte[] { LaserCubeProtocol.GetFullInfo }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RequestBufferFreeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfNotStarted();
        await SendCommandAsync(new byte[] { LaserCubeProtocol.GetBufferFree }, cancellationToken, repeatCount: 1).ConfigureAwait(false);
    }

    public async ValueTask SetDacRateAsync(uint rate, CancellationToken cancellationToken = default)
    {
        ThrowIfNotStarted();
        await SendCommandAsync(LaserCubeProtocol.CreateSetRateCommand(rate), cancellationToken).ConfigureAwait(false);

        lock (stateGate)
        {
            dacRate = rate;
        }
    }

    public async ValueTask SetOutputEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ThrowIfNotStarted();
        var command = LaserCubeProtocol.CreateBooleanCommand(LaserCubeProtocol.SetOutput, enabled);

        if (enabled)
        {
            await SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
            return;
        }

        var sender = GetDataSender();
        if (sender.Fault is not null)
        {
            await SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
            return;
        }

        await sender.ResetAsync(
            token => SendCommandAsync(command, token),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetBufferRepliesEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ThrowIfNotStarted();
        await SetBufferRepliesEnabledCoreAsync(enabled, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ClearRingBufferAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfNotStarted();
        var sender = GetDataSender();
        var command = new byte[] { LaserCubeProtocol.ClearRingBuffer };
        if (sender.Fault is not null)
        {
            await SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await sender.ResetAsync(
                token => SendCommandAsync(command, token),
                cancellationToken).ConfigureAwait(false);
        }

        lock (stateGate)
        {
            estimatedBufferFree = 0;
        }
    }

    public async ValueTask<bool> SendFrameAsync(
        IReadOnlyList<LaserPoint> points,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(points);
        ThrowIfNotStarted();
        cancellationToken.ThrowIfCancellationRequested();

        if (points.Count == 0)
        {
            return true;
        }

        var sampleCount = points.Count;
        LaserCubeDataSender? sender;
        lock (stateGate)
        {
            sender = dataSender;
            if (!isStarted || sender is null)
            {
                throw new InvalidOperationException("Call StartAsync before communicating with the LaserCube.");
            }

            if (sender.Fault is not null)
            {
                throw new InvalidOperationException("The LaserCube data sender has stopped after a transport error.", sender.Fault);
            }

            if (estimatedBufferFree - sampleCount >= minimumBufferFree)
            {
                estimatedBufferFree -= sampleCount;
                queuedSampleCount += sampleCount;
            }
            else
            {
                sender = null;
            }
        }

        if (sender is null)
        {
            await RequestBufferFreeAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        LaserPoint[] samples;
        try
        {
            samples = new LaserPoint[sampleCount];
            for (var index = 0; index < sampleCount; index++)
            {
                samples[index] = points[index];
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            ReleaseQueuedReservation(sampleCount);
            throw;
        }

        if (sender.TryEnqueue(samples))
        {
            return true;
        }

        ReleaseQueuedReservation(sampleCount);

        throw new InvalidOperationException("The LaserCube data sender is no longer accepting samples.", sender.Fault);
    }

    public async ValueTask StopAsync()
    {
        await lifecycleGate.WaitAsync().ConfigureAwait(false);

        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        StopAsync().AsTask().GetAwaiter().GetResult();
        DisposeManagedResources();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (isDisposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        DisposeManagedResources();
        GC.SuppressFinalize(this);
    }

    private static UdpClient CreateClient(int localPort)
    {
        var client = new UdpClient(AddressFamily.InterNetwork);

        try
        {
            client.Client.ReceiveBufferSize = SocketBufferSize;
            client.Client.SendBufferSize = SocketBufferSize;
            client.Client.Bind(new IPEndPoint(IPAddress.Any, localPort));
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async ValueTask SetBufferRepliesEnabledCoreAsync(bool enabled, CancellationToken cancellationToken)
    {
        await SendCommandAsync(
            LaserCubeProtocol.CreateBooleanCommand(LaserCubeProtocol.EnableBufferReplies, enabled),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask SendCommandAsync(
        ReadOnlyMemory<byte> command,
        CancellationToken cancellationToken,
        int repeatCount = CommandRepeatCount)
    {
        await commandSendGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var client = commandClient ?? throw new InvalidOperationException("LaserCube has not been started.");
            for (var repetition = 0; repetition < repeatCount; repetition++)
            {
                var bytesSent = await client.SendAsync(command, commandEndPoint, cancellationToken).ConfigureAwait(false);
                if (bytesSent != command.Length)
                {
                    throw new IOException($"UDP socket accepted {bytesSent} of {command.Length} bytes.");
                }
            }
        }
        finally
        {
            commandSendGate.Release();
        }
    }

    private async Task ReceiveResponsesAsync(
        UdpClient client,
        bool isDataSocket,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var response = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (!response.RemoteEndPoint.Address.Equals(deviceAddress))
                {
                    continue;
                }

                ProcessResponse(response.Buffer, isDataSocket);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RecordTransportError(exception);
        }
    }

    private void ProcessResponse(ReadOnlySpan<byte> response, bool isDataSocket)
    {
        if (LaserCubeProtocol.TryParseStatus(response, out var parsedStatus))
        {
            lock (stateGate)
            {
                status = parsedStatus;
                dacRate = parsedStatus!.DacRate;
                estimatedBufferFree = Math.Max(0, parsedStatus.ReceiveBufferFree - queuedSampleCount);
            }

            return;
        }

        if (LaserCubeProtocol.TryParseBufferFree(response, out var bufferFree))
        {
            LaserCubeDataSender? sender;
            lock (stateGate)
            {
                estimatedBufferFree = Math.Max(0, bufferFree - queuedSampleCount);
                if (status is not null)
                {
                    status = status with { ReceiveBufferFree = bufferFree };
                }

                sender = dataSender;
            }

            if (isDataSocket)
            {
                sender?.ReportDataFeedback();
            }
        }
    }

    private async ValueTask StopCoreAsync()
    {
        LaserCubeDataSender? sender;
        lock (stateGate)
        {
            if (!isStarted)
            {
                return;
            }

            isStarted = false;
            sender = dataSender;
            dataSender = null;
            estimatedBufferFree = 0;
            queuedSampleCount = 0;
        }

        if (sender is not null)
        {
            await sender.DisposeAsync().ConfigureAwait(false);
        }

        using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        try
        {
            await SendCommandAsync(
                LaserCubeProtocol.CreateBooleanCommand(LaserCubeProtocol.SetOutput, false),
                shutdownTimeout.Token).ConfigureAwait(false);
            await SendCommandAsync(
                new byte[] { LaserCubeProtocol.ClearRingBuffer },
                shutdownTimeout.Token).ConfigureAwait(false);
            await SetBufferRepliesEnabledCoreAsync(false, shutdownTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            RecordTransportError(exception);
        }

        var cancellation = receiveCancellation;
        var commandTask = commandReceiveTask;
        var dataTask = dataReceiveTask;

        cancellation?.Cancel();
        commandClient?.Dispose();
        dataClient?.Dispose();

        if (commandTask is not null && dataTask is not null)
        {
            await Task.WhenAll(commandTask, dataTask).ConfigureAwait(false);
        }

        cancellation?.Dispose();
        receiveCancellation = null;
        commandReceiveTask = null;
        dataReceiveTask = null;
        commandClient = null;
        dataClient = null;

        lock (stateGate)
        {
            estimatedBufferFree = 0;
            queuedSampleCount = 0;
        }
    }

    private async ValueTask SendDataPacketAsync(
        UdpClient client,
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken)
    {
        var bytesSent = await client.SendAsync(packet, dataEndPoint, cancellationToken).ConfigureAwait(false);
        if (bytesSent != packet.Length)
        {
            throw new IOException($"UDP socket accepted {bytesSent} of {packet.Length} bytes.");
        }
    }

    private uint GetDacRate()
    {
        lock (stateGate)
        {
            return dacRate;
        }
    }

    private LaserCubeDataSender GetDataSender()
    {
        lock (stateGate)
        {
            return dataSender ?? throw new InvalidOperationException("Call StartAsync before communicating with the LaserCube.");
        }
    }

    private void OnSamplesSent(int count)
    {
        lock (stateGate)
        {
            queuedSampleCount = Math.Max(0, queuedSampleCount - count);
        }
    }

    private void OnSamplesDropped(int count) => ReleaseQueuedReservation(count);

    private void ReleaseQueuedReservation(int count)
    {
        lock (stateGate)
        {
            queuedSampleCount = Math.Max(0, queuedSampleCount - count);
            estimatedBufferFree = isStarted
                ? Math.Min(ushort.MaxValue, estimatedBufferFree + count)
                : 0;
        }
    }

    private void RecordTransportError(Exception exception)
    {
        lock (stateGate)
        {
            lastTransportError = exception;
        }
    }

    private void ThrowIfNotStarted()
    {
        ThrowIfDisposed();

        lock (stateGate)
        {
            if (!isStarted)
            {
                throw new InvalidOperationException("Call StartAsync before communicating with the LaserCube.");
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
    }

    private void DisposeManagedResources()
    {
        isDisposed = true;
        lifecycleGate.Dispose();
        commandSendGate.Dispose();
    }
}
