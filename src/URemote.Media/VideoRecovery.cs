using System.Buffers.Binary;
namespace URemote.Media;

public readonly record struct VideoFeedback(uint Ssrc, ushort Sequence, bool KeyFrame);
public static class VideoFeedbackParser
{
    public static double? RoundTripMilliseconds(uint compactNow, uint lastSenderReport, uint receiverDelay)
    {
        if (lastSenderReport == 0) return null;
        var elapsed = unchecked(compactNow - lastSenderReport - receiverDelay);
        // Reject clock discontinuities, stale reports and unavailable samples.
        return elapsed <= 60u * 65536u ? elapsed * 1000.0 / 65536.0 : null;
    }

    // Called only AFTER successful SRTCP authentication. Bound work even for malformed feedback.
    public static List<VideoFeedback> Parse(ReadOnlySpan<byte> compound)
    {
        var result = new List<VideoFeedback>();
        if (compound.Length > 65536) return result;
        while (compound.Length >= 4 && result.Count < 256)
        {
            if ((compound[0] >> 6) != 2) break;
            var size = (BinaryPrimitives.ReadUInt16BigEndian(compound[2..]) + 1) * 4;
            if (size > compound.Length || size < 4) break;
            var packet = compound[..size];
            if ((packet[0] & 32) != 0)
            {
                var padding = packet[^1];
                if (padding == 0 || padding > size - 4) break;
                packet = packet[..^padding];
            }
            var fmt = packet[0] & 31;
            if (packet.Length >= 12)
            {
                var ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet[8..]);
                if (packet[1] == 205 && fmt == 1 && (packet.Length - 12) % 4 == 0)
                    for (var pos = 12; pos + 4 <= packet.Length && result.Count < 256; pos += 4)
                    {
                        var pid = BinaryPrimitives.ReadUInt16BigEndian(packet[pos..]);
                        var mask = BinaryPrimitives.ReadUInt16BigEndian(packet[(pos + 2)..]);
                        result.Add(new(ssrc, pid, false));
                        for (var bit = 0; bit < 16 && result.Count < 256; bit++)
                            if ((mask & (1 << bit)) != 0) result.Add(new(ssrc, unchecked((ushort)(pid + bit + 1)), false));
                    }
                else if (packet[1] == 206 && fmt == 1 && packet.Length == 12)
                    result.Add(new(ssrc, 0, true));
                else if (packet[1] == 206 && fmt == 4 && (packet.Length - 12) % 8 == 0)
                    for (var pos = 12; pos + 8 <= packet.Length && result.Count < 256; pos += 8)
                        result.Add(new(BinaryPrimitives.ReadUInt32BigEndian(packet[pos..]), 0, true));
            }
            compound = compound[size..];
        }
        return result;
    }
}

public enum VideoRepairResult { Available, Missing, Expired, Throttled, RetryLimit, Closed }

// Retain exact protected bytes: no SRTP nonce reuse with modified headers or payload.
public sealed class ProtectedVideoCache : IDisposable
{
    private sealed class Entry(byte[] bytes, long created)
    { public byte[] Bytes = bytes; public long Created = created; public long LastRetry = long.MinValue; public int Retries; }
    private readonly Dictionary<(uint, ushort), Entry> entries = new();
    private readonly Queue<((uint, ushort) Key, Entry Value)> order = new();
    private readonly object gate = new();
    private readonly int capacity, byteLimit;
    private int bytes;
    private bool disposed;
    public ProtectedVideoCache(int capacity = 4096, int byteLimit = 8 * 1024 * 1024)
    { if (capacity < 1 || byteLimit < 1) throw new ArgumentOutOfRangeException(); this.capacity = capacity; this.byteLimit = byteLimit; }
    public void Store(ReadOnlySpan<byte> packet, long now)
    {
        if (packet.Length < 12 || packet.Length > 2048 || packet.Length > byteLimit || packet[0] >> 6 != 2) return;
        var key = (BinaryPrimitives.ReadUInt32BigEndian(packet[8..]), BinaryPrimitives.ReadUInt16BigEndian(packet[2..]));
        lock (gate)
        {
            if (disposed) return;
            Trim(now);
            if (entries.ContainsKey(key)) return;
            while (entries.Count >= capacity || bytes + packet.Length > byteLimit) Evict();
            var value = new Entry(packet.ToArray(), now); entries.Add(key, value); order.Enqueue((key, value)); bytes += value.Bytes.Length;
        }
    }
    public byte[]? Take(uint ssrc, ushort sequence, long now) => Take(ssrc, sequence, now, out _, out _);
    public byte[]? Take(uint ssrc, ushort sequence, long now, out VideoRepairResult result, out long ageMs)
    {
        lock (gate)
        {
            ageMs = -1;
            if (disposed) { result = VideoRepairResult.Closed; return null; }
            entries.TryGetValue((ssrc, sequence), out var value);
            Trim(now);
            if (value is null) { result = VideoRepairResult.Missing; return null; }
            ageMs = now - value.Created;
            if (ageMs > 1500) { result = VideoRepairResult.Expired; return null; }
            if (value.Retries >= 3) { result = VideoRepairResult.RetryLimit; return null; }
            if (value.Retries > 0 && now - value.LastRetry < 80)
            { result = VideoRepairResult.Throttled; return null; }
            value.Retries++; value.LastRetry = now;
            result = VideoRepairResult.Available;
            return value.Bytes.ToArray();
        }
    }
    private void Trim(long now) { while (order.TryPeek(out var item) && now - item.Value.Created > 1500) Evict(); }
    private void Evict()
    {
        var item = order.Dequeue(); entries.Remove(item.Key); bytes -= item.Value.Bytes.Length; Array.Clear(item.Value.Bytes);
    }
    public void Dispose() { lock (gate) { disposed = true; while (order.Count > 0) Evict(); } }
}
