using System.IO;
using System.Text.Json;

namespace StorageDemo.Client;

public sealed record ServerEntry(string Name, string Address)
{
    public override string ToString() => $"{Name}  —  {Address}";
}

/// <summary>
/// The endpoints the client can switch between, kept in the user's profile so the list survives
/// a rebuild. One client, many servers: Windows, Linux, Docker Compose, a port-forwarded cluster.
/// </summary>
public sealed class ServerList
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "StorageDemoClient",
        "servers.json");

    private static readonly ServerEntry[] Defaults =
    [
        new("Local (dotnet run)", "http://127.0.0.1:5080"),
        new("Docker Compose", "http://127.0.0.1:5081"),
        new("Kubernetes (port-forward)", "http://127.0.0.1:5082"),
    ];

    public List<ServerEntry> Entries { get; private set; } = [.. Defaults];

    public static ServerList Load()
    {
        var list = new ServerList();

        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<List<ServerEntry>>(File.ReadAllText(FilePath));
                if (loaded is { Count: > 0 })
                {
                    list.Entries = loaded;
                }
            }
        }
        catch (Exception)
        {
            // A corrupt or unreadable file is not worth failing startup over; fall back to defaults.
        }

        return list;
    }

    /// <summary>Remembers an address the user typed, so it is one click away next time.</summary>
    public void Remember(string address)
    {
        if (Entries.Any(e => string.Equals(e.Address, address, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        Entries.Add(new ServerEntry(address, address));
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(
                FilePath,
                JsonSerializer.Serialize(Entries, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Losing the recents list is a nuisance, not a failure worth interrupting the user for.
        }
    }
}
