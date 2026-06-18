using Walhalla.Storage.Contract;

namespace Walhalla.Storage.Mvcc.Transactions;

/// <summary>Repräsentiert eine aktive Datenbanktransaktion.</summary>
public interface ITransaction<TKey, TValue> : IDisposable
    where TKey : notnull
{
    /// <summary>Einzigartige Transaktions-ID.</summary>
    ulong TxId { get; }

    /// <summary>Globale Sequenznummer zum Startzeitpunkt.</summary>
    ulong StartSequence { get; }

    /// <summary>Aktueller Transaktionsstatus.</summary>
    TransactionStatus Status { get; }

    /// <summary>Liest einen Key innerhalb der Transaktion.</summary>
    bool TryGet(TKey key, out TValue value);

    /// <summary>Prüft, ob ein Key in der Transaktion sichtbar ist.</summary>
    bool ContainsKey(TKey key);

    /// <summary>Fügt ein oder überschreibt einen Key.</summary>
    void Upsert(TKey key, TValue value);

    /// <summary>Löscht einen Key.</summary>
    void Delete(TKey key);

    /// <summary>Commit – macht alle Änderungen dauerhaft.</summary>
    void Commit();

    /// <summary>Rollback – verwirft alle Änderungen.</summary>
    void Rollback();
}
