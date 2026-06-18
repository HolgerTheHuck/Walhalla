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

        var json = await File.ReadAllTextAsync(_filePath);
        return JsonSerializer.Deserialize<List<WorkspaceConnectionInfo>>(json, JsonOptions) ?? [];
    }

    public async Task SaveAsync(IEnumerable<WorkspaceConnectionInfo> connections)
    {
        var json = JsonSerializer.Serialize(connections.ToList(), JsonOptions);
        await File.WriteAllTextAsync(_filePath, json);
    }
}
