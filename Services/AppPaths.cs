using System.IO;

namespace SqlAutoRollback.Services;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SqlAutoRollback");

    public static string Scripts { get; } = Path.Combine(Root, "Scripts");

    public static string History { get; } = Path.Combine(Root, "History");

    public static string HistoryDatabase { get; } = Path.Combine(Root, "history.db");

    public static string Credentials { get; } = Path.Combine(Root, "credentials.dat");

    public static string KnownServers { get; } = Path.Combine(Root, "servers.json");

    public static string GetHistoryFilePath(string serverName)
    {
        return Path.Combine(History, SanitizeFileName(serverName) + ".sql");
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Scripts);
        Directory.CreateDirectory(History);
    }

    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "server" : sanitized;
    }
}
