namespace Walhalla.Storage.Mvcc.Transactions;

/// <summary>
/// Wird geworfen, wenn eine Transaktionsoperation aufgerufen wird,
/// während die Transaktion nicht im Status Active ist.
/// </summary>
public sealed class TransactionStateException : InvalidOperationException
{
    public TransactionStateException(string message) : base(message) { }
}
