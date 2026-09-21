using URemote.Core;

static class DisplayInfoTests
{
    public static void Run(Action<bool, string> check)
    {
        var envelope = Fields(HostDisplayInfo.Encode(1, 2, 2));
        var screenList = Fields(envelope.Single(f => f.Tag == 7).Bytes);
        var screens = screenList.Where(f => f.Tag == 1).Select(f => Fields(f.Bytes)).ToArray();
        check(screens.Length == 2 && screens[0].Single(f => f.Tag == 12).Value == 0
            && screens[1].Single(f => f.Tag == 12).Value == 1
            && screens[0].Single(f => f.Tag == 1).Value == 0 && screens[1].Single(f => f.Tag == 1).Value == 1,
            "each advertised screen references its distinct videoTrackIndex (field 12)");
        var mobile = Fields(Fields(HostDisplayInfo.Encode(1, 2, 2, singleVideoStream: true)).Single(f => f.Tag == 7).Bytes)
            .Where(f => f.Tag == 1).Select(f => Fields(f.Bytes)).ToArray();
        check(mobile.Length == 2 && mobile.All(s => s.Single(f => f.Tag == 12).Value == 0)
            && mobile[0].Single(f => f.Tag == 1).Value == 0 && mobile[1].Single(f => f.Tag == 1).Value == 1,
            "iOS display list preserves physical screen ids while routing both to primary video track");
    }
    // Independent minimal wire reader for the synthetic screen-list fixture; not the production parser.
    private static List<(int Tag, ulong Value, byte[] Bytes)> Fields(byte[] bytes)
    {
        var position = 0;
        ulong Var()
        {
            ulong value = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                var b = bytes[position++]; value |= (ulong)(b & 127) << shift;
                if ((b & 128) == 0) return value;
            }
            throw new Exception("Invalid fixture varint.");
        }
        var fields = new List<(int, ulong, byte[])>();
        while (position < bytes.Length)
        {
            var key = Var(); var tag = (int)(key >> 3); var wire = (int)(key & 7);
            if (wire == 0) fields.Add((tag, Var(), []));
            else
            {
                var length = wire switch { 1 => 8, 2 => checked((int)Var()), 5 => 4, _ => throw new Exception("Invalid fixture wire type.") };
                var value = bytes.AsSpan(position, length).ToArray(); position += length;
                fields.Add((tag, 0, value));
            }
        }
        return fields;
    }
}
