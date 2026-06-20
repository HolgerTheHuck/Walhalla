using DbUi.Core.Catalog;
using DbUi.Core.Connection;
using DbUi.Core.Workspace;

namespace DbUi.UI.Services;

public interface IDialogService
{
    /// <summary>
    /// Zeigt den Connect-Dialog und lädt dabei die gespeicherte Recent-Liste.
    /// </summary>
    WorkspaceConnectionInfo? ShowOpenDatabaseDialog(IConnectionStore connectionStore);

    /// <summary>
    /// Zeigt einen Dialog zum Erstellen einer neuen Embedded-Datenbank an.
    /// Legt das Verzeichnis an und liefert die Verbindungsinformationen oder <c>null</c>, wenn der Benutzer abbricht.
    /// </summary>
    WorkspaceConnectionInfo? ShowNewDatabaseDialog();

    string? ShowFolderBrowserDialog(string description);

    /// <summary>
    /// Zeigt einen SSMS-ähnlichen Dialog zum Erstellen einer neuen Tabelle an.
    /// Liefert das generierte CREATE TABLE-Skript oder <c>null</c>, wenn der Benutzer abbricht.
    /// </summary>
    string? ShowCreateTableDialog();

    /// <summary>
    /// Zeigt einen Dialog zum Erstellen eines neuen Indexes für die angegebene Tabelle an.
    /// Liefert das generierte CREATE INDEX-Skript oder <c>null</c>, wenn der Benutzer abbricht.
    /// </summary>
    string? ShowCreateIndexDialog(string tableName, IReadOnlyList<string> availableColumns);

    /// <summary>
    /// Zeigt einen Dialog zum Erstellen einer neuen Stored Procedure an.
    /// Liefert das generierte CREATE PROCEDURE-Skript oder <c>null</c>, wenn der Benutzer abbricht.
    /// </summary>
    string? ShowCreateProcedureDialog();

    /// <summary>
    /// Zeigt einen Dialog zum Erstellen eines neuen Triggers für die angegebene Tabelle an.
    /// Liefert das generierte CREATE TRIGGER-Skript oder <c>null</c>, wenn der Benutzer abbricht.
    /// </summary>
    string? ShowCreateTriggerDialog(string tableName);
}
