using SIPSorcery.Net;
namespace URemote.Media;

// RFC 6184 single-NAL / FU-A payloads. Leave room for RTP, SRTP, TURN and tunnel headers.
public static class H264RtpPayloads
{
    public const int MaxPayload = 1100;
    public static IEnumerable<(byte[] Bytes, bool Last)> Create(byte[] accessUnit)
    {
        if (accessUnit.Length is 0 or > 8000000) throw new ArgumentException("Invalid access unit size.");
        foreach (var nal in H264Packetiser.ParseNals(accessUnit))
        {
            if (nal.NAL.Length <= MaxPayload) { yield return (nal.NAL, nal.IsLast); continue; }
            for (var offset = 1; offset < nal.NAL.Length; offset += MaxPayload - 2)
            {
                var count = Math.Min(MaxPayload - 2, nal.NAL.Length - offset);
                var last = offset + count == nal.NAL.Length;
                var payload = new byte[count + 2];
                payload[0] = (byte)((nal.NAL[0] & 0xe0) | 28);
                payload[1] = (byte)((nal.NAL[0] & 31) | (offset == 1 ? 0x80 : 0) | (last ? 0x40 : 0));
                nal.NAL.AsSpan(offset, count).CopyTo(payload.AsSpan(2));
                yield return (payload, nal.IsLast && last);
            }
        }
    }
}
