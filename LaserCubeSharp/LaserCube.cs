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
    private readonly SemaphoreSlim dataSendGate = new(1, 1);
    private readonly object stateGate = new();

    private UdpClient? commandClient;
    private UdpClient? dataClient;
    private CancellationTokenSource? receiveCancellation;
    private Task? commandReceiveTask;
    private Task? dataReceiveTask;
    private LaserCubeStatus? status;
    private Exception? lastTransportError;
    private int estimatedBufferFree;
    private int minimumBufferFree = 1000;
    private byte frameSequence;
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

            var newCommandClient = CreateClient(LaserCubeProtocol.CommandPort, commandEndPoint);
            UdpClient? newDataClient = null;

            try
            {
                newDataClient = CreateClient(LaserCubeProtocol.DataPort, dataEndPoint);
            }
            catch
            {
                newCommandClient.Dispose();
                throw;
            }

            commandClient = newCommandClient;
            dataClient = newDataClient;
            receiveCancellation = new CancellationTokenSource();
            commandReceiveTask = ReceiveResponsesAsync(newCommandClient, receiveCancellation.Token);
            dataReceiveTask = ReceiveResponsesAsync(newDataClient, receiveCancellation.Token);

            lock (stateGate)
            {
                status = null;
                estimatedBufferFree = 0;
                lastTransportError = null;
                isStarted = true;
            }

            frameSequence = 0;

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
    }

    public async ValueTask SetOutputEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ThrowIfNotStarted();
        await SendCommandAsync(
            LaserCubeProtocol.CreateBooleanCommand(LaserCubeProtocol.SetOutput, enabled),
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
        await SendCommandAsync(new byte[] { LaserCubeProtocol.ClearRingBuffer }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> SendFrameAsync(
        IReadOnlyList<LaserPoint> points,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(points);
        ThrowIfNotStarted();

        if (points.Count == 0)
        {
            return true;
        }

        if (points.Count > LaserCubeProtocol.MaximumPointsPerFrame)
        {
            throw new ArgumentException(
                $"A frame cannot exceed {LaserCubeProtocol.MaximumPointsPerFrame} points.",
                nameof(points));
        }

        await dataSendGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var reserved = false;
            lock (stateGate)
            {
                if (estimatedBufferFree - points.Count >= minimumBufferFree)
                {
                    estimatedBufferFree -= points.Count;
                    reserved = true;
                }
            }

            if (!reserved)
            {
                await RequestBufferFreeAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            var client = dataClient ?? throw new InvalidOperationException("LaserCube has not been started.");
            var offset = 0;
            byte messageSequence = 0;

            while (offset < points.Count)
            {
                var count = Math.Min(LaserCubeProtocol.MaximumPointsPerPacket, points.Count - offset);
                var packet = LaserCubeProtocol.CreateDataPacket(
                    points,
                    offset,
                    count,
                    messageSequence,
                    frameSequence);

                var bytesSent = await client.SendAsync(packet, cancellationToken).ConfigureAwait(false);
                if (bytesSent != packet.Length)
                {
                    throw new IOException($"UDP socket accepted {bytesSent} of {packet.Length} bytes.");
                }

                offset += count;
                unchecked
                {
                    messageSequence++;
                }
            }

            unchecked
            {
                frameSequence++;
            }

            return true;
        }
        finally
        {
            dataSendGate.Release();
        }
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

    private static UdpClient CreateClient(int localPort, IPEndPoint remoteEndPoint)
    {
        var client = new UdpClient(AddressFamily.InterNetwork);

        try
        {
            client.Client.ReceiveBufferSize = SocketBufferSize;
            client.Client.SendBufferSize = SocketBufferSize;
            client.Client.Bind(new IPEndPoint(IPAddress.Any, localPort));
            client.Connect(remoteEndPoint);
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
                var bytesSent = await client.SendAsync(command, cancellationToken).ConfigureAwait(false);
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

    private async Task ReceiveResponsesAsync(UdpClient client, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var response = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                ProcessResponse(response.Buffer);
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

    private void ProcessResponse(ReadOnlySpan<byte> response)
    {
        if (LaserCubeProtocol.TryParseStatus(response, out var parsedStatus))
        {
            lock (stateGate)
            {
                status = parsedStatus;
                estimatedBufferFree = parsedStatus!.ReceiveBufferFree;
            }

            return;
        }

        if (LaserCubeProtocol.TryParseBufferFree(response, out var bufferFree))
        {
            lock (stateGate)
            {
                estimatedBufferFree = bufferFree;
                if (status is not null)
                {
                    status = status with { ReceiveBufferFree = bufferFree };
                }
            }
        }
    }

    private async ValueTask StopCoreAsync()
    {
        if (!isStarted)
        {
            return;
        }

        await dataSendGate.WaitAsync().ConfigureAwait(false);

        try
        {
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
                isStarted = false;
                estimatedBufferFree = 0;
            }
        }
        finally
        {
            dataSendGate.Release();
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
        dataSendGate.Dispose();
    }
}
