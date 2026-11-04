using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace URemote.Media;

// Average-rate token bucket plus a peak-rate limit. Static desktops earn a
// bounded burst allowance, matching the encoder's 500 ms VBV. A large refresh
// frame must not be stretched over hundreds of milliseconds just because the
// previous frames used almost no bandwidth.
internal sealed class VideoPacketPacer(long frequency)
{
    private bool started;
    private long last;
    private double tokens, peakDeadline;

    internal TimeSpan Reserve(int bytes, int bitrate, long now, bool onLink, bool allowBurst = true)
    {
        if (frequency <= 0 || bytes is < 1 or > 65536 || bitrate is < 1000000 or > 40000000)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        var rate = bitrate * 1.3 / 8;
        var capacity = allowBurst ? rate * .5 : 0;
        if (!started)
        {
            tokens = capacity; last = now; peakDeadline = now; started = true;
        }
        else
        {
            tokens = Math.Min(capacity, tokens + Math.Max(0, now - last) * rate / frequency);
            last = Math.Max(last, now);
        }
        tokens -= bytes;
        var averageDue = now + Math.Max(0, -tokens) * frequency / rate;
        // On-link UDP can use short smooth bursts; routed/relay traffic gets
        // a smaller allowance. Loss feedback temporarily disables all bursts.
        var peakRate = !allowBurst ? rate : onLink ? Math.Max(rate, 10000000) : Math.Min(20000000, rate * 4);
        peakDeadline = Math.Max(now, peakDeadline) + bytes * frequency / peakRate;
        return TimeSpan.FromSeconds(Math.Max(0, Math.Max(averageDue, peakDeadline) - now) / frequency);
    }

    internal static bool IsOnLink(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up
                && n.NetworkInterfaceType is not (NetworkInterfaceType.Tunnel or NetworkInterfaceType.Loopback)))
                foreach (var local in adapter.GetIPProperties().UnicastAddresses)
                    if (local.Address.AddressFamily == address.AddressFamily && local.PrefixLength > 0
                        && MatchesPrefix(local.Address.GetAddressBytes(), address.GetAddressBytes(), local.PrefixLength)) return true;
        }
        catch (NetworkInformationException) { }
        return false;
    }

    internal static bool MatchesPrefix(ReadOnlySpan<byte> local, ReadOnlySpan<byte> remote, int bits)
    {
        if (local.Length != remote.Length || bits < 1 || bits > local.Length * 8) return false;
        var full = bits / 8;
        if (!local[..full].SequenceEqual(remote[..full])) return false;
        var remaining = bits % 8;
        return remaining == 0 || ((local[full] ^ remote[full]) & (255 << (8 - remaining))) == 0;
    }
}
