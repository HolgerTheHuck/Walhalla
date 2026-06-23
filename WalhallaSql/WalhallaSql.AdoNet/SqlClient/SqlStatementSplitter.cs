using System;
using System.Collections.Generic;
using System.Text;

namespace WalhallaSql.AdoNet.SqlClient;

/// <summary>
/// Teilt einen SQL-Command-String an Semikolons in einzelne Statements auf,
/// ohne String-Literale oder Blockkommentare zu zerstören.
/// </summary>
internal static class SqlStatementSplitter
{
    public static IReadOnlyList<string> Split(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return Array.Empty<string>();

        var statements = new List<string>();
        var builder = new StringBuilder();
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var inLineComment = false;
        var inBlockComment = false;

        for (var i = 0; i < sql.Length; i++)
        {
            var current = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (inLineComment)
            {
                if (current == '\n')
                    inLineComment = false;
                builder.Append(current);
                continue;
            }

            if (inBlockComment)
            {
                if (current == '*' && next == '/')
                {
                    inBlockComment = false;
                    builder.Append(current);
                    builder.Append(next);
                    i++;
                    continue;
                }
                builder.Append(current);
                continue;
            }

            if (inSingleQuote)
            {
                builder.Append(current);
                if (current == '\'')
                {
                    // Escaped quote inside string: ''
                    if (next == '\'')
                    {
                        builder.Append(next);
                        i++;
                        continue;
                    }
                    inSingleQuote = false;
                }
                continue;
            }

            if (inDoubleQuote)
            {
                builder.Append(current);
                if (current == '"')
                {
                    if (next == '"')
                    {
                        builder.Append(next);
                        i++;
                        continue;
                    }
                    inDoubleQuote = false;
                }
                continue;
            }

            if (current == '\'')
            {
                inSingleQuote = true;
                builder.Append(current);
                continue;
            }

            if (current == '"')
            {
                inDoubleQuote = true;
                builder.Append(current);
                continue;
            }

            if (current == '-' && next == '-')
            {
                inLineComment = true;
                builder.Append(current);
                builder.Append(next);
                i++;
                continue;
            }

            if (current == '/' && next == '*')
            {
                inBlockComment = true;
                builder.Append(current);
                builder.Append(next);
                i++;
                continue;
            }

            if (current == ';')
            {
                var part = builder.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(part))
                    statements.Add(part);
                builder.Clear();
                continue;
            }

            builder.Append(current);
        }

        var last = builder.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(last))
            statements.Add(last);

        return statements;
    }
}
