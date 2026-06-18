// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Walhalla.Indexes.Primitives;

namespace Walhalla.Indexes.FullText;

/// <summary>
/// Einfacher Whitespace-Tokenizer fuer Volltext-Indexierung.
/// </summary>
public static class Tokenizer
{
    public static IEnumerable<string> Tokenize(string text)
    {
        foreach (var (token, _) in TokenizeWithPositions(text))
            yield return token;
    }

    public static IEnumerable<(string Token, int Position)> TokenizeWithPositions(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        int start = -1;
        int position = 0;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool isLetterOrDigit = char.IsLetterOrDigit(c);

            if (isLetterOrDigit && start == -1)
            {
                start = i;
            }
            else if (!isLetterOrDigit && start != -1)
            {
                yield return (text.Substring(start, i - start).ToLowerInvariant(), position++);
                start = -1;
            }
        }

        if (start != -1)
            yield return (text.Substring(start).ToLowerInvariant(), position);
    }
}

/// <summary>
/// In-Memory invertierter Volltext-Index.
/// Term → Bitmap von Dokument-IDs.
/// </summary>
public sealed class FullTextIndex
{
    // term → Bitmap von Dokument-IDs (Posting-List)
    private readonly Dictionary<string, SimpleBitmap> _postings = new(StringComparer.Ordinal);

    // Dokument-ID → Termdaten (fuer Entfernen und Query-Auswertung)
    private readonly Dictionary<long, FullTextDocumentTerms> _docTerms = new();

    /// <summary>Indexiert einen Text fuer eine Dokument-ID.</summary>
    public void IndexDocument(long id, string text)
    {
        RemoveDocument(id);

        var document = FullTextQueryParser.BuildDocumentTerms(text);
        foreach (string term in document.TermFrequencies.Keys)
        {
            if (!_postings.TryGetValue(term, out var bitmap))
            {
                bitmap = new SimpleBitmap();
                _postings[term] = bitmap;
            }
            bitmap.Set((ulong)id);
        }

        _docTerms[id] = document;
    }

    /// <summary>Entfernt ein Dokument aus allen Postings.</summary>
    public void RemoveDocument(long id)
    {
        if (!_docTerms.TryGetValue(id, out var document))
            return;

        foreach (string term in document.TermFrequencies.Keys)
        {
            if (_postings.TryGetValue(term, out var bitmap))
            {
                bitmap.Clear((ulong)id);
                if (bitmap.Count == 0)
                    _postings.Remove(term);
            }
        }

        _docTerms.Remove(id);
    }

    /// <summary>
    /// Sucht nach Dokumenten, die ALLE Query-Terms enthalten (AND).
    /// Liefert null, wenn kein Term gefunden wurde.
    /// </summary>
    public SimpleBitmap? Search(string query, FullTextQueryMode mode = FullTextQueryMode.All, string? notQuery = null)
    {
        return SearchBitmapCore(query, mode, notQuery);
    }

    /// <summary>
    /// Sucht nach Dokumenten, die MINDESTENS EINEN Query-Term enthalten (OR).
    /// Liefert null, wenn kein Term gefunden wurde.
    /// </summary>
    public SimpleBitmap? SearchAny(string query, string? notQuery = null)
    {
        return SearchBitmapCore(query, FullTextQueryMode.Any, notQuery);
    }

    /// <summary>Anzahl der indizierten Dokumente.</summary>
    public int DocumentCount => _docTerms.Count;

    /// <summary>Anzahl eindeutiger Terms im Index.</summary>
    public int TermCount => _postings.Count;

    private SimpleBitmap? SearchBitmapCore(string query, FullTextQueryMode mode, string? notQuery)
    {
        var parsedQuery = FullTextQueryParser.Parse(query, notQuery);
        if (!parsedQuery.HasPositiveClauses)
            return null;

        var candidateTerms = parsedQuery.EnumeratePositiveTerms()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (candidateTerms.Length == 0)
            return null;

        var postings = new List<SimpleBitmap>(candidateTerms.Length);
        foreach (string term in candidateTerms)
        {
            if (!_postings.TryGetValue(term, out var bitmap))
            {
                if (mode == FullTextQueryMode.All)
                    return null;

                continue;
            }

            postings.Add(bitmap);
        }

        if (postings.Count == 0)
            return null;

        SimpleBitmap candidates = postings[0];
        for (int i = 1; i < postings.Count; i++)
            candidates = mode == FullTextQueryMode.Any ? candidates.Or(postings[i]) : candidates.And(postings[i]);

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
            if (_docTerms.TryGetValue((long)rawId, out var document) && FullTextQueryParser.Matches(document, parsedQuery, mode))
                result.Set(rawId);
        }

        return result;
    }
}
