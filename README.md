# LaserCubeSharp

Minimal .NET 8 library for controlling a network LaserCube over UDP.

> Lasers are dangerous. Use suitable safety equipment and a controlled environment.
> Output is never enabled automatically by this library.

## Usage

```csharp
using LaserCubeSharp;

// Discovery is optional: use a known address directly when it is configured.
await using var laser = new LaserCube("192.168.1.42");
await laser.StartAsync();

await laser.SetDacRateAsync(30_000);
await laser.SetOutputEnabledAsync(true);

// A deliberately blank two-packet frame. Replace it with a safely prepared scan.
var frame = Enumerable
    .Repeat(new LaserPoint(2048, 2048, 0, 0, 0), 280)
    .ToArray();

if (!await laser.SendFrameAsync(frame))
{
    // The device did not report enough free buffer space. Retry later.
}

await laser.SetOutputEnabledAsync(false);
```

## Device discovery

`DiscoverAsync` sends the original LaserCube alive (`0x27`) and full-info (`0x77`)
requests through every suitable active non-loopback IPv4 network interface and
listens for one second by default. Results are deduplicated by the source IP
address. The timeout can be changed, for example with
`LaserCube.DiscoverAsync(TimeSpan.FromSeconds(2))`.

Each result contains the address from which the cube replied and its decoded
status:

```csharp
using LaserCubeSharp;

var devices = await LaserCube.DiscoverAsync(
    timeout: TimeSpan.FromSeconds(2),
    cancellationToken);

foreach (var device in devices)
{
    Console.WriteLine(
        $"{device.Status.ModelName} {device.Status.SerialNumber} at {device.Address}");
}
```

An empty list means that no cube replied before the timeout. Discovery supports
multiple cubes and multiple active network adapters. Broadcast traffic must be
allowed by the operating system, firewall, access point, and VLAN configuration.

```csharp
var device = devices.FirstOrDefault()
    ?? throw new InvalidOperationException("No LaserCube was found.");

await using var laser = device.CreateClient();
await laser.StartAsync();
```

Applications that need a specific cube can select it by `Address` or
`Status.SerialNumber`.

`LaserCubeDevice.Address` is the source address from which the response was
received and is the address used by `CreateClient()`. `Status.Address` is the
address reported by the device firmware and may be stale while the network is
being reconfigured.

Discovery is not required. Use `new LaserCube("192.168.1.42")` or
`new LaserCube(IPAddress.Parse("192.168.1.42"))` whenever the application already
knows the device address.

## Connection and streaming

`StartAsync` opens one bidirectional command socket on UDP port `45457` and one
bidirectional data socket on `45458`. It disables output, clears stale samples,
enables buffer feedback, and requests device status.

`SendFrameAsync` atomically accepts the supplied samples into a session-local queue.
It returns `false` without accepting any samples when the latest buffer estimate
cannot safely accept the whole set. Accepted samples are copied, retain their exact
order and values, and are packetized independently of call boundaries.

The sender combines consecutive short sets into packets of up to 140 samples. A
partial packet waits for at most the playback duration of one full packet, clamped
to 1-10 ms. Transport bursts contain at most 20 packets. Buffer replies allow the
next burst to proceed as soon as the device has processed the previous one; a 10 ms
recovery timeout is used when replies are unavailable. `message_number` advances
per packet, while `frame_number` identifies a transport burst rather than a
`SendFrameAsync` call.

`StopAsync` and disposal attempt to disable output before closing the sockets.
Applications should still provide their own physical safety and emergency-stop
procedure.

## Device status

The latest validated 64-byte status response is available through `laser.Status`.
`laser.EstimatedBufferFree` includes the library's local reservation of submitted
points. Invalid, failed, truncated, and unknown-version responses are ignored.

## Protocol checks

The repository includes dependency-free checks for packet encoding and response
parsing:

```powershell
dotnet run --project LaserCubeSharp.ProtocolChecks -c Release
```

These checks do not verify behavior against physical hardware.
