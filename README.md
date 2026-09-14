# LaserCubeSharp

Minimal .NET 8 library for controlling a network LaserCube over UDP.

> Lasers are dangerous. Use suitable safety equipment and a controlled environment.
> Output is never enabled automatically by this library.

## Usage

```csharp
using LaserCubeSharp;

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

`StartAsync` opens one bidirectional command socket on UDP port `45457` and one
bidirectional data socket on `45458`. It disables output, clears stale samples,
enables buffer feedback, and requests device status.

One `SendFrameAsync` call submits the supplied frame at most once. It returns `false`
instead of sending when the latest buffer estimate cannot safely accept the whole
frame. A frame is limited to 2800 points: 20 packets of at most 140 points each.

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
