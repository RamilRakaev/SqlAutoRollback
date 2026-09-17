using System.Text.Json;
using System.IO;

namespace SqlAutoRollback.Services;

public interface IServerHistoryStore
{
    IReadOnlyList<string> GetAll();

    void Remember(string serverName);
}

public sealed class ServerHistoryStore : IServerHistoryStore
{
    private const int MaxServers = 30;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public IReadOnlyList<string> GetAll()
    {
        try
        {
            if (!File.Exists(AppPaths.KnownServers))
            {
                return [];
            }

            var json = File.ReadAllText(AppPaths.KnownServers);
            var servers = JsonSerializer.Deserialize<List<string>>(json, JsonOptions);
            return servers?
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    public void Remember(string serverName)
    {
        var trimmed = serverName.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        var servers = GetAll().ToList();
        servers.RemoveAll(name => string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase));
        servers.Insert(0, trimmed);
        if (servers.Count > MaxServers)
        {
            servers = servers.Take(MaxServers).ToList();
        }

        AppPaths.EnsureCreated();
        File.WriteAllText(AppPaths.KnownServers, JsonSerializer.Serialize(servers, JsonOptions));
    }
}
