// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Walhalla.Storage.Core.Transformers;

/// <summary>
/// Composes two <see cref="IValueTransformer"/> instances into a single pipeline.
/// Encode applies <see cref="_first"/> then <see cref="_second"/>;
/// decode applies <see cref="_second"/> then <see cref="_first"/> (inverse order).
/// </summary>
internal sealed class ChainedTransformer : IValueTransformer
{
    private readonly IValueTransformer _first;
    private readonly IValueTransformer _second;

    internal ChainedTransformer(IValueTransformer first, IValueTransformer second)
    {
        _first  = first  ?? throw new ArgumentNullException(nameof(first));
        _second = second ?? throw new ArgumentNullException(nameof(second));
    }

    /// <inheritdoc/>
    public byte[] Encode(ReadOnlySpan<byte> raw)
    {
        var intermediate = _first.Encode(raw);
        return _second.Encode(intermediate);
    }

    /// <inheritdoc/>
    public byte[] Decode(ReadOnlySpan<byte> stored)
    {
        var intermediate = _second.Decode(stored);
        return _first.Decode(intermediate);
    }
}

/// <summary>
/// Extension methods for composing <see cref="IValueTransformer"/> pipelines.
/// </summary>
public static class ValueTransformerExtensions
{
    /// <summary>
    /// Chains <paramref name="next"/> after <paramref name="current"/> so that
    /// <c>Encode</c> applies both in order and <c>Decode</c> applies both in reverse.
    /// </summary>
    /// <example>
    /// <code>
    /// // compress first, then encrypt – correct order for combined pipelines
    /// IValueTransformer pipeline = new LZ4Transformer()
    ///     .Then(new AesGcmTransformer(key));
    /// </code>
    /// </example>
    public static IValueTransformer Then(this IValueTransformer current, IValueTransformer next)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(next);
        return new ChainedTransformer(current, next);
    }
}
