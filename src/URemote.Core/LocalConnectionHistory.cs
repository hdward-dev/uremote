using System.Text.Json;

namespace URemote.Core;

// The persisted type deliberately has no password property.
public sealed record LocalConnectionRecord(LocalDesktopProtocol Protocol, string Host, int Port, string Domain, string Username)
{
    public LocalDesktopOptions Options(string password = "") => new(Protocol, Host, Port, Username, password, Domain);
    public static LocalConnectionRecord From(LocalDesktopOptions options) => new(options.Protocol, options.Host, options.Port,
        options.Protocol == LocalDesktopProtocol.Rdp ? options.Domain : "", options.Protocol == LocalDesktopProtocol.Rdp ? options.Username : "");
    public bool Matches(LocalConnectionRecord other) => Protocol == other.Protocol && Port == other.Port &&
        string.Equals(Host, other.Host, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Domain, other.Domain, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Username, other.Username, StringComparison.OrdinalIgnoreCase);
}

public sealed class LocalConnectionHistory(string path)
{
    public IReadOnlyList<LocalConnectionRecord> Load()
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 65536) return [];
            var records = JsonSerializer.Deserialize<LocalConnectionRecord?[]>(File.ReadAllText(path)) ?? [];
            var result = new List<LocalConnectionRecord>();
            foreach (var record in records)
            {
                if (record is null) continue;
                try { record.Options().Validate(); } catch (ArgumentException) { continue; }
                if (!result.Any(record.Matches)) result.Add(record);
                if (result.Count == 5) break;
            }
            return result;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    public IReadOnlyList<LocalConnectionRecord> Remember(LocalDesktopOptions options)
    {
        options.Validate();
        var record = LocalConnectionRecord.From(options);
        var records = new[] { record }.Concat(Load().Where(previous => !record.Matches(previous))).Take(5).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(records)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return records;
    }
}
