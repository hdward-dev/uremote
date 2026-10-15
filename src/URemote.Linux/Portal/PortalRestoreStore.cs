using System.Security.Cryptography;
using System.Text;

namespace URemote.Linux;

// Restore tokens are single-use capabilities. Never put them in logs or settings exports.
internal sealed class PortalRestoreStore
{
    private readonly string path;
    internal PortalRestoreStore(bool input, string? directory = null)
    {
        var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (string.IsNullOrEmpty(state) || !Path.IsPathFullyQualified(state))
            state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
        directory ??= Path.Combine(state, "asterdock", "uremote", "portal");
        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "unknown";
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(desktop)))[..16];
        path = Path.Combine(directory, (input ? "control-" : "view-") + scope + ".token");
    }
    internal string? Take()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            if (!File.Exists(path)) return null;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
            var mode = File.GetUnixFileMode(path);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0) { File.Delete(path); return null; }
            if (new FileInfo(path).Length > 8192) { File.Delete(path); return null; }
            var token = File.ReadAllText(path);
            File.Delete(path); // A failed/canceled restore must not reuse a consumed token.
            return Valid(token) ? token : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { Console.WriteLine("portal-restore-store-unavailable"); return null; }
    }
    internal void Save(string? token)
    {
        if (!OperatingSystem.IsLinux()) return;
        if (!Valid(token)) return;
        string? temporary = null;
        try
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid token directory.");
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var file = new FileStream(temporary, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            using (var writer = new StreamWriter(file)) writer.Write(token);
            File.Move(temporary, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { Console.WriteLine("portal-restore-store-unavailable"); }
        finally { if (temporary is not null) { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
    }
    private static bool Valid(string? token) => !string.IsNullOrWhiteSpace(token) && token.Length <= 4096 && !token.Any(char.IsControl);
}
