using System;
using System.Collections.Generic;
using System.Text;

namespace WalhallaSql.Sql;

/// <summary>
/// Zerlegt ein SQL-Skript in einzelne ausfuehrbare Statements.
/// Beruecksichtigt String-Literale und entfernt Zeilen- sowie Blockkommentare.
/// Statements, die mit CREATE PROCEDURE/TRIGGER beginnen, werden als Ganzes
/// bis zum abschliessenden END auf oberster Ebene zusammengehalten.
/// </summary>
public static class SqlScriptSplitter
{
    /// <summary>
    /// Entfernt Kommentare und teilt das SQL an Semikolons auf dem obersten
    /// Syntax-Level in einzelne Statements. Leere Fragmente werden ignoriert.
    /// </summary>
    public static IReadOnlyList<string> Split(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return Array.Empty<string>();

        var cleaned = RemoveComments(sql);
        var statements = SplitStatements(cleaned);

        // Leere Fragmente entfernen
        var result = new List<string>(statements.Count);
        foreach (var stmt in statements)
        {
            if (!string.IsNullOrWhiteSpace(stmt))
                result.Add(stmt.Trim());
        }

        return result;
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

    private static List<string> SplitStatements(string sql)
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

            // CREATE PROCEDURE / TRIGGER als Ganzes halten
            if (i == start || (i > 0 && char.IsWhiteSpace(sql[i - 1])))
            {
                var remaining = sql[i..];
                if (StartsWithCreateProcedureOrTrigger(remaining))
                {
                    var end = FindEndOfCreateBlock(sql, i);
                    if (end > i)
                    {
                        statements.Add(sql[start..end].Trim());
                        i = end;
                        start = i;
                        continue;
                    }
                }
            }

            if (c == ';')
            {
                statements.Add(sql[start..i].Trim());
                i++;
                start = i;
                continue;
            }

            i++;
        }

        if (start < sql.Length)
            statements.Add(sql[start..].Trim());

        return statements;
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
