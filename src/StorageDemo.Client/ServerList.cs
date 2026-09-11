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
        // The Talos lab cluster, reached on the address Cilium holds for the API Service. No
        // port-forward: the cluster hands out real addresses on the lab network.
        new("Talos lab", "http://10.10.10.122:5080"),
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

                    // A saved list is the user's, but a default added since it was written would
                    // otherwise never appear: the file exists, so nothing reads the defaults again.
                    list.Entries.AddRange(Defaults.Where(d =>
                        !loaded.Any(e => string.Equals(e.Address, d.Address, StringComparison.OrdinalIgnoreCase))));
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
