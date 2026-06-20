using System;

namespace DbUi.Core.Workspace;

public enum WorkspaceConnectionMode
{
    Local,
    PgWire
}

public sealed class WorkspaceConnectionInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string DisplayName { get; set; } = string.Empty;

    public WorkspaceConnectionMode Mode { get; set; } = WorkspaceConnectionMode.Local;

    // Lokaler Embedded-Modus
    public string StoragePath { get; set; } = string.Empty;

    public string DatabaseName { get; set; } = "App";

    public bool IsInMemory =>
        Mode == WorkspaceConnectionMode.Local &&
        string.Equals(StoragePath?.Trim(), ":memory:", StringComparison.OrdinalIgnoreCase);

    // PgWire-Modus
    public string PgWireHost { get; set; } = "localhost";

    public int PgWirePort { get; set; } = 5432;

    public string PgWireUser { get; set; } = string.Empty;

    public string PgWirePassword { get; set; } = string.Empty;

    public string PgWireDatabase { get; set; } = string.Empty;
}
