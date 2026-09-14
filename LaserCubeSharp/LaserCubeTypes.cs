using System.Net;

namespace LaserCubeSharp;

public enum LaserCubeConnectionType : byte
{
    Unknown = 1,
    EthernetServer = 2,
    WiFiServer = 3,
    EthernetClient = 4,
    WiFiClient = 5,
}

public readonly record struct LaserPoint
{
    public const ushort MaximumValue = 0x0fff;

    public LaserPoint(ushort x, ushort y, ushort red, ushort green, ushort blue)
    {
        X = Validate(x, nameof(x));
        Y = Validate(y, nameof(y));
        Red = Validate(red, nameof(red));
        Green = Validate(green, nameof(green));
        Blue = Validate(blue, nameof(blue));
    }

    public ushort X { get; }

    public ushort Y { get; }

    public ushort Red { get; }

    public ushort Green { get; }

    public ushort Blue { get; }

    private static ushort Validate(ushort value, string parameterName)
    {
        if (value > MaximumValue)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"LaserCube point values must be between 0 and {MaximumValue}.");
        }

        return value;
    }
}

public sealed record LaserCubeStatus(
    byte PayloadVersion,
    byte FirmwareMajor,
    byte FirmwareMinor,
    bool OutputEnabled,
    bool InterlockEnabled,
    bool TemperatureWarning,
    bool OverTemperature,
    byte PacketErrorCount,
    uint DacRate,
    uint MaximumDacRate,
    ushort ReceiveBufferFree,
    ushort ReceiveBufferSize,
    byte BatteryPercent,
    sbyte TemperatureCelsius,
    LaserCubeConnectionType ConnectionType,
    string SerialNumber,
    IPAddress Address,
    byte ModelNumber,
    string ModelName);
