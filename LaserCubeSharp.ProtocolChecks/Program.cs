using System.Buffers.Binary;
using System.Net;
using System.Text;
using LaserCubeSharp;

var checks = new (string Name, Action Run)[]
{
    ("point validation", CheckPointValidation),
    ("command encoding", CheckCommandEncoding),
    ("data packet encoding", CheckDataPacketEncoding),
    ("packet point limit", CheckPacketPointLimit),
    ("buffer response parsing", CheckBufferResponseParsing),
    ("full status parsing", CheckStatusParsing),
    ("invalid status rejection", CheckInvalidStatusRejection),
};

var failures = 0;
foreach (var check in checks)
{
    try
    {
        check.Run();
        Console.WriteLine($"PASS  {check.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL  {check.Name}: {exception.Message}");
    }
}

return failures == 0 ? 0 : 1;

static void CheckPointValidation()
{
    _ = new LaserPoint(0, 1, 2, 3, LaserPoint.MaximumValue);
    Throws<ArgumentOutOfRangeException>(() => new LaserPoint(0x1000, 0, 0, 0, 0));
}

static void CheckCommandEncoding()
{
    var rate = LaserCubeProtocol.CreateSetRateCommand(30_000);
    Equal(5, rate.Length);
    Equal((byte)0x82, rate[0]);
    Equal(30_000u, BinaryPrimitives.ReadUInt32LittleEndian(rate.AsSpan(1)));

    var output = LaserCubeProtocol.CreateBooleanCommand(LaserCubeProtocol.SetOutput, true);
    Equal(new byte[] { 0x80, 0x01 }, output);
}

static void CheckDataPacketEncoding()
{
    LaserPoint[] points =
    [
        new(0x001, 0x234, 0x567, 0x89a, 0xbcd),
        new(0xfff, 0, 1, 2, 3),
    ];

    var packet = LaserCubeProtocol.CreateDataPacket(points, 0, points.Length, 255, 255);

    Equal(24, packet.Length);
    Equal((byte)0xa9, packet[0]);
    Equal((byte)0, packet[1]);
    Equal((byte)255, packet[2]);
    Equal((byte)255, packet[3]);
    Equal((ushort)0x001, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(4, 2)));
    Equal((ushort)0x234, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(6, 2)));
    Equal((ushort)0xbcd, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(12, 2)));
    Equal((ushort)0xfff, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(14, 2)));
}

static void CheckPacketPointLimit()
{
    var maximum = Enumerable.Repeat(new LaserPoint(0, 0, 0, 0, 0), 140).ToArray();
    Equal(1404, LaserCubeProtocol.CreateDataPacket(maximum, 0, maximum.Length, 0, 0).Length);

    var tooMany = Enumerable.Repeat(new LaserPoint(0, 0, 0, 0, 0), 141).ToArray();
    Throws<ArgumentOutOfRangeException>(() =>
        LaserCubeProtocol.CreateDataPacket(tooMany, 0, tooMany.Length, 0, 0));
}

static void CheckBufferResponseParsing()
{
    var response = new byte[] { 0x8a, 0x00, 0x88, 0x13 };
    True(LaserCubeProtocol.TryParseBufferFree(response, out var bufferFree));
    Equal((ushort)5000, bufferFree);

    response[1] = 1;
    False(LaserCubeProtocol.TryParseBufferFree(response, out _));
}

static void CheckStatusParsing()
{
    var response = new byte[64];
    response[0] = 0x77;
    response[1] = 0;
    response[2] = 0;
    response[3] = 1;
    response[4] = 24;
    response[5] = 0x5f;
    BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(10, 4), 30_000);
    BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(14, 4), 45_000);
    BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(19, 2), 5_000);
    BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(21, 2), 6_000);
    response[23] = 80;
    response[24] = unchecked((byte)-5);
    response[25] = (byte)LaserCubeConnectionType.EthernetServer;
    new byte[] { 1, 2, 3, 4, 5, 6 }.CopyTo(response, 26);
    new byte[] { 192, 168, 1, 42 }.CopyTo(response, 32);
    response[37] = 10;
    Encoding.UTF8.GetBytes("LaserCube Ultra").CopyTo(response, 38);

    True(LaserCubeProtocol.TryParseStatus(response, out var status));
    NotNull(status);
    Equal((byte)1, status!.FirmwareMajor);
    Equal((byte)24, status.FirmwareMinor);
    True(status.OutputEnabled);
    True(status.InterlockEnabled);
    True(status.TemperatureWarning);
    True(status.OverTemperature);
    Equal((byte)5, status.PacketErrorCount);
    Equal(30_000u, status.DacRate);
    Equal((ushort)5_000, status.ReceiveBufferFree);
    Equal((sbyte)-5, status.TemperatureCelsius);
    Equal(IPAddress.Parse("192.168.1.42"), status.Address);
    Equal("010203040506", status.SerialNumber);
    Equal("LaserCube Ultra", status.ModelName);
}

static void CheckInvalidStatusRejection()
{
    False(LaserCubeProtocol.TryParseStatus(new byte[63], out _));

    var response = new byte[64];
    response[0] = 0x77;
    response[1] = 1;
    False(LaserCubeProtocol.TryParseStatus(response, out _));

    response[1] = 0;
    response[2] = 1;
    False(LaserCubeProtocol.TryParseStatus(response, out _));
}

static void True(bool value)
{
    if (!value)
    {
        throw new InvalidOperationException("Expected true.");
    }
}

static void False(bool value) => True(!value);

static void NotNull(object? value)
{
    if (value is null)
    {
        throw new InvalidOperationException("Expected a non-null value.");
    }
}

static void Equal<T>(T expected, T actual)
{
    if (expected is Array expectedArray && actual is Array actualArray)
    {
        if (expectedArray.Length == actualArray.Length &&
            expectedArray.Cast<object>().SequenceEqual(actualArray.Cast<object>()))
        {
            return;
        }

        throw new InvalidOperationException("Arrays are not equal.");
    }

    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}

static void Throws<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}
