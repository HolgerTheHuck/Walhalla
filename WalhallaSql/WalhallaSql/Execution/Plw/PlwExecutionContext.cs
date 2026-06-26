using System;
using System.Diagnostics;

namespace WalhallaSql.Execution.Plw;

/// <summary>
/// Laufzeitkontext fuer einen PLW-Aufruf. Traegt die vom Nutzer konfigurierten
/// Sicherheitslimits (Instruktionen, Timeout, Speicher, Aufruftiefe) und prueft
/// sie waehrend der Ausfuehrung.
/// </summary>
internal sealed class PlwExecutionContext
{
    private readonly WalhallaOptions _options;
    private readonly Stopwatch _stopwatch;
    private readonly long _startAllocatedBytes;
    private long _instructionCounter;
    private int _currentDepth;

    public PlwExecutionContext(WalhallaOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _stopwatch = Stopwatch.StartNew();
        _startAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
        _instructionCounter = 0;
        _currentDepth = 0;
    }

    /// <summary>
    /// Erhoeht den Instruktionszaehler und prueft alle konfigurierten Limits.
    /// Muss vor jeder ausfuehrbaren PLW-Anweisung aufgerufen werden.
    /// </summary>
    public void Step()
    {
        _instructionCounter++;
        CheckLimits();
    }

    /// <summary>
    /// Prueft Timeout, Instruktionslimit und Speicherverbrauch.
    /// </summary>
    public void CheckLimits()
    {
        if (_options.PlwTimeout.HasValue && _stopwatch.Elapsed > _options.PlwTimeout.Value)
        {
            throw new WalhallaException(
                $"PLW-Ausfuehrung wurde nach {_options.PlwTimeout.Value.TotalSeconds:0.###}s durch das Timeout-Limit abgebrochen.",
                "57014");
        }

        if (_instructionCounter > _options.PlwMaxInstructions)
        {
            throw new WalhallaException(
                $"PLW-Ausfuehrung ueberschritt das Instruktionslimit von {_options.PlwMaxInstructions:N0} Anweisungen.",
                "54011");
        }

        if (_options.PlwMaxAllocatedBytesPerCall > 0)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread() - _startAllocatedBytes;
            if (allocated > _options.PlwMaxAllocatedBytesPerCall)
            {
                throw new WalhallaException(
                    $"PLW-Ausfuehrung ueberschritt das Speicherlimit von {_options.PlwMaxAllocatedBytesPerCall:N0} Bytes.",
                    "54000");
            }
        }
    }

    /// <summary>
    /// Markiert den Eintritt in einen verschachtelten Aufruf (z. B. EXECUTE einer
    /// anderen Prozedur). Prueft dabei die konfigurierte maximale Aufruftiefe.
    /// </summary>
    public IDisposable EnterCall()
    {
        _currentDepth++;

        if (_currentDepth > _options.PlwMaxCallDepth)
        {
            _currentDepth--;
            throw new WalhallaException(
                $"PLW-Ausfuehrung ueberschritt die maximale Aufruftiefe von {_options.PlwMaxCallDepth}.",
                "54023");
        }

        return new CallDepthScope(this);
    }

    /// <summary>
    /// Anzahl der bisher ausgefuehrten Instruktionen.
    /// </summary>
    public long InstructionCounter => _instructionCounter;

    /// <summary>
    /// Aktuelle verschachtelte Aufruftiefe.
    /// </summary>
    public int CurrentDepth => _currentDepth;

    private sealed class CallDepthScope : IDisposable
    {
        private readonly PlwExecutionContext _context;
        private bool _disposed;

        public CallDepthScope(PlwExecutionContext context)
        {
            _context = context;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_context._currentDepth > 0)
                _context._currentDepth--;
        }
    }
}
