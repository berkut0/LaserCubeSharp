using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace LaserCubeSharp.Structs;

//public struct LaserCubeSettings
//{
//    public int ChunkSize = 146;
//    public int FreeBufferLimit = 1000;

//    public LaserCubeSettings()
//    {

//    }
//}
public enum CMD : byte
{
    FIRST_DATA = 0xa9,
    ZERO = 0x00,
    GET_FULL_INFO = 0x77,
    SET_RATE = 0x82,
    SET_OUTPUT = 0x80,
    GET_RINGBUFFER_EMPTY_SAMPLE_COUNT = 0x8a,
    ENABLE_BUFFER_SIZE_RESPONSE_ON_DATA = 0x78,
    CLEAR_RINGBUFFER = 0x8d,
    OUTPUT_ENABLE = 0x01,
    OUTPUT_DISABLE = 0x00
}

public enum ConnType : byte
{
    CON_UNKNOWN = 1,
    CON_ETHERNET_SERVER = 2,
    CON_WIFI_SERVER = 3,
    CON_ETHERNET_CLIENT = 4,
    CON_WIFI_CLIENT = 5
}
public record LaserPoint
{
    public ushort X { get; set; }
    public ushort Y { get; set; }
    public ushort R { get; set; }
    public ushort G { get; set; }
    public ushort B { get; set; }

    public LaserPoint(ushort x, ushort y, ushort r, ushort g, ushort b)
    {
        X = Math.Clamp(x, (ushort)0, (ushort)4095);
        Y = Math.Clamp(y, (ushort)0, (ushort)4095);
        R = Math.Clamp(r, (ushort)0, (ushort)4095); ;
        G = Math.Clamp(g, (ushort)0, (ushort)4095);
        B = Math.Clamp(b, (ushort)0, (ushort)4095);
    }

    public void ToByteArray(byte[] byteArray)
    {
        if (byteArray.Length < 10) throw new ArgumentException("Array must be of length 10 or greater");

        unsafe
        {
            fixed (byte* bytePtr = byteArray)
            {
                ushort* structPtr = (ushort*)bytePtr;
                structPtr[0] = X;
                structPtr[1] = Y;
                structPtr[2] = R;
                structPtr[3] = G;
                structPtr[4] = B;
            }
        }
    }

    public override string ToString()
    {
        return $"x:{X} r:{Y} r:{R} g:{G} b:{B}";
    }

    public byte[] ToBytes()
    {
        byte[] byteArray = new byte[10];
        ToByteArray(byteArray);
        return byteArray;
    }
}

[StructLayout(LayoutKind.Explicit, Pack = 1)]
public unsafe struct LaserRingBufferResponse
{
    [FieldOffset(0)]
    private readonly byte header;

    [FieldOffset(1)]
    private readonly byte number; // probablly indicates something and has two states: 1 and 0

    [FieldOffset(2)]
    public ushort RXBufferFree;

    public override string ToString()
    {
        return this.RXBufferFree.ToString();
    }
}

/// <summary>
/// The most important data structure that corresponds to the bytes that the laser sends.
/// </summary>
[StructLayout(LayoutKind.Explicit, Pack = 1)]
public unsafe struct LaserConfiguration
{
    [FieldOffset(0)]
    private readonly byte header;

    [FieldOffset(2)]
    public byte PayloadVersionID;

    [FieldOffset(3)]
    public byte FirmwareMajor;

    [FieldOffset(4)]
    public byte FirmwareMinor;

    [FieldOffset(5)]
    private readonly byte BitField;

    public bool OutputEnabled => (BitField & 0b_0000_0001) > 0;
    public bool LockEnabled => (BitField & 0b_0000_0010) > 0;
    public bool TemperatureWarning => (BitField & 0b_0000_0100) > 0;
    public bool OverTemperature => (BitField & 0b_0000_1000) > 0;
    public bool PacketErrors => (BitField & 0b_0001_0000) > 0;

    [FieldOffset(10)]
    public uint DACRate;

    [FieldOffset(14)]
    public uint MaxDACRate;

    [FieldOffset(19)]
    public ushort RXBufferFree;

    [FieldOffset(21)]
    public ushort RXBufferSize;

    [FieldOffset(23)]
    public byte BatteryPercent;

    [FieldOffset(24)]
    public byte Temperature;

    [FieldOffset(25)]
    public ConnType ConnectionType;

    [FieldOffset(26)]
    private fixed byte serialNumber[6];

    public string SerialNumber
    {
        get
        {          
            fixed (byte* ptr = serialNumber)
            {
                Span<byte> sn = new Span<byte>(ptr, 6);
                return Convert.ToHexString(sn);
            }
        }
    }


    [FieldOffset(32)]
    private fixed byte ip[4];

    public string IP
    {
        get
        {
            return $"{ip[0]}.{ip[1]}.{ip[2]}.{ip[3]}";      
        }
    }

    [FieldOffset(37)]
    public byte ModelNumber;


    [FieldOffset(38)]
    private fixed byte modelName[26];

    public string ModelName
    {
        get
        {
            fixed (byte* ptr = modelName)
            {
                return Encoding.UTF8.GetString(ptr, 26).Replace("\0", string.Empty);
            }

        }
    }

    public override string ToString()
    {
        Type structType = this.GetType();
        System.Reflection.FieldInfo[] fields = structType.GetFields();
        var builder = new StringBuilder();
        builder.Append($"OutputEnabled : {OutputEnabled}\r\n");
        builder.Append($"LockEnabled : {LockEnabled}\r\n");
        builder.Append($"TemperatureWarning : {TemperatureWarning}\r\n");
        builder.Append($"OverTemperature : {OverTemperature}\r\n");
        builder.Append($"PacketErrors : {PacketErrors}\r\n");
        foreach (var field in fields)
        {
            builder.Append(string.Format("{0} : {1}\r\n",
                                         field.Name,
                                         field.GetValue(this).ToString()));
        }
        builder.Append($"SerialNumber : {SerialNumber}\r\n");
        builder.Append($"ModelName : {ModelName}\r\n");
        return builder.ToString();
    }
}
