namespace WTreeModern.Storage;

/// <summary>
/// Abstraktion für den persistenten Block-Speicher.
/// Jeder Block hat einen permanenten Long-Handle.
/// Blöcke haben variable Größe – kein Page-Padding nötig.
/// </summary>
public interface IBlockStore : IDisposable
{
    /// <summary>Anzahl der bisher allozierten Handles (= nächster freier Handle-Wert).</summary>
    long AllocatedCount { get; }

    /// <summary>Reserviert einen neuen eindeutigen Handle. Idempotent bei Neuanlage.</summary>
    long AllocateHandle();

    /// <summary>Liest den gespeicherten Byte-Inhalt eines Blocks.</summary>
    byte[] Read(long handle);

    /// <summary>Schreibt (oder überschreibt) den Inhalt eines Blocks.</summary>
    void Write(long handle, byte[] data, int offset, int length);

    /// <summary>Gibt an ob ein Handle bereits beschrieben wurde.</summary>
    bool Exists(long handle);

    /// <summary>Leert ausstehende Schreiboperationen atomar auf das Medium.</summary>
    void Commit();

    /// <summary>Schließt den Store sauber (ohne Commit).</summary>
    void Close();
}
