// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Walhalla.Storage.Core.Transformers;

/// <summary>
/// Encodes values before they are written to disk and decodes them when they are read back.
/// Typical implementations provide compression, encryption, or a combination of both.
/// </summary>
/// <remarks>
/// <para>
/// Implementations <b>must</b> be stateless and thread-safe – a transformer instance is shared
/// across all concurrent read/write operations against the same store.
/// </para>
/// <para>
/// When multiple transformers are composed via <see cref="ValueTransformerExtensions.Then"/>,
/// operations are applied in declaration order on encode and in reverse order on decode:
/// <code>
/// // Declaration: compress first, then encrypt
/// var chain = new LZ4Transformer().Then(new AesGcmTransformer(key));
///
/// // Encode path:  raw → lz4 → aes → disk
/// // Decode path:  disk → aes⁻¹ → lz4⁻¹ → raw
/// </code>
/// </para>
/// </remarks>
public interface IValueTransformer
{
    /// <summary>
    /// Transforms raw value bytes (e.g. compress + encrypt) before the value is written to disk.
    /// </summary>
    /// <param name="raw">The original, unmodified value bytes.</param>
    /// <returns>Transformed bytes to be stored on disk.</returns>
    byte[] Encode(ReadOnlySpan<byte> raw);

    /// <summary>
    /// Reverses the transformation (e.g. decrypt + decompress) when a value is read from disk.
    /// </summary>
    /// <param name="stored">The bytes that were previously returned by <see cref="Encode"/>.</param>
    /// <returns>The original, unmodified value bytes.</returns>
    byte[] Decode(ReadOnlySpan<byte> stored);
}
