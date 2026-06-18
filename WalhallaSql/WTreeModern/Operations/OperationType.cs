namespace WTreeModern.Operations;

public enum OperationType : byte
{
    /// <summary>Schlüssel einfügen oder überschreiben.</summary>
    Upsert = 1,
    /// <summary>Schlüssel löschen (kein Fehler falls nicht vorhanden).</summary>
    Delete = 2,
}
