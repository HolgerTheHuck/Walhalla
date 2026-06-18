using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace WalhallaSql.EfCore.Linq;

internal static class WalhallaSqlWhereEvaluator
{
    public static bool Evaluate(string? filterExpression, IReadOnlyDictionary<string, object?> row)
    {
        if (string.IsNullOrWhiteSpace(filterExpression))
            return true;

        var tokens = Tokenize(filterExpression);
        var index = 0;
        var result = ParseOrExpression(tokens, ref index, row);
        return result;
    }

    private sealed class Token
    {
        public TokenType Type { get; init; }
        public string Value { get; init; } = string.Empty;
    }

    private enum TokenType
    {
        Identifier,
        Operator,
        Literal,
        LeftParen,
        RightParen,
        Comma,
        Keyword
    }

    private static List<Token> Tokenize(string expression)
    {
        var tokens = new List<Token>();
        var i = 0;

        while (i < expression.Length)
        {
            var c = expression[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '(')
            {
                tokens.Add(new Token { Type = TokenType.LeftParen, Value = "(" });
                i++;
                continue;
            }

            if (c == ')')
            {
                tokens.Add(new Token { Type = TokenType.RightParen, Value = ")" });
                i++;
                continue;
            }

            if (c == ',')
            {
                tokens.Add(new Token { Type = TokenType.Comma, Value = "," });
                i++;
                continue;
            }

            if (c == '=' || c == '!' || c == '>' || c == '<')
            {
                var op = c.ToString();
                i++;

                if (c == '!' && i < expression.Length && expression[i] == '=')
                {
                    op = "!=";
                    i++;
                }
                else if (c == '>' && i < expression.Length && expression[i] == '=')
                {
                    op = ">=";
                    i++;
                }
                else if (c == '<' && i < expression.Length && expression[i] == '=')
                {
                    op = "<=";
                    i++;
                }
                else if (c == '<' && i < expression.Length && expression[i] == '>')
                {
                    op = "<>";
                    i++;
                }

                tokens.Add(new Token { Type = TokenType.Operator, Value = op });
                continue;
            }

            if (c == '\'')
            {
                var start = i;
                i++;
                while (i < expression.Length)
                {
                    if (expression[i] == '\'' && i + 1 < expression.Length && expression[i + 1] == '\'')
                    {
                        i += 2;
                        continue;
                    }

                    if (expression[i] == '\'')
                    {
                        i++;
                        break;
                    }

                    i++;
                }

                var raw = expression.Substring(start, i - start);
                var unescaped = raw.Substring(1, raw.Length - 2).Replace("''", "'");
                tokens.Add(new Token { Type = TokenType.Literal, Value = unescaped });
                continue;
            }

            if (char.IsDigit(c) || (c == '-' && i + 1 < expression.Length && char.IsDigit(expression[i + 1])))
            {
                var start = i;
                i++;
                while (i < expression.Length && (char.IsDigit(expression[i]) || expression[i] == '.'))
                    i++;

                tokens.Add(new Token { Type = TokenType.Literal, Value = expression.Substring(start, i - start) });
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                i++;
                while (i < expression.Length && (char.IsLetterOrDigit(expression[i]) || expression[i] == '_'))
                    i++;

                var word = expression.Substring(start, i - start);

                if (word.Equals("AND", StringComparison.OrdinalIgnoreCase)
                    || word.Equals("OR", StringComparison.OrdinalIgnoreCase))
                {
                    tokens.Add(new Token { Type = TokenType.Operator, Value = word.ToUpperInvariant() });
                }
                else if (word.Equals("NULL", StringComparison.OrdinalIgnoreCase))
                {
                    tokens.Add(new Token { Type = TokenType.Keyword, Value = "NULL" });
                }
                else if (word.Equals("IS", StringComparison.OrdinalIgnoreCase)
                    || word.Equals("NOT", StringComparison.OrdinalIgnoreCase)
                    || word.Equals("IN", StringComparison.OrdinalIgnoreCase))
                {
                    tokens.Add(new Token { Type = TokenType.Keyword, Value = word.ToUpperInvariant() });
                }
                else
                {
                    tokens.Add(new Token { Type = TokenType.Identifier, Value = word });
                }

                continue;
            }

            i++;
        }

        return tokens;
    }

    private static bool ParseOrExpression(List<Token> tokens, ref int index, IReadOnlyDictionary<string, object?> row)
    {
        var left = ParseAndExpression(tokens, ref index, row);

        while (index < tokens.Count && tokens[index].Type == TokenType.Operator && tokens[index].Value == "OR")
        {
            index++;
            var right = ParseAndExpression(tokens, ref index, row);
            left = left || right;
        }

        return left;
    }

    private static bool ParseAndExpression(List<Token> tokens, ref int index, IReadOnlyDictionary<string, object?> row)
    {
        var left = ParseComparison(tokens, ref index, row);

        while (index < tokens.Count && tokens[index].Type == TokenType.Operator && tokens[index].Value == "AND")
        {
            index++;
            var right = ParseComparison(tokens, ref index, row);
            left = left && right;
        }

        return left;
    }

    private static bool ParseComparison(List<Token> tokens, ref int index, IReadOnlyDictionary<string, object?> row)
    {
        if (index >= tokens.Count)
            return false;

        if (tokens[index].Type == TokenType.LeftParen)
        {
            index++;
            var result = ParseOrExpression(tokens, ref index, row);
            if (index < tokens.Count && tokens[index].Type == TokenType.RightParen)
                index++;
            return result;
        }

        if (tokens[index].Type != TokenType.Identifier)
            throw new InvalidOperationException($"Expected column identifier but found '{tokens[index].Value}'.");

        var column = tokens[index].Value;
        index++;

        if (index >= tokens.Count)
            return false;

        // IS NULL / IS NOT NULL
        if (tokens[index].Type == TokenType.Keyword && tokens[index].Value == "IS")
        {
            index++;
            var isNot = false;
            if (index < tokens.Count && tokens[index].Type == TokenType.Keyword && tokens[index].Value == "NOT")
            {
                isNot = true;
                index++;
            }

            if (index >= tokens.Count || tokens[index].Type != TokenType.Keyword || tokens[index].Value != "NULL")
                throw new InvalidOperationException("Expected NULL after IS [NOT].");

            index++;
            var columnValue = row.TryGetValue(column, out var val) ? val : null;
            return isNot ? columnValue != null : columnValue == null;
        }

        // IN (...)
        if (tokens[index].Type == TokenType.Keyword && tokens[index].Value == "IN")
        {
            index++;
            if (index >= tokens.Count || tokens[index].Type != TokenType.LeftParen)
                throw new InvalidOperationException("Expected '(' after IN.");

            index++;
            var values = new List<string>();
            while (index < tokens.Count && tokens[index].Type != TokenType.RightParen)
            {
                if (tokens[index].Type == TokenType.Literal)
                {
                    values.Add(tokens[index].Value);
                    index++;
                }
                else if (tokens[index].Type == TokenType.Comma)
                {
                    index++;
                    continue;
                }
                else
                {
                    throw new InvalidOperationException($"Unexpected token '{tokens[index].Value}' in IN list.");
                }
            }

            if (index < tokens.Count && tokens[index].Type == TokenType.RightParen)
                index++;

            var columnForIn = row.TryGetValue(column, out var inVal) ? inVal : null;
            if (columnForIn == null)
                return values.Any(v => v == string.Empty);

            var colStr = Convert.ToString(columnForIn, CultureInfo.InvariantCulture) ?? string.Empty;
            foreach (var literal in values)
            {
                if (literal.Equals(colStr, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        // Binary operators: =, !=, <>, >, <, >=, <=
        if (tokens[index].Type != TokenType.Operator)
            throw new InvalidOperationException($"Expected operator but found '{tokens[index].Value}'.");

        var op = tokens[index].Value;
        index++;

        if (index >= tokens.Count)
            throw new InvalidOperationException("Expected value after operator.");

        string? rightValue;
        if (tokens[index].Type == TokenType.Literal)
        {
            rightValue = tokens[index].Value;
            index++;
        }
        else if (tokens[index].Type == TokenType.Keyword && tokens[index].Value == "NULL")
        {
            rightValue = null;
            index++;
        }
        else
        {
            throw new InvalidOperationException($"Expected literal value but found '{tokens[index].Value}'.");
        }

        var leftValue = row.TryGetValue(column, out var lv) ? lv : null;
        return CompareValues(leftValue, rightValue, op);
    }

    private static bool CompareValues(object? left, string? right, string op)
    {
        if (right == null)
        {
            return op switch
            {
                "=" => left == null,
                "!=" => left != null,
                "<>" => left != null,
                _ => throw new InvalidOperationException($"Operator '{op}' is not valid with NULL.")
            };
        }

        if (left == null)
            return false;

        var leftStr = Convert.ToString(left, CultureInfo.InvariantCulture) ?? string.Empty;

        // Try numeric comparison first
        if (IsNumericString(leftStr) && IsNumericString(right))
        {
            var leftNum = decimal.Parse(leftStr, CultureInfo.InvariantCulture);
            var rightNum = decimal.Parse(right, CultureInfo.InvariantCulture);
            return op switch
            {
                "=" => leftNum == rightNum,
                "!=" => leftNum != rightNum,
                "<>" => leftNum != rightNum,
                ">" => leftNum > rightNum,
                "<" => leftNum < rightNum,
                ">=" => leftNum >= rightNum,
                "<=" => leftNum <= rightNum,
                _ => false
            };
        }

        var cmp = string.Compare(leftStr, right, StringComparison.OrdinalIgnoreCase);
        return op switch
        {
            "=" => cmp == 0,
            "!=" => cmp != 0,
            "<>" => cmp != 0,
            ">" => cmp > 0,
            "<" => cmp < 0,
            ">=" => cmp >= 0,
            "<=" => cmp <= 0,
            _ => false
        };
    }

    private static bool IsNumericString(string text)
    {
        return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out _);
    }
}
