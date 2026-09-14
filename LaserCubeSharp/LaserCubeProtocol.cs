using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace LaserCubeSharp;

internal static class LaserCubeProtocol
{
    internal const int CommandPort = 45457;
    internal const int DataPort = 45458;
    internal const int FullInfoResponseLength = 64;
    internal const int BufferFreeResponseLength = 4;
    internal const int PointSize = 10;
    internal const int DataHeaderSize = 4;
    internal const int MaximumPointsPerPacket = 140;
    internal const int MaximumPacketsPerFrame = 20;
    internal const int MaximumPointsPerFrame = MaximumPointsPerPacket * MaximumPacketsPerFrame;

    internal const byte GetFullInfo = 0x77;
    internal const byte EnableBufferReplies = 0x78;
    internal const byte SetOutput = 0x80;
    internal const byte SetRate = 0x82;
    internal const byte GetBufferFree = 0x8a;
    internal const byte ClearRingBuffer = 0x8d;
    internal const byte SampleData = 0xa9;

    internal static byte[] CreateBooleanCommand(byte command, bool enabled) =>
        [command, enabled ? (byte)1 : (byte)0];

    internal static byte[] CreateSetRateCommand(uint rate)
    {
        var command = new byte[5];
        command[0] = SetRate;
        BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(1), rate);
        return command;
    }

    internal static byte[] CreateDataPacket(
        IReadOnlyList<LaserPoint> points,
        int offset,
        int count,
        byte messageSequence,
        byte frameSequence)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (offset < 0 || count < 0 || offset > points.Count - count)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (count is 0 or > MaximumPointsPerPacket)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                count,
                $"A data packet must contain between 1 and {MaximumPointsPerPacket} points.");
        }

        var packet = new byte[DataHeaderSize + (count * PointSize)];
        packet[0] = SampleData;
        packet[1] = 0;
        packet[2] = messageSequence;
        packet[3] = frameSequence;

        var destination = packet.AsSpan(DataHeaderSize);
        for (var index = 0; index < count; index++)
        {
            var point = points[offset + index];
            var pointBytes = destination.Slice(index * PointSize, PointSize);
            BinaryPrimitives.WriteUInt16LittleEndian(pointBytes[0..2], point.X);
            BinaryPrimitives.WriteUInt16LittleEndian(pointBytes[2..4], point.Y);
            BinaryPrimitives.WriteUInt16LittleEndian(pointBytes[4..6], point.Red);
            BinaryPrimitives.WriteUInt16LittleEndian(pointBytes[6..8], point.Green);
            BinaryPrimitives.WriteUInt16LittleEndian(pointBytes[8..10], point.Blue);
        }

        return packet;
    }

    internal static bool TryParseBufferFree(ReadOnlySpan<byte> response, out ushort bufferFree)
    {
        bufferFree = 0;

        if (response.Length != BufferFreeResponseLength ||
            response[0] != GetBufferFree ||
            response[1] != 0)
        {
            return false;
        }

        bufferFree = BinaryPrimitives.ReadUInt16LittleEndian(response[2..4]);
        return true;
    }

    internal static bool TryParseStatus(ReadOnlySpan<byte> response, out LaserCubeStatus? status)
    {
        status = null;

        if (response.Length != FullInfoResponseLength ||
            response[0] != GetFullInfo ||
            response[1] != 0 ||
            response[2] != 0)
        {
            return false;
        }

        var firmwareMajor = response[3];
        var firmwareMinor = response[4];
        var flags = response[5];
        var usesCurrentFlags = firmwareMajor > 0 || firmwareMinor >= 13;

        var modelNameBytes = response[38..64];
        var terminator = modelNameBytes.IndexOf((byte)0);
        if (terminator >= 0)
        {
            modelNameBytes = modelNameBytes[..terminator];
        }

        status = new LaserCubeStatus(
            PayloadVersion: response[2],
            FirmwareMajor: firmwareMajor,
            FirmwareMinor: firmwareMinor,
            OutputEnabled: (flags & 0x01) != 0,
            InterlockEnabled: (flags & (usesCurrentFlags ? 0x02 : 0x08)) != 0,
            TemperatureWarning: (flags & (usesCurrentFlags ? 0x04 : 0x10)) != 0,
            OverTemperature: (flags & (usesCurrentFlags ? 0x08 : 0x20)) != 0,
            PacketErrorCount: usesCurrentFlags ? (byte)(flags >> 4) : (byte)0,
            DacRate: BinaryPrimitives.ReadUInt32LittleEndian(response[10..14]),
            MaximumDacRate: BinaryPrimitives.ReadUInt32LittleEndian(response[14..18]),
            ReceiveBufferFree: BinaryPrimitives.ReadUInt16LittleEndian(response[19..21]),
            ReceiveBufferSize: BinaryPrimitives.ReadUInt16LittleEndian(response[21..23]),
            BatteryPercent: response[23],
            TemperatureCelsius: unchecked((sbyte)response[24]),
            ConnectionType: (LaserCubeConnectionType)response[25],
            SerialNumber: Convert.ToHexString(response[26..32]),
            Address: new IPAddress(response[32..36]),
            ModelNumber: response[37],
            ModelName: Encoding.UTF8.GetString(modelNameBytes));

        return true;
    }
}
