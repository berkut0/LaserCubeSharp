using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LaserCubeSharp;

internal static class LaserCubeDiscovery
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(1);

    public static async Task<IReadOnlyList<LaserCubeDevice>> DiscoverAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var discoveryTimeout = timeout ?? DefaultTimeout;
        if (discoveryTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The discovery timeout must be greater than zero.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var interfaces = GetIPv4Interfaces();
        var sockets = new List<UdpClient>();
        var interfaceSockets = new List<UdpClient>();
        var devices = new ConcurrentDictionary<IPAddress, LaserCubeDevice>();
        using var receiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            var mainSocket = CreateSocket(IPAddress.Any, 0);
            sockets.Add(mainSocket);

            var aliveSocket = TryCreateAliveSocket();
            if (aliveSocket is not null)
            {
                sockets.Add(aliveSocket);
            }

            foreach (var networkInterface in interfaces)
            {
                try
                {
                    var socket = CreateSocket(networkInterface.Address, 0);
                    sockets.Add(socket);
                    interfaceSockets.Add(socket);
                }
                catch (SocketException)
                {
                    // Some VPN and virtual adapters cannot be used for UDP broadcast.
                }
            }

            var receiveTasks = sockets
                .Select(socket => ReceiveResponsesAsync(socket, devices, receiveCancellation.Token))
                .ToArray();

            SocketException?[] receiveErrors;
            try
            {
                var successfulRequests = await SendDiscoveryRequestsAsync(
                    mainSocket,
                    aliveSocket ?? mainSocket,
                    interfaceSockets,
                    interfaces,
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                if (successfulRequests == 0)
                {
                    throw new IOException("LaserCube discovery could not send a request on any network interface.");
                }

                await Task.Delay(discoveryTimeout, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                receiveCancellation.Cancel();
                receiveErrors = await Task.WhenAll(receiveTasks).ConfigureAwait(false);
            }

            if (devices.IsEmpty &&
                receiveErrors.Length > 0 &&
                receiveErrors.All(error => error is not null))
            {
                throw new IOException(
                    "LaserCube discovery could not receive responses on any network interface.",
                    new AggregateException(receiveErrors!));
            }
        }
        finally
        {
            foreach (var socket in sockets)
            {
                socket.Dispose();
            }
        }

        return devices.Values
            .OrderBy(device => device.Address.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<int> SendDiscoveryRequestsAsync(
        UdpClient mainSocket,
        UdpClient aliveSocket,
        IReadOnlyList<UdpClient> interfaceSockets,
        IReadOnlyList<IPv4Interface> interfaces,
        CancellationToken cancellationToken)
    {
        var successfulRequests = 0;

        for (var repetition = 0; repetition < 2; repetition++)
        {
            successfulRequests += await TrySendAsync(
                mainSocket,
                LaserCubeProtocol.GetFullInfo,
                new IPEndPoint(IPAddress.Broadcast, LaserCubeProtocol.CommandPort),
                cancellationToken).ConfigureAwait(false);
            successfulRequests += await TrySendAsync(
                aliveSocket,
                LaserCubeProtocol.GetAlive,
                new IPEndPoint(IPAddress.Broadcast, LaserCubeProtocol.AlivePort),
                cancellationToken).ConfigureAwait(false);

            foreach (var networkInterface in interfaces)
            {
                var directedBroadcast = networkInterface.BroadcastAddress;

                successfulRequests += await TrySendAsync(
                    mainSocket,
                    LaserCubeProtocol.GetFullInfo,
                    new IPEndPoint(directedBroadcast, LaserCubeProtocol.CommandPort),
                    cancellationToken).ConfigureAwait(false);
                successfulRequests += await TrySendAsync(
                    aliveSocket,
                    LaserCubeProtocol.GetAlive,
                    new IPEndPoint(directedBroadcast, LaserCubeProtocol.AlivePort),
                    cancellationToken).ConfigureAwait(false);
            }

            foreach (var interfaceSocket in interfaceSockets)
            {
                successfulRequests += await TrySendAsync(
                    interfaceSocket,
                    LaserCubeProtocol.GetFullInfo,
                    new IPEndPoint(IPAddress.Broadcast, LaserCubeProtocol.CommandPort),
                    cancellationToken).ConfigureAwait(false);
                successfulRequests += await TrySendAsync(
                    interfaceSocket,
                    LaserCubeProtocol.GetAlive,
                    new IPEndPoint(IPAddress.Broadcast, LaserCubeProtocol.AlivePort),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return successfulRequests;
    }

    private static async Task<SocketException?> ReceiveResponsesAsync(
        UdpClient socket,
        ConcurrentDictionary<IPAddress, LaserCubeDevice> devices,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult response;
            try
            {
                response = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionReset)
            {
                // Windows can surface ICMP "port unreachable" responses from broadcasts.
                continue;
            }
            catch (SocketException exception)
            {
                return exception;
            }

            if (LaserCubeProtocol.IsAliveResponse(response.Buffer))
            {
                await TrySendAsync(
                    socket,
                    LaserCubeProtocol.GetFullInfo,
                    new IPEndPoint(response.RemoteEndPoint.Address, LaserCubeProtocol.CommandPort),
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (LaserCubeProtocol.TryParseStatus(response.Buffer, out var status))
            {
                devices[response.RemoteEndPoint.Address] =
                    new LaserCubeDevice(response.RemoteEndPoint.Address, status!);
            }
        }

        return null;
    }

    private static async ValueTask<int> TrySendAsync(
        UdpClient socket,
        byte command,
        IPEndPoint destination,
        CancellationToken cancellationToken)
    {
        try
        {
            await socket.SendAsync(new byte[] { command }, destination, cancellationToken).ConfigureAwait(false);
            return 1;
        }
        catch (SocketException)
        {
            // Broadcast is expected to fail on some VPN and virtual adapters.
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
    }

    private static UdpClient CreateSocket(IPAddress localAddress, int localPort)
    {
        var client = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            client.EnableBroadcast = true;
            client.Client.ExclusiveAddressUse = false;
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.Client.Bind(new IPEndPoint(localAddress, localPort));
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static UdpClient? TryCreateAliveSocket()
    {
        try
        {
            return CreateSocket(IPAddress.Any, LaserCubeProtocol.AlivePort);
        }
        catch (SocketException)
        {
            // Discovery still works through full-info broadcasts if another process owns the alive port.
            return null;
        }
    }

    private static IReadOnlyList<IPv4Interface> GetIPv4Interfaces()
    {
        var interfaces = new List<IPv4Interface>();

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            IPInterfaceProperties properties;
            try
            {
                properties = networkInterface.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            foreach (var address in properties.UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork ||
                    address.IPv4Mask is null)
                {
                    continue;
                }

                interfaces.Add(new IPv4Interface(
                    address.Address,
                    GetBroadcastAddress(address.Address, address.IPv4Mask)));
            }
        }

        return interfaces;
    }

    internal static IPAddress GetBroadcastAddress(IPAddress address, IPAddress subnetMask)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(subnetMask);

        var addressBytes = address.GetAddressBytes();
        var maskBytes = subnetMask.GetAddressBytes();
        if (addressBytes.Length != 4 || maskBytes.Length != 4)
        {
            throw new ArgumentException("An IPv4 address and subnet mask are required.");
        }

        var broadcastBytes = new byte[4];
        for (var index = 0; index < broadcastBytes.Length; index++)
        {
            broadcastBytes[index] = (byte)(addressBytes[index] | ~maskBytes[index]);
        }

        return new IPAddress(broadcastBytes);
    }

    private sealed record IPv4Interface(IPAddress Address, IPAddress BroadcastAddress);
}
