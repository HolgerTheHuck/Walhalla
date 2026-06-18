using System;

namespace DbUi.Core.Workspace;

public sealed class WorkspaceConnectionInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string DisplayName { get; set; } = string.Empty;

    public string StoragePath { get; set; } = string.Empty;

    public string DatabaseName { get; set; } = "App";

    public bool IsInMemory =>
        string.Equals(StoragePath?.Trim(), ":memory:", StringComparison.OrdinalIgnoreCase);
}
