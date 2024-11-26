### LASERS ARE DANGEROUS. USE WITH CAUTION. THE AUTHOR IS NOT RESPONSIBLE FOR ANY MISUSE OF THIS CODE. IF YOU USE A LASER, YOU ACKNOWLEDGE THAT YOU ARE AWARE OF ALL PRECAUTIONS.

## Overview

The main motivation was to create code that could communicate with the [Wickedlaser LaserCube](https://www.laseros.com/lasercube/) in the [VVVV visual programming environment](https://visualprogramming.net/). Therefore, the code is not accompanied by a specific application. In a nutshell, when you create a LaserCube object, you create two 'listeners' and one 'sender'. You set the IP address, set the buffer, and the sender starts sending. In theory that already should work. There are a number of specific fine-tuning settings that can be optionally set.

The code is probably not perfect, but the [original C++ code](https://github.com/Wickedlasers/laserdocklib) is much more problematic because it is deeply tied to QT. There are [implementations in Python](https://gist.github.com/s4y/0675595c2ff5734e927d68caf652e3af), which inspired me to do this implementation in C#. My code has achieved a more granular setup that is suitable for fine-tuning and more complex configurations.

Pseudocode (not tested, but should work):

```C#
var laser = new LaserCube();
laser.RemoteEndPoint = "192.168.1.42";

// Be careful, the laser will not show anything if there is no RingBuffer running, there must be at least 2 chunks for this to work. This is probably done for safety reasons. Two chunks by default >150pts
var buffer = new List<LaserPoint>(256); //some points
laser.SetPoints(buffer);

// Optionals:
laser.SetDACRate(30000).Wait(); 
laser.RequestConfiguration().Wait();
laser.SendDisableOutput().Wait();
laser.SendEnableOutput().Wait();
laser.ChunkSize = 146;
laser.FreeBufferLimit = 1000;
laser.SetDelayPacket(1000);
laser.SetDelayMessage(0);

// Device Status Gathered from Bytes from Device
LaserConfiguration laserStatus = laser.GetConfigData();
Console.WriteLine(laserStatus.ToString());
```

## How it works

The main algorithm for the transmission is as follows:

1. the point buffer must be chunked. If you exceed your network's MTU by one chunk, the network will drop those packets. 146 points per chunk fits into the standard 1500 bytes per packet. But in general, if you experiment with chunk sizes enough, you'll find that reducing the size can also be useful.

2. Chunks are sent to the device with a delay between sends equal to 'PacketDelayMicroseconds'.

3. If the buffer is smaller than the specified limit, the chunk is not sent, it is discarded.

4. The cycle is repeated with the delay specified in 'MessageDelayMicroseconds', incrementing the frame number.

The algorithm for receiving information has two "listeners" because there are two ports to which the device can respond in a number of cases.

1. First, the algorithm sends a request every 600 buffers sent. The request is for a "configuration" that is stored in the object when it is received. This is the data that contains all the configuration fields, including the free buffer.

2. Second, the algorithm listens for bytes representing information about the current state of the ring buffer. These bytes carry data about the current free buffer that is received in response to sent point packets. This buffer sets the free buffer field to 'configuration'.