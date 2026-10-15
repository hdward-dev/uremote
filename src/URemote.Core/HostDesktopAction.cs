namespace URemote.Core;

// Official Mac simple-action values, confirmed by an authorized client capture.
public enum HostDesktopAction { ShowDesktop = 2, ShowWindows = 3 }

public static class HostDesktopActions
{
    public static HostDesktopAction? Decode(string channel, bool isText, ReadOnlyMemory<byte> packet)
    {
        if (channel != "CONTROL_DATA_CHANNEL" || isText || packet.Length == 0 || packet.Length > 8192
            || packet.Span[0] == (byte)'{') return null;
        var envelope = ProtoFields.Read(packet);
        if (envelope.Items.Count(f => f.Tag == 3) != 1 || envelope.Bytes(3) is not { } simple) return null;
        var fields = ProtoFields.Read(simple);
        var actions = fields.Items.Where(f => f.Tag == 1).ToArray();
        if (actions.Length != 1 || actions[0].WireType != 0) return null;
        return actions[0].Value switch { 2 => HostDesktopAction.ShowDesktop, 3 => HostDesktopAction.ShowWindows, _ => null };
    }
}
