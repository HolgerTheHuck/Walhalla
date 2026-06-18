using WTreeModern.Diagnostics;

namespace WTreeModern.Tree;

/// <summary>
/// Gemeinsames Interface für <see cref="InternalNode{TKey,TValue}"/> und
/// <see cref="LeafNode{TKey,TValue}"/>. Ermöglicht Latching und Pinning.
/// </summary>
internal interface INode
{
    /// <summary>Block-Handle im Storage. Wird beim Cachen gesetzt.</summary>
    long Handle { get; set; }

    bool IsDirty { get; set; }

    /// <summary>Timeout für Latch-Acquisition. Wird vom WTree gesetzt.</summary>
    TimeSpan LatchTimeout { get; set; }

    /// <summary>Optionale Wait-for-Graph Diagnostik.</summary>
    ILatchDiagnostics? LatchDiagnostics { get; set; }

    /// <summary>Erwirbt einen Shared-Latch (Read-Lock).</summary>
    void EnterShared();

    /// <summary>Erwirbt einen Exclusive-Latch (Write-Lock).</summary>
    void EnterExclusive();

    /// <summary>Gibt den Latch frei (Read oder Write).</summary>
    void ExitLatch();

    /// <summary>Verhindert Eviction aus dem Node-Cache.</summary>
    void Pin();

    /// <summary>Erlaubt Eviction aus dem Node-Cache.</summary>
    void Unpin();

    /// <summary>true wenn der Node von mindestens einem Thread gehalten wird.</summary>
    bool IsPinned { get; }
}
