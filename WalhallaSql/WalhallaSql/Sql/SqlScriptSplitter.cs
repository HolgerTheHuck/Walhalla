using System;
using System.Collections.Generic;
using System.Text;

namespace WalhallaSql.Sql;

/// <summary>
/// Zerlegt ein SQL-Skript in einzelne ausfuehrbare Statements.
/// CREATE PROCEDURE/TRIGGER-Blöcke werden als Ganzes erkannt, damit der darin
/// enthaltene C#-Code (Verbatim-Strings, Semikolons, Kommentare) nicht zerstört
/// wird. Zeilen- und Blockkommentare im restlichen SQL werden entfernt.
/// </summary>
public static class SqlScriptSplitter
{
    /// <summary>
    /// Teilt das SQL an Semikolons auf dem obersten Syntax-Level in einzelne
    /// Statements. CREATE PROCEDURE/TRIGGER-Blöcke werden als Ganzes erkannt,
    /// Kommentare und überflüssige Leerzeichen entfernt. Leere Fragmente
    /// werden ignoriert.
    /// </summary>
    public static IReadOnlyList<string> Split(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return Array.Empty<string>();

        // CREATE PROCEDURE/TRIGGER-Blöcke zuerst herausziehen, damit deren
        // Inhalt (C#-Code mit Semikolons, Kommentaren etc.) nicht zerstört wird.
        var (preservedSql, createBlocks) = ExtractCreateBlocks(sql);
        var cleaned = RemoveComments(preservedSql);
        var statements = SplitStatements(cleaned, createBlocks);

        // Leere Fragmente entfernen
        var result = new List<string>(statements.Count);
        foreach (var stmt in statements)
        {
            if (!string.IsNullOrWhiteSpace(stmt))
                result.Add(stmt.Trim());
        }

        return result;
    }

    /// <summary>
    /// Ersetzt CREATE PROCEDURE/TRIGGER-Blöcke durch Platzhalter und gibt die
    /// zugehörigen Original-Blöcke zurück. Dadurch bleiben C#-Verbatim-Strings
    /// und Kommentare im Prozedur-Body unverändert.
    /// </summary>
    private static (string Sql, Dictionary<string, string> Blocks) ExtractCreateBlocks(string sql)
    {
        var blocks = new Dictionary<string, string>();
        var builder = new StringBuilder(sql.Length);
        var i = 0;

        while (i < sql.Length)
        {
            var c = sql[i];

            // SQL-String-Literale überspringen
            if (c == '\'')
            {
                builder.Append(c);
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == '\'')
                        {
                            builder.Append('\'');
                            builder.Append('\'');
                            i += 2;
                            continue;
                        }
                        builder.Append('\'');
                        i++;
                        break;
                    }
                    builder.Append(sql[i]);
                    i++;
                }
                continue;
            }

            // Blockkommentar überspringen
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                builder.Append("/*");
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/'))
                {
                    builder.Append(sql[i]);
                    i++;
                }
                if (i + 1 < sql.Length)
                {
                    builder.Append("*/");
                    i += 2;
                }
                continue;
            }

            // Zeilenkommentar überspringen
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                i += 2;
                while (i < sql.Length && sql[i] != '\n')
                    i++;
                if (i < sql.Length && sql[i] == '\n')
                    i++;
                continue;
            }

            var remaining = sql[i..];
            if (StartsWithCreateProcedureOrTrigger(remaining))
            {
                var end = FindEndOfCreateBlock(sql, i);
                var block = sql[i..end];
                var placeholder = $"__CREATE_BLOCK_{blocks.Count}__";
                blocks[placeholder] = block;
                builder.Append(placeholder);
                i = end;
                continue;
            }

            builder.Append(c);
            i++;
        }

        return (builder.ToString(), blocks);
    }

    private static string RemoveComments(string sql)
    {
        var builder = new StringBuilder(sql.Length);
        var i = 0;

        while (i < sql.Length)
        {
            var c = sql[i];

            // Zeichenkettenliteral ueberspringen
            if (c == '\'')
            {
                builder.Append(c);
                i++;
                while (i < sql.Length)
                {
                    var inner = sql[i];
                    builder.Append(inner);
                    if (inner == '\'')
                    {
                        // Escaped quote ('') -> beide Zeichen gehoeren zum Literal
                        if (i + 1 < sql.Length && sql[i + 1] == '\'')
                        {
                            i++;
                            builder.Append('\'');
                        }
                        else
                        {
                            break;
                        }
                    }
                    i++;
                }
                i++;
                continue;
            }

            // Zeilenkommentar
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                i += 2;
                while (i < sql.Length && sql[i] != '\n')
                    i++;
                // Newline behalten, damit Zeilen nicht zusammengeklebt werden
                if (i < sql.Length && sql[i] == '\n')
                {
                    builder.Append('\n');
                    i++;
                }
                continue;
            }

            // Blockkommentar
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/'))
                    i++;
                if (i + 1 < sql.Length)
                    i += 2;
                // Leerzeichen statt Kommentar einfuegen, damit benachbarte Token
                // nicht verschmelzen (z. B. "Id/*x*/INT" -> "Id INT").
                if (builder.Length > 0 && !char.IsWhiteSpace(builder[builder.Length - 1]))
                    builder.Append(' ');
                continue;
            }

            builder.Append(c);
            i++;
        }

        return builder.ToString();
    }

    private static List<string> SplitStatements(string sql, Dictionary<string, string> createBlocks)
    {
        var statements = new List<string>();
        var start = 0;
        var i = 0;

        while (i < sql.Length)
        {
            var c = sql[i];

            // String-Literal ueberspringen
            if (c == '\'')
            {
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == '\'')
                        {
                            i += 2;
                            continue;
                        }
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }

            if (c == ';')
            {
                var fragment = sql[start..i].Trim();
                if (!string.IsNullOrWhiteSpace(fragment))
                    AddStatement(statements, fragment, createBlocks);
                i++;
                start = i;
                continue;
            }

            i++;
        }

        if (start < sql.Length)
        {
            var fragment = sql[start..].Trim();
            if (!string.IsNullOrWhiteSpace(fragment))
                AddStatement(statements, fragment, createBlocks);
        }

        return statements;
    }

    private static void AddStatement(List<string> statements, string fragment, Dictionary<string, string> createBlocks)
    {
        foreach (var placeholder in createBlocks.Keys)
        {
            if (fragment.Contains(placeholder, StringComparison.OrdinalIgnoreCase))
            {
                fragment = fragment.Replace(placeholder, createBlocks[placeholder]);
            }
        }

        statements.Add(fragment.Trim());
    }

    private static bool StartsWithCreateProcedureOrTrigger(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("CREATE OR REPLACE PROCEDURE", StringComparison.OrdinalIgnoreCase))
            return true;
        if (trimmed.StartsWith("CREATE PROCEDURE", StringComparison.OrdinalIgnoreCase))
            return true;
        if (trimmed.StartsWith("CREATE OR REPLACE TRIGGER", StringComparison.OrdinalIgnoreCase))
            return true;
        if (trimmed.StartsWith("CREATE TRIGGER", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static int FindEndOfCreateBlock(string sql, int startIndex)
    {
        var i = startIndex;
        var inString = false;
        var beginDepth = 0;

        while (i < sql.Length)
        {
            var c = sql[i];

            if (c == '\'')
            {
                if (inString)
                {
                    if (i + 1 < sql.Length && sql[i + 1] == '\'')
                    {
                        i += 2;
                        continue;
                    }
                    inString = false;
                }
                else
                {
                    inString = true;
                }
                i++;
                continue;
            }

            if (inString)
            {
                i++;
                continue;
            }

            if (MatchesKeywordAt(sql, i, "BEGIN"))
            {
                beginDepth++;
                i += 5;
                continue;
            }

            if (MatchesKeywordAt(sql, i, "END"))
            {
                beginDepth--;
                if (beginDepth <= 0)
                {
                    // Ende des aeuusseren Blocks gefunden.
                    i += 3;
                    // Optional ein abschliessendes Semikolon mit einbeziehen
                    while (i < sql.Length && char.IsWhiteSpace(sql[i]))
                        i++;
                    if (i < sql.Length && sql[i] == ';')
                        i++;
                    return i;
                }
                i += 3;
                continue;
            }

            i++;
        }

        // Kein END gefunden -> Rest als ein Statement nehmen
        return sql.Length;
    }

    private static bool MatchesKeywordAt(string text, int index, string keyword)
    {
        if (index < 0 || index + keyword.Length > text.Length)
            return false;

        var candidate = text.Substring(index, keyword.Length);
        if (!candidate.Equals(keyword, StringComparison.OrdinalIgnoreCase))
            return false;

        var prefixBoundary = index == 0
            || char.IsWhiteSpace(text[index - 1])
            || text[index - 1] == '('
            || text[index - 1] == ')'
            || text[index - 1] == ';';

        var next = index + keyword.Length;
        var suffixBoundary = next >= text.Length
            || char.IsWhiteSpace(text[next])
            || text[next] == '('
            || text[next] == ')'
            || text[next] == ';';

        return prefixBoundary && suffixBoundary;
    }
}
