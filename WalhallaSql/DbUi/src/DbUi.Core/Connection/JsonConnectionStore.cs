using System.Text.Json;
using DbUi.Core.Workspace;

namespace DbUi.Core.Connection;

public class JsonConnectionStore : IConnectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;

    public JsonConnectionStore()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "DbUi");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "connections.json");
    }

    public async Task<IReadOnlyList<WorkspaceConnectionInfo>> LoadAsync()
    {
        if (!File.Exists(_filePath))
            return [];

        try
        {
            var json = await File.ReadAllTextAsync(_filePath);
            return JsonSerializer.Deserialize<List<WorkspaceConnectionInfo>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public async Task SaveAsync(IEnumerable<WorkspaceConnectionInfo> connections)
    {
        var json = JsonSerializer.Serialize(connections.ToList(), JsonOptions);
        await File.WriteAllTextAsync(_filePath, json);
    }

    public async Task<WorkspaceConnectionInfo?> LoadLastAsync()
    {
        var connections = await LoadAsync();
        return connections.FirstOrDefault();
    }

    public async Task SaveRecentAsync(WorkspaceConnectionInfo connection, int maxEntries = 10)
    {
        var connections = (await LoadAsync()).ToList();

        // Bestehenden Eintrag mit gleicher Id entfernen
        connections.RemoveAll(c => string.Equals(c.Id, connection.Id, StringComparison.OrdinalIgnoreCase));

        // Äquivalente vorhandene Einträge (gleicher Modus + gleiche Schlüsselwerte) deduplizieren
        bool IsSameConnection(WorkspaceConnectionInfo a, WorkspaceConnectionInfo b)
        {
            if (a.Mode != b.Mode) return false;
            return a.Mode switch
            {
                WorkspaceConnectionMode.Local =>
                    string.Equals(a.StoragePath, b.StoragePath, StringComparison.OrdinalIgnoreCase),
                WorkspaceConnectionMode.PgWire =>
                    string.Equals(a.PgWireHost, b.PgWireHost, StringComparison.OrdinalIgnoreCase) &&
                    a.PgWirePort == b.PgWirePort &&
                    string.Equals(a.PgWireDatabase, b.PgWireDatabase, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(a.PgWireUser, b.PgWireUser, StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }

        connections.RemoveAll(c => IsSameConnection(c, connection));

        // Neuen Eintrag an den Anfang setzen
        connections.Insert(0, connection);

        if (connections.Count > maxEntries)
            connections = connections.Take(maxEntries).ToList();

        await SaveAsync(connections);
    }
}
