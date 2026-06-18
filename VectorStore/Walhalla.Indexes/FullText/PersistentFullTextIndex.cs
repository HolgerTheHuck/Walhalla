// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Walhalla.Indexes.Primitives;
using Walhalla.Storage.Core.Transactions;
using Walhalla.Storage.Core.Runtime;
using Walhalla.Storage.Contract;

namespace Walhalla.Indexes.FullText;

/// <summary>
/// Persistenter invertierter Volltext-Index auf Basis von WalhallaStore.
/// </summary>
public sealed class PersistentFullTextIndex
{
    private const byte NamespaceSeparator = 0;
    private const byte TermKeyKind = 1;
    private const byte DocumentKeyKind = 2;
    private const byte StatsKeyKind = 3;
    private const byte DocumentTermsVersion1 = 1;
    private const byte DocumentTermsVersion2 = 2;
    private const byte DocumentTermsVersion3 = 3;
    private const byte StatsPayloadVersion = 1;

    private readonly IKeyValueStore _store;
    private readonly byte[] _prefix;
    private readonly byte[] _termPrefix;
    private readonly byte[] _documentPrefix;
    private readonly byte[] _statsKey;

    public PersistentFullTextIndex(IKeyValueStore store, string keyPrefix = "fulltext")
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(keyPrefix);

        _prefix = BuildPrefix(keyPrefix);
        _termPrefix = BuildTypedPrefix(TermKeyKind);
        _documentPrefix = BuildTypedPrefix(DocumentKeyKind);
        _statsKey = BuildTypedPrefix(StatsKeyKind);
    }

    /// <summary>Indexiert einen Text fuer eine Dokument-ID persistent.</summary>
    public void IndexDocument(long id, string text)
    {
        var oldTerms = ReadDocumentTermData(id);
        var newTerms = FullTextQueryParser.BuildDocumentTerms(text);
        var affectedTerms = new HashSet<string>(oldTerms?.TermFrequencies.Keys ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        affectedTerms.UnionWith(newTerms.TermFrequencies.Keys);
        var stats = ReadStats() ?? new FullTextIndexStats();

        using var tx = _store.BeginTransaction();
        foreach (string term in affectedTerms)
        {
            var bitmap = ReadPosting(term) ?? new SimpleBitmap();
            if (oldTerms is not null && oldTerms.TermFrequencies.ContainsKey(term))
                bitmap.Clear((ulong)id);

            if (newTerms.TermFrequencies.ContainsKey(term))
                bitmap.Set((ulong)id);

            byte[] termKey = BuildTermKey(term);
            if (bitmap.Count == 0)
                tx.Delete(termKey);
            else
                tx.Upsert(termKey, bitmap.Serialize());
        }

        tx.Upsert(BuildDocumentKey(id), SerializeTerms(newTerms));
        WriteStats(tx, UpdateStatsForWrite(stats, oldTerms?.Length ?? 0, newTerms.Length, oldTerms is null));
        tx.Commit();
    }

    /// <summary>Entfernt ein Dokument persistent aus allen Posting-Listen.</summary>
    public void RemoveDocument(long id)
    {
        var oldTerms = ReadDocumentTermData(id);
        if (oldTerms is null)
            return;

        var stats = ReadStats() ?? new FullTextIndexStats();

        using var tx = _store.BeginTransaction();
        foreach (string term in oldTerms.TermFrequencies.Keys)
        {
            var bitmap = ReadPosting(term);
            if (bitmap is null)
                continue;

            bitmap.Clear((ulong)id);
            byte[] termKey = BuildTermKey(term);
            if (bitmap.Count == 0)
                tx.Delete(termKey);
            else
                tx.Upsert(termKey, bitmap.Serialize());
        }

        tx.Delete(BuildDocumentKey(id));
        WriteStats(tx, UpdateStatsForDelete(stats, oldTerms.Length));
        tx.Commit();
    }

    /// <summary>Sucht Dokumente, die alle Query-Terms enthalten.</summary>
    public SimpleBitmap? Search(string query, FullTextQueryMode mode = FullTextQueryMode.All, string? notQuery = null)
    {
        return SearchBitmapCore(query, mode, notQuery);
    }

    /// <summary>Sucht Dokumente, die mindestens einen Query-Term enthalten.</summary>
    public SimpleBitmap? SearchAny(string query, string? notQuery = null)
    {
        return SearchBitmapCore(query, FullTextQueryMode.Any, notQuery);
    }

    /// <summary>Sucht Dokumente, die alle Query-Terms enthalten, mit BM25-aehnlichem Ranking.</summary>
    public IReadOnlyList<(long Id, float Score)> SearchScored(string query, int topK = 10, FullTextQueryMode mode = FullTextQueryMode.All, string? notQuery = null)
    {
        return SearchScoredCore(query, topK, mode, notQuery);
    }

    /// <summary>Sucht Dokumente, die mindestens einen Query-Term enthalten, mit BM25-aehnlichem Ranking.</summary>
    public IReadOnlyList<(long Id, float Score)> SearchAnyScored(string query, int topK = 10, string? notQuery = null)
    {
        return SearchScoredCore(query, topK, FullTextQueryMode.Any, notQuery);
    }

    public int DocumentCount => _store.ScanPrefix(_documentPrefix).Count();

    public int TermCount => _store.ScanPrefix(_termPrefix).Count();

    public void Clear()
    {
        var keys = new List<byte[]>();
        keys.AddRange(_store.ScanPrefix(_termPrefix).Select(static pair => pair.Key));
        keys.AddRange(_store.ScanPrefix(_documentPrefix).Select(static pair => pair.Key));
        if (_store.TryGet(_statsKey, out var _) )
            keys.Add(_statsKey);
        if (keys.Count == 0)
            return;

        using var tx = _store.BeginTransaction();
        DeleteKeys(tx, keys);
        tx.Commit();
    }

    private SimpleBitmap? ReadPosting(string term)
    {
        return _store.TryGet(BuildTermKey(term), out var payload) && payload is not null
            ? SimpleBitmap.Deserialize(payload)
            : null;
    }

    private FullTextDocumentTerms? ReadDocumentTermData(long id)
    {
        return _store.TryGet(BuildDocumentKey(id), out var payload) && payload is not null
            ? DeserializeTerms(payload)
            : null;
    }

    private byte[] BuildTermKey(string term)
    {
        byte[] termBytes = Encoding.UTF8.GetBytes(term);
        byte[] key = new byte[_termPrefix.Length + termBytes.Length];
        _termPrefix.CopyTo(key, 0);
        termBytes.CopyTo(key, _termPrefix.Length);
        return key;
    }

    private byte[] BuildDocumentKey(long id)
    {
        byte[] key = new byte[_documentPrefix.Length + sizeof(long)];
        _documentPrefix.CopyTo(key, 0);
        BinaryPrimitives.WriteInt64BigEndian(key.AsSpan(_documentPrefix.Length), id);
        return key;
    }

    private byte[] BuildTypedPrefix(byte keyKind)
    {
        byte[] prefix = new byte[_prefix.Length + 1];
        _prefix.CopyTo(prefix, 0);
        prefix[^1] = keyKind;
        return prefix;
    }

    private static byte[] BuildPrefix(string keyPrefix)
    {
        byte[] keyPrefixBytes = Encoding.UTF8.GetBytes(keyPrefix);
        byte[] prefix = new byte[keyPrefixBytes.Length + 2];
        keyPrefixBytes.CopyTo(prefix, 0);
        prefix[keyPrefixBytes.Length] = NamespaceSeparator;
        return prefix;
    }

    private SimpleBitmap? SearchBitmapCore(string query, FullTextQueryMode mode, string? notQuery)
    {
        var parsedQuery = FullTextQueryParser.Parse(query, notQuery);
        if (!parsedQuery.HasPositiveClauses)
            return null;

        var postings = LoadPostings(parsedQuery, mode);
        if (postings.Count == 0)
            return null;

        SimpleBitmap candidates = BuildCandidateBitmap(postings, mode);
        if (candidates.Count == 0)
            return candidates;

        bool requiresPostFilter = parsedQuery.Phrases.Length > 0
            || parsedQuery.NegativeTerms.Length > 0
            || parsedQuery.NegativePhrases.Length > 0;
        if (!requiresPostFilter)
            return candidates;

        var result = new SimpleBitmap();
        foreach (ulong rawId in candidates.EnumerateSetBits())
        {
            long id = checked((long)rawId);
            var document = ReadDocumentTermData(id);
            if (document is not null && FullTextQueryParser.Matches(document, parsedQuery, mode))
                result.Set(rawId);
        }

        return result;
    }

    private IReadOnlyList<(long Id, float Score)> SearchScoredCore(string query, int topK, FullTextQueryMode mode, string? notQuery)
    {
        if (topK <= 0)
            return Array.Empty<(long Id, float Score)>();

        var parsedQuery = FullTextQueryParser.Parse(query, notQuery);
        if (!parsedQuery.HasPositiveClauses)
            return Array.Empty<(long Id, float Score)>();

        var postings = LoadPostings(parsedQuery, mode);
        if (postings.Count == 0)
            return Array.Empty<(long Id, float Score)>();

        SimpleBitmap candidates = BuildCandidateBitmap(postings, mode);
        if (candidates.Count == 0)
            return Array.Empty<(long Id, float Score)>();

        var stats = ReadStats() ?? BuildStatsFromDocuments();
        if (stats.DocumentCount == 0)
            return Array.Empty<(long Id, float Score)>();

        double averageDocumentLength = Math.Max(1.0, (double)stats.TotalTermCount / stats.DocumentCount);
        var results = new List<(long Id, float Score)>(Math.Min(candidates.Count, topK));
        var documentFrequencies = postings.ToDictionary(static pair => pair.Term, static pair => pair.Bitmap.Count, StringComparer.Ordinal);

        foreach (ulong rawId in candidates.EnumerateSetBits())
        {
            long id = checked((long)rawId);
            var document = ReadDocumentTermData(id);
            if (document is null)
                continue;

            if (!FullTextQueryParser.Matches(document, parsedQuery, mode))
                continue;

            float score = FullTextQueryParser.ComputeBm25Score(document, parsedQuery, documentFrequencies, stats.DocumentCount, averageDocumentLength);
            if (score <= 0)
                continue;

            results.Add((id, score));
        }

        return results
            .OrderByDescending(static result => result.Score)
            .ThenBy(static result => result.Id)
            .Take(topK)
            .ToArray();
    }

    private static byte[] SerializeTerms(FullTextDocumentTerms terms)
    {
        var buffer = new List<byte>();
        buffer.Add(DocumentTermsVersion3);
        WriteVarUInt64(buffer, (ulong)terms.Length);
        var sortedTerms = terms.TermFrequencies.OrderBy(static pair => pair.Key, StringComparer.Ordinal).ToArray();
        WriteVarUInt64(buffer, (ulong)sortedTerms.Length);

        foreach (var (term, frequency) in sortedTerms)
        {
            byte[] termBytes = Encoding.UTF8.GetBytes(term);
            WriteVarUInt64(buffer, (ulong)termBytes.Length);
            buffer.AddRange(termBytes);
            WriteVarUInt64(buffer, (ulong)frequency);

            int[] positions = terms.TermPositions.TryGetValue(term, out var termPositions)
                ? termPositions
                : Array.Empty<int>();
            WriteVarUInt64(buffer, (ulong)positions.Length);

            int previousPosition = 0;
            bool hasPrevious = false;
            foreach (int position in positions)
            {
                ulong delta = hasPrevious
                    ? (ulong)(position - previousPosition)
                    : (ulong)position;
                WriteVarUInt64(buffer, delta);
                previousPosition = position;
                hasPrevious = true;
            }
        }

        return buffer.ToArray();
    }

    private static FullTextDocumentTerms DeserializeTerms(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            throw new InvalidDataException("Document terms payload is empty.");

        return data[0] switch
        {
            DocumentTermsVersion1 => DeserializeTermsV1(data),
            DocumentTermsVersion2 => DeserializeTermsV2(data),
            DocumentTermsVersion3 => DeserializeTermsV3(data),
            _ => throw new InvalidDataException($"Unsupported document terms payload version {data[0]}.")
        };
    }

    private static FullTextDocumentTerms DeserializeTermsV1(ReadOnlySpan<byte> data)
    {
        int offset = 1;
        ulong count = ReadVarUInt64(data, ref offset);
        if (count > int.MaxValue)
            throw new InvalidDataException("Document terms payload contains too many terms.");

        var terms = new Dictionary<string, int>((int)count, StringComparer.Ordinal);
        for (ulong i = 0; i < count; i++)
        {
            ulong length = ReadVarUInt64(data, ref offset);
            if (length > int.MaxValue || offset + (int)length > data.Length)
                throw new InvalidDataException("Document terms payload ended unexpectedly.");

            string term = Encoding.UTF8.GetString(data.Slice(offset, (int)length));
            terms[term] = 1;
            offset += (int)length;
        }

        if (offset != data.Length)
            throw new InvalidDataException("Document terms payload contains trailing bytes.");

        return new FullTextDocumentTerms(terms, new Dictionary<string, int[]>(StringComparer.Ordinal), terms.Count);
    }

    private static FullTextDocumentTerms DeserializeTermsV2(ReadOnlySpan<byte> data)
    {
        int offset = 1;
        ulong totalLength = ReadVarUInt64(data, ref offset);
        ulong count = ReadVarUInt64(data, ref offset);
        if (count > int.MaxValue || totalLength > int.MaxValue)
            throw new InvalidDataException("Document terms payload contains too many terms.");

        var terms = new Dictionary<string, int>((int)count, StringComparer.Ordinal);
        for (ulong i = 0; i < count; i++)
        {
            ulong length = ReadVarUInt64(data, ref offset);
            if (length > int.MaxValue || offset + (int)length > data.Length)
                throw new InvalidDataException("Document terms payload ended unexpectedly.");

            string term = Encoding.UTF8.GetString(data.Slice(offset, (int)length));
            offset += (int)length;

            ulong frequency = ReadVarUInt64(data, ref offset);
            if (frequency == 0 || frequency > int.MaxValue)
                throw new InvalidDataException("Document terms payload contains an invalid term frequency.");

            terms[term] = (int)frequency;
        }

        if (offset != data.Length)
            throw new InvalidDataException("Document terms payload contains trailing bytes.");

        return new FullTextDocumentTerms(terms, new Dictionary<string, int[]>(StringComparer.Ordinal), (int)totalLength);
    }

    private static FullTextDocumentTerms DeserializeTermsV3(ReadOnlySpan<byte> data)
    {
        int offset = 1;
        ulong totalLength = ReadVarUInt64(data, ref offset);
        ulong count = ReadVarUInt64(data, ref offset);
        if (count > int.MaxValue || totalLength > int.MaxValue)
            throw new InvalidDataException("Document terms payload contains too many terms.");

        var terms = new Dictionary<string, int>((int)count, StringComparer.Ordinal);
        var positions = new Dictionary<string, int[]>((int)count, StringComparer.Ordinal);

        for (ulong i = 0; i < count; i++)
        {
            ulong length = ReadVarUInt64(data, ref offset);
            if (length > int.MaxValue || offset + (int)length > data.Length)
                throw new InvalidDataException("Document terms payload ended unexpectedly.");

            string term = Encoding.UTF8.GetString(data.Slice(offset, (int)length));
            offset += (int)length;

            ulong frequency = ReadVarUInt64(data, ref offset);
            ulong positionCount = ReadVarUInt64(data, ref offset);
            if (frequency == 0 || frequency > int.MaxValue || positionCount > int.MaxValue)
                throw new InvalidDataException("Document terms payload contains invalid positional data.");

            terms[term] = (int)frequency;

            if (positionCount == 0)
                continue;

            var termPositions = new int[(int)positionCount];
            int previousPosition = 0;
            for (int index = 0; index < termPositions.Length; index++)
            {
                ulong delta = ReadVarUInt64(data, ref offset);
                int position = index == 0
                    ? checked((int)delta)
                    : checked(previousPosition + (int)delta);
                termPositions[index] = position;
                previousPosition = position;
            }

            positions[term] = termPositions;
        }

        if (offset != data.Length)
            throw new InvalidDataException("Document terms payload contains trailing bytes.");

        return new FullTextDocumentTerms(terms, positions, (int)totalLength);
    }

    private FullTextIndexStats? ReadStats()
    {
        return _store.TryGet(_statsKey, out var payload) && payload is not null
            ? DeserializeStats(payload)
            : null;
    }

    private static byte[] SerializeStats(FullTextIndexStats stats)
    {
        var buffer = new List<byte>(17)
        {
            StatsPayloadVersion
        };
        WriteVarUInt64(buffer, (ulong)stats.DocumentCount);
        WriteVarUInt64(buffer, (ulong)stats.TotalTermCount);
        return buffer.ToArray();
    }

    private static FullTextIndexStats DeserializeStats(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            throw new InvalidDataException("Full-text stats payload is empty.");

        if (data[0] != StatsPayloadVersion)
            throw new InvalidDataException($"Unsupported full-text stats payload version {data[0]}.");

        int offset = 1;
        ulong documentCount = ReadVarUInt64(data, ref offset);
        ulong totalTermCount = ReadVarUInt64(data, ref offset);
        if (documentCount > int.MaxValue || totalTermCount > int.MaxValue || offset != data.Length)
            throw new InvalidDataException("Full-text stats payload is invalid.");

        return new FullTextIndexStats((int)documentCount, (int)totalTermCount);
    }

    private static FullTextIndexStats UpdateStatsForWrite(FullTextIndexStats stats, int previousLength, int newLength, bool isNewDocument)
    {
        return new FullTextIndexStats(
            isNewDocument ? stats.DocumentCount + 1 : stats.DocumentCount,
            Math.Max(0, stats.TotalTermCount - previousLength + newLength));
    }

    private static FullTextIndexStats UpdateStatsForDelete(FullTextIndexStats stats, int previousLength)
    {
        int documentCount = Math.Max(0, stats.DocumentCount - 1);
        int totalTermCount = Math.Max(0, stats.TotalTermCount - previousLength);
        return new FullTextIndexStats(documentCount, totalTermCount);
    }

    private void WriteStats(IStorageTransaction tx, FullTextIndexStats stats)
    {
        if (stats.DocumentCount == 0)
            tx.Delete(_statsKey);
        else
            tx.Upsert(_statsKey, SerializeStats(stats));
    }

    private FullTextIndexStats BuildStatsFromDocuments()
    {
        int documentCount = 0;
        int totalTermCount = 0;

        foreach (var (_, payload) in _store.ScanPrefix(_documentPrefix))
        {
            var document = DeserializeTerms(payload);
            documentCount++;
            totalTermCount += document.Length;
        }

        return new FullTextIndexStats(documentCount, totalTermCount);
    }

    private static void WriteVarUInt64(List<byte> buffer, ulong value)
    {
        while (value >= 0x80)
        {
            buffer.Add((byte)(value | 0x80));
            value >>= 7;
        }

        buffer.Add((byte)value);
    }

    private static ulong ReadVarUInt64(ReadOnlySpan<byte> data, ref int offset)
    {
        ulong value = 0;
        int shift = 0;

        while (offset < data.Length)
        {
            byte current = data[offset++];
            value |= (ulong)(current & 0x7F) << shift;

            if ((current & 0x80) == 0)
                return value;

            shift += 7;
            if (shift >= 64)
                throw new InvalidDataException("Document terms payload contains an invalid varint.");
        }

        throw new InvalidDataException("Document terms payload ended unexpectedly.");
    }

    private static void DeleteKeys(IStorageTransaction tx, IEnumerable<byte[]> keys)
    {
        foreach (byte[] key in keys)
            tx.Delete(key);
    }

    private List<(string Term, SimpleBitmap Bitmap)> LoadPostings(FullTextQuery query, FullTextQueryMode mode)
    {
        var candidateTerms = query.EnumeratePositiveTerms()
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var postings = new List<(string Term, SimpleBitmap Bitmap)>(candidateTerms.Length);
        foreach (string term in candidateTerms)
        {
            var bitmap = ReadPosting(term);
            if (bitmap is null)
            {
                if (mode == FullTextQueryMode.All)
                    return new List<(string Term, SimpleBitmap Bitmap)>();

                continue;
            }

            postings.Add((term, bitmap));
        }

        return postings;
    }

    private static SimpleBitmap BuildCandidateBitmap(List<(string Term, SimpleBitmap Bitmap)> postings, FullTextQueryMode mode)
    {
        SimpleBitmap candidates = postings[0].Bitmap;
        for (int i = 1; i < postings.Count; i++)
            candidates = mode == FullTextQueryMode.Any ? candidates.Or(postings[i].Bitmap) : candidates.And(postings[i].Bitmap);

        return candidates;
    }

    private readonly record struct FullTextIndexStats(int DocumentCount = 0, int TotalTermCount = 0);
}