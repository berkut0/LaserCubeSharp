namespace LaserCubeSharp;

internal sealed class LaserCubePacketQueue
{
    private readonly Queue<LaserPoint> samples = new();
    private byte messageSequence;
    private byte frameSequence;
    private int packetsInBurst;

    internal int Count => samples.Count;

    internal int PacketsInBurst => packetsInBurst;

    internal void Enqueue(IReadOnlyList<LaserPoint> points)
    {
        for (var index = 0; index < points.Count; index++)
        {
            samples.Enqueue(points[index]);
        }
    }

    internal bool TryCreatePacket(bool flushPartial, out byte[]? packet, out int sampleCount)
    {
        packet = null;
        sampleCount = 0;

        if (samples.Count == 0 || (!flushPartial && samples.Count < LaserCubeProtocol.MaximumPointsPerPacket))
        {
            return false;
        }

        if (packetsInBurst >= LaserCubeProtocol.MaximumPacketsPerBurst)
        {
            throw new InvalidOperationException("Complete the current burst before creating another packet.");
        }

        sampleCount = Math.Min(samples.Count, LaserCubeProtocol.MaximumPointsPerPacket);
        var packetSamples = new LaserPoint[sampleCount];
        for (var index = 0; index < sampleCount; index++)
        {
            packetSamples[index] = samples.Dequeue();
        }

        packet = LaserCubeProtocol.CreateDataPacket(
            packetSamples,
            0,
            sampleCount,
            messageSequence,
            frameSequence);

        unchecked
        {
            messageSequence++;
        }

        packetsInBurst++;
        return true;
    }

    internal void CompleteBurst()
    {
        if (packetsInBurst == 0)
        {
            return;
        }

        unchecked
        {
            frameSequence++;
        }

        packetsInBurst = 0;
    }

    internal int Clear()
    {
        var count = samples.Count;
        samples.Clear();
        CompleteBurst();
        return count;
    }
}
