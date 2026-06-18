// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Walhalla.Indexes.Primitives;

namespace Walhalla.VectorStore.Indexes;

/// <summary>
/// Ergebnis einer Payload-Index-Auswertung fuer den Suchpfad.
/// </summary>
public sealed class PayloadIndexEvaluation
{
    public PayloadIndexEvaluation(SimpleBitmap? bitmap, bool requiresPostFilter)
    {
        Bitmap = bitmap;
        RequiresPostFilter = requiresPostFilter;
    }

    public SimpleBitmap? Bitmap { get; }

    public bool RequiresPostFilter { get; }

    public bool IsExact => Bitmap is not null && !RequiresPostFilter;
}