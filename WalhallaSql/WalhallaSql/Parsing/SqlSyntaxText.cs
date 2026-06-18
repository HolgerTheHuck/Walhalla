using System;
using System.Collections.Generic;
using System.Text;

namespace WalhallaSql.Parsing;

internal static class SqlSyntaxText
{
    public static string RemoveTrailingSemicolon(string sql)
        => sql.Trim().TrimEnd(';').TrimEnd();

    public static bool StartsWithKeyword(string sql, string keyword)
        => sql.TrimStart().StartsWith(keyword, StringComparison.OrdinalIgnoreCase);

    public static int FindTopLevelKeyword(string sql, string keyword, int startIndex)
    {
        var inString = false;
        var depth = 0;
        for (var i = startIndex; i <= sql.Length - keyword.Length; i++)
        {
            var current = sql[i];
            if (current == '\'')
            {
                // SQL escaped quote: '' inside a string is a literal single quote,
                // not a string terminator. Skip the pair without toggling state.
                if (inString && i + 1 < sql.Length && sql[i + 1] == '\'')
                {
                    i++;
                    continue;
                }
                inString = !inString;
                continue;
            }

            if (inString) continue;
            if (current == '(') { depth++; continue; }
            if (current == ')') { depth--; continue; }
            if (depth != 0) continue;

            if (MatchesKeywordAt(sql, i, keyword))
                return i;
        }
        return -1;
    }

    public static string[] SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        SplitTopLevel(text.AsSpan(), separator, span => parts.Add(span.ToString()));
        return parts.ToArray();
    }

    public delegate void SpanAction(ReadOnlySpan<char> span);

    /// <summary>Zero-allocation split: invokes <paramref name="action"/> with trimmed span for each part.</summary>
    public static void SplitTopLevel(ReadOnlySpan<char> text, char separator, SpanAction action)
    {
        var start = 0;
        var inString = false;
        var depth = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'')
            {
                if (inString && i + 1 < text.Length && text[i + 1] == '\'')
                {
                    i++;
                    continue;
                }
                inString = !inString;
                continue;
            }

            if (!inString)
            {
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == separator && depth == 0)
                {
                    var part = text.Slice(start, i - start).Trim();
                    if (part.Length > 0)
                        action(part);
                    start = i + 1;
                }
            }
        }

        var last = text.Slice(start).Trim();
        if (last.Length > 0)
            action(last);
    }

    public static int FindMatchingParen(string text, int openParenIndex)
    {
        var depth = 0;
        var inString = false;
        for (var index = openParenIndex; index < text.Length; index++)
        {
            var current = text[index];
            if (current == '\'')
            {
                if (inString && index + 1 < text.Length && text[index + 1] == '\'')
                {
                    index++;
                    continue;
                }
                inString = !inString;
                continue;
            }

            if (inString) continue;
            if (current == '(') depth++;
            else if (current == ')')
            {
                depth--;
                if (depth == 0) return index;
            }
        }
        return -1;
    }

    public static string NormalizeIdentifier(string identifier)
        => NormalizeIdentifier(identifier.AsSpan());

    public static string NormalizeIdentifier(ReadOnlySpan<char> identifier)
    {
        var trimmed = identifier.Trim();
        if (trimmed.Length >= 2
            && ((trimmed[0] == '"' && trimmed[^1] == '"')
                || (trimmed[0] == '[' && trimmed[^1] == ']')
                || (trimmed[0] == '`' && trimmed[^1] == '`')))
        {
            return trimmed.Slice(1, trimmed.Length - 2).ToString();
        }
        return trimmed.ToString();
    }

    /// <summary>
    /// Extrahiert den Spaltennamen aus einem optional tabellenqualifizierten
    /// Bezeichner (z. B. "t"."col" oder [t].[col]) und normalisiert ihn.
    /// </summary>
    public static string NormalizeColumnIdentifier(string identifier)
    {
        var trimmed = identifier.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return trimmed;

        var parts = SplitTopLevel(trimmed, '.');
        return parts.Length > 1
            ? NormalizeIdentifier(parts[^1])
            : NormalizeIdentifier(trimmed);
    }

    public static bool TryParseSingleTableSource(string tableSegment, out string collectionName, out string? alias)
    {
        collectionName = string.Empty;
        alias = null;

        if (string.IsNullOrWhiteSpace(tableSegment))
            return false;

        var tokens = tableSegment.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return false;

        collectionName = NormalizeIdentifier(tokens[0]);
        if (tokens.Length == 1) return true;
        if (tokens.Length == 2) { alias = NormalizeIdentifier(tokens[1]); return true; }
        if (tokens.Length == 3 && tokens[1].Equals("AS", StringComparison.OrdinalIgnoreCase))
        {
            alias = NormalizeIdentifier(tokens[2]);
            return true;
        }
        return false;
    }

    internal static bool MatchesKeywordAt(string sql, int index, string keyword)
    {
        if (index < 0 || index + keyword.Length > sql.Length) return false;
        if (!sql.AsSpan(index, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase)) return false;

        var beforeOk = index == 0 || !char.IsLetterOrDigit(sql[index - 1]);
        var afterIndex = index + keyword.Length;
        var afterOk = afterIndex >= sql.Length || !char.IsLetterOrDigit(sql[afterIndex]);
        return beforeOk && afterOk;
    }

    public static string ReadIdentifier(string sql, ref int pos)
    {
        while (pos < sql.Length && char.IsWhiteSpace(sql[pos])) pos++;
        if (pos >= sql.Length) return string.Empty;

        if (sql[pos] == '"')
        {
            pos++;
            var start = pos;
            while (pos < sql.Length && sql[pos] != '"')
                pos++;
            var result = sql[start..pos];
            if (pos < sql.Length && sql[pos] == '"')
                pos++;
            return result;
        }

        var idStart = pos;
        while (pos < sql.Length && (char.IsLetterOrDigit(sql[pos]) || sql[pos] == '_' || sql[pos] == '$'))
            pos++;
        return sql[idStart..pos];
    }
}
