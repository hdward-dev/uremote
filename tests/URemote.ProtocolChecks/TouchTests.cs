using System.Buffers.Binary;
using URemote.Core;
static class TouchTests
{
    public static void Run(Action<bool,string> check)
    {
        byte[] Point(uint id, float x, float y) {
            var coords = new byte[10]; coords[0] = 21; coords[5] = 29;
            BinaryPrimitives.WriteSingleLittleEndian(coords.AsSpan(1), x);
            BinaryPrimitives.WriteSingleLittleEndian(coords.AsSpan(6), y);
            return FileTransferProtocol.Join(FileTransferProtocol.Int(1,id), coords);
        }
        byte[] Packet(int phase, params byte[][] points) => FileTransferProtocol.Blob(20, FileTransferProtocol.Blob(1,
            FileTransferProtocol.Join(FileTransferProtocol.Int(1,(ulong)phase),
                FileTransferProtocol.Join(points.Select(p => FileTransferProtocol.Blob(2,p)).ToArray()))));
        var input = HostTouchProtocol.Decode(Packet(1,Point(0,.25f,.75f),Point(123,.5f,.5f)));
        check(input is { Phase: 1, Points.Count: 2 } && input.Points[0].Id == 0 && input.Points[1].Id == 123 && input.Points[0].Y == .75,
            "official touch envelope preserves simultaneous contacts and zero contact ID");
        check(HostTouchProtocol.Decode(Packet(4)) is { Phase: 4, Points.Count: 0 }, "touch cancellation can release all contacts");
        check(HostTouchProtocol.Decode(Packet(0)) is { Phase: 0, Points.Count: 0 }, "initial touch state is not a press");
        check(HostTouchProtocol.Decode("{\"action\":\"mouse_click\"}"u8.ToArray()) is null, "JSON mouse input bypasses touch parser");
        foreach(var packet in new[] { Packet(9), Packet(1,Point(0,float.NaN,0)), Packet(1,Point(0,2,0)),
            Packet(1,Point(0,0,0),Point(0,1,1)), Packet(1,Enumerable.Range(0,17).Select(i=>Point((uint)i,0,0)).ToArray()), Packet(1,Point(0,0,0))[..^1] })
        {
            try { HostTouchProtocol.Decode(packet); check(false,"invalid touch rejected"); }
            catch(FormatException) { check(true,"invalid touch rejected"); }
        }
        var request = FileTransferProtocol.Blob(13, []);
        var reply = HostTouchProtocol.ReplyMetrics(request, 1, true)!;
        check(reply.SequenceEqual(new byte[]{8,1,106,4,26,2,8,1}), "touch support uses official SystemMetrics general capability field");
        check(HostTouchProtocol.ReplyMetrics(reply, 2, true) is null, "metrics replies cannot form a response loop");
        var state = new TouchFrameState();
        check(state.Apply(input!).Count == 2, "simultaneous contacts retained in one batch");
        check(state.Apply(input!).All(c => c.Kind == TouchChangeKind.Motion), "repeated down updates existing contacts without lift/repress");
        try { state.Apply(new(2,[new(0,.5,.5),new(1,double.NaN,0)])); check(false,"invalid batch"); }
        catch (FormatException) { check(state.Reset().Count == 2, "invalid batch does not mutate active contacts"); }
        check(state.Apply(new(2,[new(0,.5,.5)])).Count == 0, "move after reset cannot start a gesture on another screen");
        state.Apply(input!);
        check(state.Apply(new(3,[])).Count == 2 && state.Reset().Count == 0, "empty touch end releases all slots exactly once");
        state.Apply(input!);
        check(state.Apply(new(4,[])).Count == 2, "cancel releases all active contacts");
        check(!input!.ToString().Contains("0.25"), "touch diagnostics redact coordinates");
    }
}
