namespace URemote.Host;

// Published atomically by the UI; consumed only at the start of a permission generation.
public sealed record HostSessionOptions(bool Input, bool Audio, bool Clipboard, TimeSpan Duration, string[]? DisplayKeys);
