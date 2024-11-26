using LaserCubeSharp.Structs;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace LaserCubeSharp
{
    public class LaserCube : IDisposable
    {
        private static int CMD_PORT = 45457;
        private static int DATA_PORT = 45458;

        private IPEndPoint endPointCommand = new(IPAddress.Broadcast, CMD_PORT);
        private IPEndPoint endPointData = new(IPAddress.Broadcast, DATA_PORT);
        public string RemoteEndPoint
        {
            get => endPointCommand.Address.ToString();
            set
            {
                endPointCommand.Address = IPAddress.Parse(value);
                endPointData.Address = IPAddress.Parse(value);
                Console.WriteLine($"LASERCUBE | Set IP Address : {value}");
            }

        }

        private UdpClient senderClient = null;

        public bool senderValid => senderClient != null;

        /* ______________________________________________________________________ */

        private CancellationTokenSource cts = new CancellationTokenSource();

        private LaserConfiguration configuration;
        public LaserConfiguration GetConfigData() => configuration;

        private uint currentFrame;
        private const int FRAME_LOOP = 600;
        public uint GetFrame => currentFrame;

        /// <summary>
        /// This is the number of points that make up a packet. The size should be such that it fits within the MTU of your network (one point is 10 bytes).
        /// </summary>
        public int ChunkSize = 146;

        /// <summary>
        /// The buffer limit after which packets will no longer be sent. Typically, the free buffer is a very dynamic value and is constantly updated as packets are exchanged.
        /// </summary>
        public int FreeBufferLimit = 1000;

        private TimeSpan delayPacketSpan = TimeSpan.FromMilliseconds(1);
        private TimeSpan delayMessageSpan = TimeSpan.FromMilliseconds(0);

        ///<summary>
        ///Sets the delay between sending packets generated from chunks.
        ///</summary>
        ///<remarks>
        ///default is 1000 microseconds or 1ms
        ///</remarks>
        public void SetDelayPacket(uint microseconds)
        {
            delayPacketSpan = TimeSpan.FromMicroseconds(microseconds);
        }

        ///<summary>
        ///Sets the delay between sending an entire buffer.
        ///</summary>
        ///<remarks>
        ///default is 0 microseconds or 0ms
        ///</remarks>
        public void SetDelayMessage(uint microseconds)
        {
            delayMessageSpan = TimeSpan.FromMicroseconds(microseconds);
        }

        Subject<bool> dataSendedSubject = new Subject<bool>();
        ///<summary>
        ///Can be used as an event source to indicate when an entire buffer has been sent.
        ///</summary>
        public IObservable<bool> GetDataSended() => dataSendedSubject.AsObservable();


        /* ______________________________________________________________________ */

        private LaserPoint[] points = [];
        public LaserPoint[] GetPoints() => points;
        public void SetPoints(IEnumerable<LaserPoint> pts)
        {

            points = Utils.CopyToBuffer<LaserPoint>(pts);
        }

        /* ______________________________________________________________________ */
        public LaserCube()
        {
            Console.WriteLine("LASERCUBE | * Creating...");


            CreateListener(CMD_PORT);
            CreateListener(DATA_PORT);

            CreateSender();

        }

        public void Dispose()
        {
            cts.Cancel();
            Console.WriteLine("LASERCUBE | Dispose");
        }

        //public async Task<bool> ReceiveConfiguration()
        //{
        //    if (senderValid)
        //    {
        //        try
        //        {
        //            var result = await senderClient.ReceiveAsync(cts.Token);
        //            updateConfiguration(result.Buffer);
        //            return true;
        //        }
        //        catch (Exception e) { Console.WriteLine($"LASERCUBE | {e.Message}"); return false; }
        //    }
        //    return false;
        //}

        ///<summary>
        ///Request device status by sending a special command
        ///</summary>
        public async Task<bool> RequestConfiguration()
        {
            if (senderValid)
            {
                try
                {
                    await senderClient.SendAsync(new byte[] { (byte)CMD.GET_FULL_INFO }, endPointCommand, cts.Token);
                    return true;
                }
                catch (Exception e) { Console.WriteLine($"LASERCUBE | {e.Message}"); return false; }
            }
            return false;
        }

        ///<summary>
        ///Not really a very important thing, but "Enable" flag will hide from you the ability to enter the menu via the button on the back of the device.
        ///</summary>
        public async Task<bool> SendEnableOutput()
        {
            if (senderValid)
            {
                try
                {
                    await senderClient.SendAsync(new byte[] { (byte)CMD.ENABLE_BUFFER_SIZE_RESPONSE_ON_DATA, (byte)CMD.OUTPUT_ENABLE }, endPointCommand, cts.Token);
                    await senderClient.SendAsync(new byte[] { (byte)CMD.SET_OUTPUT, (byte)CMD.OUTPUT_ENABLE }, endPointCommand, cts.Token);
                    return true;
                }
                catch (Exception e) { Console.WriteLine($"LASERCUBE | {e.Message}"); return false; }
            }
            return false;
        }

        ///<summary>
        ///Turns off a special flag that allows you to access the menu using the back button on the device.
        ///</summary>
        public async Task<bool> SendDisableOutput()
        {
            if (senderValid)
            {
                try
                {
                    await senderClient.SendAsync(new byte[] { (byte)CMD.ENABLE_BUFFER_SIZE_RESPONSE_ON_DATA, (byte)CMD.OUTPUT_DISABLE }, endPointCommand, cts.Token);
                    await senderClient.SendAsync(new byte[] { (byte)CMD.SET_OUTPUT, (byte)CMD.OUTPUT_DISABLE }, endPointCommand, cts.Token);
                    return true;
                }
                catch (Exception e) { Console.WriteLine($"LASERCUBE | {e.Message}"); ; return false; }
            }
            return false;
        }

        ///<summary>
        ///Sends a special command to the device to set the DACRate value.
        ///</summary>
        public async Task<bool> SetDACRate(uint SampleRate)
        {
            if (senderValid)
            {
                try
                {
                    List<byte> data = [(byte)CMD.SET_RATE, .. BitConverter.GetBytes(SampleRate)];
                    await senderClient.SendAsync(data.ToArray(), endPointCommand, cts.Token);
                    return true;
                }
                catch (Exception e) { Console.WriteLine($"LASERCUBE | {e.Message}"); ; return false; }
            }
            return false;
        }

        private async Task PreciseDelay(Stopwatch stopwatch, TimeSpan timeSpan, CancellationToken ct)
        {
            if (timeSpan.Ticks != 0)
            {
                stopwatch.Restart();
                var elapsedTicks = stopwatch.ElapsedTicks;
                var targetTicks = (timeSpan.TotalMicroseconds * Stopwatch.Frequency) / 1_000_000;

                while (elapsedTicks < targetTicks && !ct.IsCancellationRequested)
                {
                    elapsedTicks = stopwatch.ElapsedTicks;
                }
            }

            await Task.Yield();
        }

        /// <summary>
        /// Sender Task
        /// </summary>
        /// <remarks>
        /// <para>1. the point buffer must be chunked. If you exceed the MTU of your network by one chunk, the network will drop those packets. 146 points per chunk fits into the standard 1500 bytes per packet. But in general, if you experiment with the size of the chunks enough, you'll find that reducing the size can also be useful.</para>
        /// <para>2. Chunks are sent to the device with a delay between sends equal to 'PacketDelayMicroseconds'.</para>
        /// <para>3. If the buffer is smaller than the specified limit, the chunk is not sent, it is discarded.</para>
        /// <para>4. The cycle is repeated with the delay specified in 'MessageDelayMicroseconds', incrementing the frame number.</para>
        /// </remarks>
        async Task SendData(UdpClient senderClient, List<byte> message)
        {
            if (currentFrame % FRAME_LOOP == 0)
            {
                await RequestConfiguration();
            }

            var stopwatch = new Stopwatch();

            if (points.Count() > 0)
            {
                var chunks = points.Chunk(ChunkSize).ToArray();

                for (int chunk = 0; chunk < chunks.Length; chunk++)
                {
                    message.AddRange([(byte)CMD.FIRST_DATA, 0, (byte)(chunk % 0xFF), (byte)(currentFrame % 0xFF)]);

                    for (int point = 0; point < chunks[chunk].Length; point++)
                    {
                        message.AddRange(Utils.LaserPointToByte(chunks[chunk][point]));
                    }

                    if (GetConfigData().RXBufferFree > FreeBufferLimit)
                    {
                        await senderClient.SendAsync(message.ToArray(), endPointData, cts.Token);
                    }
                    message.Clear();
                    await PreciseDelay(stopwatch, delayPacketSpan, cts.Token);
                    //await Task.Delay(delayPacketSpan); // imposible to use microseconds due to low temporal resolution
                }

                if (currentFrame % 2 == 0)
                {
                    await senderClient.SendAsync(new byte[] { (byte)CMD.GET_RINGBUFFER_EMPTY_SAMPLE_COUNT }, endPointCommand, cts.Token);
                }
            }
            currentFrame = (currentFrame + 1) % FRAME_LOOP;
            await PreciseDelay(stopwatch, delayMessageSpan, cts.Token);
            //await Task.Delay(delayMessageSpan); // imposible to use microseconds due to low temporal resolution
            if (points.Count() == 0)
            {
                await Task.Delay(100, cts.Token);
            }
        }

        private void CreateSender()
        {
            Console.WriteLine("LASERCUBE | CreateSender");

            var task = Task.Run(async () =>
            {
                try
                {
                    senderClient = new UdpClient();

                    senderClient.Client.ExclusiveAddressUse = false;
                    senderClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    senderClient.Client.Bind(new IPEndPoint(IPAddress.Any, CMD_PORT));

                    //await SendEnableOutput();
                    List<byte> message = new List<byte>();

                    while (!cts.IsCancellationRequested)
                    {
                        await SendData(senderClient, message);
                        dataSendedSubject.OnNext(true);
                    }

                }
                catch (Exception ex)
                {
                    Console.WriteLine($"LASERCUBE | Sender : {ex.Message}");
                    dataSendedSubject.OnError(ex);
                }
                finally
                {
                    if (senderClient != null)
                    {
                        await SendDisableOutput();
                        senderClient.Dispose();
                        senderClient = null;
                    }
                    dataSendedSubject.OnCompleted();
                }
            });
        }


        public void updateConfiguration(byte[] data)
        {
            if (data.Length > 0)
            {
                switch (data[0])
                {
                    case (byte)CMD.GET_FULL_INFO:
                        configuration = Utils.ParseResponse<LaserConfiguration>(data);
                        break;

                    case (byte)CMD.GET_RINGBUFFER_EMPTY_SAMPLE_COUNT:
                        configuration.RXBufferFree = Utils.ParseResponse<LaserRingBufferResponse>(data).RXBufferFree;
                        break;
                }
            }
        }

        private void CreateListener(int port)
        {
            Console.WriteLine($"LASERCUBE | CreateListener : {port}");
            Task.Run(async () =>
            {
                try
                {
                    using (var client = new UdpClient())
                    {
                        client.Client.ExclusiveAddressUse = false;
                        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                        client.Client.Bind(new IPEndPoint(IPAddress.Any, port));

                        while (!cts.IsCancellationRequested)
                        {
                            var result = await client.ReceiveAsync(cts.Token);
                            var data = result.Buffer;
                            updateConfiguration(data);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"LASERCUBE | Listener {port} : {ex.Message}");
                }
            });

            Console.WriteLine($"LASERCUBE | Listener Has Been Created : {port}");

        }
    }
}
