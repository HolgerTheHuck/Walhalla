// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Walhalla.Indexes.FullText;

public enum FullTextQueryMode
{
    All,
    Any,
}

public sealed class FullTextQuery
{
    public FullTextQuery(string[] terms, string[][] phrases, string[] negativeTerms, string[][] negativePhrases)
    {
        Terms = terms;
        Phrases = phrases;
        NegativeTerms = negativeTerms;
        NegativePhrases = negativePhrases;
    }

    public string[] Terms { get; }

    public string[][] Phrases { get; }

    public string[] NegativeTerms { get; }

    public string[][] NegativePhrases { get; }

    public bool HasPositiveClauses => Terms.Length > 0 || Phrases.Length > 0;

    public IEnumerable<string> EnumeratePositiveTerms()
    {
        foreach (string term in Terms)
            yield return term;

        foreach (string[] phrase in Phrases)
        {
            foreach (string term in phrase)
                yield return term;
        }
    }
}

public sealed class FullTextDocumentTerms
{
    public FullTextDocumentTerms(Dictionary<string, int> termFrequencies, Dictionary<string, int[]> termPositions, int length)
    {
        TermFrequencies = termFrequencies;
        TermPositions = termPositions;
        Length = length;
    }

    public Dictionary<string, int> TermFrequencies { get; }

    public Dictionary<string, int[]> TermPositions { get; }

    public int Length { get; }

    public bool HasPositions => TermPositions.Count != 0;
}

public static class FullTextQueryParser
{
    private const float Bm25K1 = 1.2f;
    private const float Bm25B = 0.75f;

    public static FullTextQuery Parse(string query, string? notQuery = null)
    {
        var terms = new List<string>();
        var phrases = new List<string[]>();
        ParseSegment(query, terms, phrases);

        var negativeTerms = new List<string>();
        var negativePhrases = new List<string[]>();
        ParseSegment(notQuery, negativeTerms, negativePhrases);

        return new FullTextQuery(
            terms.Distinct(StringComparer.Ordinal).ToArray(),
            phrases.Where(static phrase => phrase.Length > 0).ToArray(),
            negativeTerms.Distinct(StringComparer.Ordinal).ToArray(),
            negativePhrases.Where(static phrase => phrase.Length > 0).ToArray());
    }

    public static FullTextDocumentTerms BuildDocumentTerms(string text, bool includePositions = true)
    {
        var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
        var positions = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        int length = 0;

        foreach (var (token, position) in Tokenizer.TokenizeWithPositions(text))
        {
            length++;
            frequencies.TryGetValue(token, out int frequency);
            frequencies[token] = frequency + 1;

            if (!includePositions)
                continue;

            if (!positions.TryGetValue(token, out var tokenPositions))
            {
                tokenPositions = new List<int>();
                positions[token] = tokenPositions;
            }

            tokenPositions.Add(position);
        }

        return new FullTextDocumentTerms(
            frequencies,
            positions.ToDictionary(static pair => pair.Key, static pair => pair.Value.ToArray(), StringComparer.Ordinal),
            length);
    }

    public static bool Matches(FullTextDocumentTerms document, FullTextQuery query, FullTextQueryMode mode)
    {
        if (!query.HasPositiveClauses)
            return false;

        bool positiveMatch = mode switch
        {
            FullTextQueryMode.All => MatchesAll(document, query),
            FullTextQueryMode.Any => MatchesAny(document, query),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };

        if (!positiveMatch)
            return false;

        if (query.NegativeTerms.Any(document.TermFrequencies.ContainsKey))
            return false;

        if (query.NegativePhrases.Any(phrase => MatchesPhrase(document, phrase)))
            return false;

        return true;
    }

    public static float ComputeBm25Score(FullTextDocumentTerms document, FullTextQuery query, Dictionary<string, int> documentFrequencies, int documentCount, double averageDocumentLength)
    {
        float score = 0;
        foreach (string term in query.EnumeratePositiveTerms().Distinct(StringComparer.Ordinal))
        {
            if (!document.TermFrequencies.TryGetValue(term, out int termFrequency) || termFrequency <= 0)
                continue;

            int documentFrequency = Math.Max(1, documentFrequencies.GetValueOrDefault(term));
            double idf = Math.Log(1.0 + (documentCount - documentFrequency + 0.5) / (documentFrequency + 0.5));
            double normalization = 1.0 - Bm25B + Bm25B * document.Length / averageDocumentLength;
            double numerator = termFrequency * (Bm25K1 + 1.0);
            double denominator = termFrequency + Bm25K1 * normalization;
            score += (float)(idf * numerator / denominator);
        }

        foreach (string[] phrase in query.Phrases)
        {
            if (MatchesPhrase(document, phrase))
                score += 0.25f * phrase.Length;
        }

        return score;
    }

    private static bool MatchesAll(FullTextDocumentTerms document, FullTextQuery query)
    {
        foreach (string term in query.Terms)
        {
            if (!document.TermFrequencies.ContainsKey(term))
                return false;
        }

        foreach (string[] phrase in query.Phrases)
        {
            if (!MatchesPhrase(document, phrase))
                return false;
        }

        return true;
    }

    private static bool MatchesAny(FullTextDocumentTerms document, FullTextQuery query)
    {
        foreach (string term in query.Terms)
        {
            if (document.TermFrequencies.ContainsKey(term))
                return true;
        }

        foreach (string[] phrase in query.Phrases)
        {
            if (MatchesPhrase(document, phrase))
                return true;
        }

        return false;
    }

    private static bool MatchesPhrase(FullTextDocumentTerms document, string[] phrase)
    {
        if (phrase.Length == 0)
            return false;

        if (phrase.Length == 1)
            return document.TermFrequencies.ContainsKey(phrase[0]);

        if (!document.HasPositions || !document.TermPositions.TryGetValue(phrase[0], out int[]? firstPositions))
            return false;

        var lookups = new HashSet<int>[phrase.Length - 1];
        for (int i = 1; i < phrase.Length; i++)
        {
            if (!document.TermPositions.TryGetValue(phrase[i], out int[]? positions))
                return false;

            lookups[i - 1] = positions.ToHashSet();
        }

        foreach (int start in firstPositions)
        {
            bool isMatch = true;
            for (int offset = 1; offset < phrase.Length; offset++)
            {
                if (!lookups[offset - 1].Contains(start + offset))
                {
                    isMatch = false;
                    break;
                }
            }

            if (isMatch)
                return true;
        }

        return false;
    }

    private static void ParseSegment(string? query, List<string> terms, List<string[]> phrases)
    {
        if (string.IsNullOrWhiteSpace(query))
            return;

        var buffer = new StringBuilder();
        bool inQuote = false;

        foreach (char character in query)
        {
            if (character == '"')
            {
                if (inQuote)
                    AddPhrase(buffer.ToString(), phrases);
                else
                    AddTerms(buffer.ToString(), terms);

                buffer.Clear();
                inQuote = !inQuote;
                continue;
            }

            buffer.Append(character);
        }

        if (buffer.Length == 0)
            return;

        if (inQuote)
            AddPhrase(buffer.ToString(), phrases);
        else
            AddTerms(buffer.ToString(), terms);
    }

    private static void AddTerms(string text, List<string> terms)
    {
        terms.AddRange(Tokenizer.Tokenize(text));
    }

    private static void AddPhrase(string text, List<string[]> phrases)
    {
        string[] tokens = Tokenizer.Tokenize(text).ToArray();
        if (tokens.Length > 0)
            phrases.Add(tokens);
    }
}