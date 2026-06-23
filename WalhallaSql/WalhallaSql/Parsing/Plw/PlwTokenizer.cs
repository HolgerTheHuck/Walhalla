using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace WalhallaSql.Parsing.Plw;

/// <summary>
/// Token-Arten für die Walhalla Procedural Language (PLW).
/// </summary>
internal enum PlwTokenKind
{
    Unknown,
    Eof,

    // Literale
    Identifier,
    Number,
    String,
    DollarString,

    // Boolesche / NULL-Konstanten
    Null,
    True,
    False,
    And,
    Or,
    Not,

    // Schlüsselwörter (Kontrollstrukturen)
    Declare,
    Begin,
    End,
    If,
    Then,
    Elsif,
    Else,
    Loop,
    While,
    For,
    In,
    Exit,
    Continue,

    // SQL-artige Schlüsselwörter innerhalb von PLW
    Return,
    Query,
    Execute,
    Using,
    Into,
    Raise,
    Notice,
    Exception,
    Perform,
    Record,
    RecordFound,
    Open,
    Fetch,
    Close,

    // Operatoren und Satzzeichen
    ColonEquals,    // :=
    Semicolon,      // ;
    Comma,          // ,
    LeftParen,      // (
    RightParen,     // )
    Dot,            // .

    Plus,           // +
    Minus,          // -
    Star,           // *
    Slash,          // /
    Percent,        // %

    Equals,         // =
    LessThan,       // <
    GreaterThan,    // >
    LessEquals,     // <=
    GreaterEquals,  // >=
    NotEquals,      // <> oder !=
    Concat,         // ||

    Colon,          // :
    DoubleDot,      // ..
}

/// <summary>
/// Ein Token im PLW-Quelltext.
/// </summary>
internal sealed record PlwToken(
    PlwTokenKind Kind,
    string Text,
    int Position,
    int Length,
    int Line,
    int Column,
    string? DollarTag = null)
{
    public bool IsKeyword => Kind > PlwTokenKind.String && Kind < PlwTokenKind.ColonEquals;
}

/// <summary>
/// Zerlegt einen PLW-Prozedurbody in Tokens.
/// Kommentare (-- und /* */) werden übersprungen.
/// </summary>
internal static class PlwTokenizer
{
    public static IReadOnlyList<PlwToken> Tokenize(string source)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));

        var tokens = new List<PlwToken>();
        var reader = new SourceReader(source);

        while (!reader.IsAtEnd)
        {
            var token = ReadToken(ref reader);
            if (token.Kind != PlwTokenKind.Unknown)
                tokens.Add(token);
        }

        tokens.Add(new PlwToken(PlwTokenKind.Eof, string.Empty, reader.Position, 0, reader.Line, reader.Column));
        return tokens;
    }

    private static PlwToken ReadToken(ref SourceReader reader)
    {
        SkipWhitespace(ref reader);
        if (reader.IsAtEnd)
            return new PlwToken(PlwTokenKind.Eof, string.Empty, reader.Position, 0, reader.Line, reader.Column);

        int startPos = reader.Position;
        int startLine = reader.Line;
        int startCol = reader.Column;

        char c = reader.Current;

        if (IsIdentifierStart(c))
        {
            var text = ReadIdentifier(ref reader);
            var kind = MapKeyword(text);
            return CreateToken(kind, text, startPos, reader.Position - startPos, startLine, startCol);
        }

        if (char.IsDigit(c))
        {
            var text = ReadNumber(ref reader);
            return CreateToken(PlwTokenKind.Number, text, startPos, reader.Position - startPos, startLine, startCol);
        }

        if (c == '\'')
        {
            var text = ReadString(ref reader);
            return CreateToken(PlwTokenKind.String, text, startPos, reader.Position - startPos, startLine, startCol);
        }

        if (c == '$')
        {
            var (content, tag) = ReadDollarString(ref reader);
            return CreateToken(PlwTokenKind.DollarString, content, startPos, reader.Position - startPos, startLine, startCol, tag);
        }

        return ReadOperatorOrPunctuation(ref reader, startPos, startLine, startCol);
    }

    private static PlwToken CreateToken(PlwTokenKind kind, string text, int position, int length, int line, int column, string? dollarTag = null)
        => new PlwToken(kind, text, position, length, line, column, dollarTag);

    private static void SkipWhitespace(ref SourceReader reader)
    {
        while (!reader.IsAtEnd && char.IsWhiteSpace(reader.Current))
            reader.Advance();
    }

    private static bool IsIdentifierStart(char c)
        => char.IsLetter(c) || c == '_' || c == '@';

    private static bool IsIdentifierPart(char c)
        => char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '$';

    private static string ReadIdentifier(ref SourceReader reader)
    {
        var start = reader.Position;
        while (!reader.IsAtEnd && IsIdentifierPart(reader.Current))
            reader.Advance();
        return reader.Source[start..reader.Position];
    }

    private static string ReadNumber(ref SourceReader reader)
    {
        var start = reader.Position;
        bool dotSeen = false;
        while (!reader.IsAtEnd)
        {
            char c = reader.Current;
            if (char.IsDigit(c))
            {
                reader.Advance();
                continue;
            }
            if (c == '.' && !dotSeen && reader.Peek(1) is char next && char.IsDigit(next))
            {
                dotSeen = true;
                reader.Advance();
                continue;
            }
            break;
        }
        return reader.Source[start..reader.Position];
    }

    private static string ReadString(ref SourceReader reader)
    {
        // Einfache Anführungszeichen; '' ist Escape.
        reader.Advance(); // überspringe '
        var start = reader.Position;
        var sb = new System.Text.StringBuilder();
        while (!reader.IsAtEnd)
        {
            char c = reader.Current;
            if (c == '\'')
            {
                if (reader.Peek(1) == '\'')
                {
                    sb.Append('\'');
                    reader.Advance(2);
                    continue;
                }
                reader.Advance();
                break;
            }
            sb.Append(c);
            reader.Advance();
        }
        return sb.ToString();
    }

    private static (string content, string tag) ReadDollarString(ref SourceReader reader)
    {
        reader.Advance(); // überspringe '$'
        var tagStart = reader.Position;
        while (!reader.IsAtEnd && reader.Current != '$')
            reader.Advance();

        if (reader.IsAtEnd)
            throw new FormatException($"Unvollstaendiges Dollar-Quote bei Zeile {reader.Line}, Spalte {reader.Column}.");

        var tag = reader.Source[tagStart..reader.Position];
        reader.Advance(); // überspringe abschließendes '$' des Tags

        if (!string.IsNullOrEmpty(tag) && !tag.All(c => char.IsLetterOrDigit(c) || c == '_'))
            throw new FormatException($"Ungueltiges Dollar-Quote-Tag '{tag}' bei Zeile {reader.Line}, Spalte {reader.Column}.");

        var close = $"${tag}$";
        var contentStart = reader.Position;
        var closeIdx = reader.Source.IndexOf(close, contentStart, StringComparison.Ordinal);
        if (closeIdx < 0)
            throw new FormatException($"Schliessendes Dollar-Quote '{close}' nicht gefunden (gestartet bei Zeile {reader.Line}, Spalte {reader.Column}).");

        var content = reader.Source[contentStart..closeIdx];
        reader.Position = closeIdx + close.Length;
        return (content, tag);
    }

    private static PlwToken ReadOperatorOrPunctuation(ref SourceReader reader, int startPos, int startLine, int startCol)
    {
        char c = reader.Current;
        switch (c)
        {
            case ':':
                if (reader.Peek(1) == '=')
                {
                    reader.Advance(2);
                    return CreateToken(PlwTokenKind.ColonEquals, ":=", startPos, 2, startLine, startCol);
                }
                reader.Advance();
                return CreateToken(PlwTokenKind.Colon, ":", startPos, 1, startLine, startCol);

            case ';':
                reader.Advance();
                return CreateToken(PlwTokenKind.Semicolon, ";", startPos, 1, startLine, startCol);
            case ',':
                reader.Advance();
                return CreateToken(PlwTokenKind.Comma, ",", startPos, 1, startLine, startCol);
            case '(':
                reader.Advance();
                return CreateToken(PlwTokenKind.LeftParen, "(", startPos, 1, startLine, startCol);
            case ')':
                reader.Advance();
                return CreateToken(PlwTokenKind.RightParen, ")", startPos, 1, startLine, startCol);
            case '.':
                if (reader.Peek(1) == '.')
                {
                    reader.Advance(2);
                    return CreateToken(PlwTokenKind.DoubleDot, "..", startPos, 2, startLine, startCol);
                }
                reader.Advance();
                return CreateToken(PlwTokenKind.Dot, ".", startPos, 1, startLine, startCol);

            case '+':
                reader.Advance();
                return CreateToken(PlwTokenKind.Plus, "+", startPos, 1, startLine, startCol);
            case '-':
                if (reader.Peek(1) == '-')
                {
                    SkipLineComment(ref reader);
                    return CreateToken(PlwTokenKind.Unknown, string.Empty, startPos, 0, startLine, startCol);
                }
                reader.Advance();
                return CreateToken(PlwTokenKind.Minus, "-", startPos, 1, startLine, startCol);
            case '*':
                reader.Advance();
                return CreateToken(PlwTokenKind.Star, "*", startPos, 1, startLine, startCol);
            case '/':
                if (reader.Peek(1) == '*')
                {
                    SkipBlockComment(ref reader);
                    return CreateToken(PlwTokenKind.Unknown, string.Empty, startPos, 0, startLine, startCol);
                }
                reader.Advance();
                return CreateToken(PlwTokenKind.Slash, "/", startPos, 1, startLine, startCol);
            case '%':
                reader.Advance();
                return CreateToken(PlwTokenKind.Percent, "%", startPos, 1, startLine, startCol);

            case '=':
                reader.Advance();
                return CreateToken(PlwTokenKind.Equals, "=", startPos, 1, startLine, startCol);
            case '<':
                if (reader.Peek(1) == '=')
                {
                    reader.Advance(2);
                    return CreateToken(PlwTokenKind.LessEquals, "<=", startPos, 2, startLine, startCol);
                }
                if (reader.Peek(1) == '>')
                {
                    reader.Advance(2);
                    return CreateToken(PlwTokenKind.NotEquals, "<>", startPos, 2, startLine, startCol);
                }
                reader.Advance();
                return CreateToken(PlwTokenKind.LessThan, "<", startPos, 1, startLine, startCol);
            case '>':
                if (reader.Peek(1) == '=')
                {
                    reader.Advance(2);
                    return CreateToken(PlwTokenKind.GreaterEquals, ">=", startPos, 2, startLine, startCol);
                }
                reader.Advance();
                return CreateToken(PlwTokenKind.GreaterThan, ">", startPos, 1, startLine, startCol);
            case '!':
                if (reader.Peek(1) == '=')
                {
                    reader.Advance(2);
                    return CreateToken(PlwTokenKind.NotEquals, "!=", startPos, 2, startLine, startCol);
                }
                reader.Advance();
                return CreateToken(PlwTokenKind.Unknown, "!", startPos, 1, startLine, startCol);
            case '|':
                if (reader.Peek(1) == '|')
                {
                    reader.Advance(2);
                    return CreateToken(PlwTokenKind.Concat, "||", startPos, 2, startLine, startCol);
                }
                reader.Advance();
                return CreateToken(PlwTokenKind.Unknown, "|", startPos, 1, startLine, startCol);

            default:
                reader.Advance();
                return CreateToken(PlwTokenKind.Unknown, c.ToString(CultureInfo.InvariantCulture), startPos, 1, startLine, startCol);
        }
    }

    private static void SkipLineComment(ref SourceReader reader)
    {
        while (!reader.IsAtEnd && reader.Current != '\n')
            reader.Advance();
    }

    private static void SkipBlockComment(ref SourceReader reader)
    {
        reader.Advance(2); // überspringe /*
        while (!reader.IsAtEnd)
        {
            if (reader.Current == '*' && reader.Peek(1) == '/')
            {
                reader.Advance(2);
                return;
            }
            reader.Advance();
        }
        throw new FormatException($"Blockkommentar '/*' wurde nicht geschlossen (Zeile {reader.Line}, Spalte {reader.Column}).");
    }

    private static PlwTokenKind MapKeyword(string text)
    {
        return text.ToUpperInvariant() switch
        {
            "DECLARE" => PlwTokenKind.Declare,
            "BEGIN" => PlwTokenKind.Begin,
            "END" => PlwTokenKind.End,
            "IF" => PlwTokenKind.If,
            "THEN" => PlwTokenKind.Then,
            "ELSIF" => PlwTokenKind.Elsif,
            "ELSE" => PlwTokenKind.Else,
            "LOOP" => PlwTokenKind.Loop,
            "WHILE" => PlwTokenKind.While,
            "FOR" => PlwTokenKind.For,
            "IN" => PlwTokenKind.In,
            "EXIT" => PlwTokenKind.Exit,
            "CONTINUE" => PlwTokenKind.Continue,

            "RETURN" => PlwTokenKind.Return,
            "QUERY" => PlwTokenKind.Query,
            "EXECUTE" => PlwTokenKind.Execute,
            "USING" => PlwTokenKind.Using,
            "INTO" => PlwTokenKind.Into,
            "RAISE" => PlwTokenKind.Raise,
            "NOTICE" => PlwTokenKind.Notice,
            "EXCEPTION" => PlwTokenKind.Exception,
            "PERFORM" => PlwTokenKind.Perform,
            "RECORD" => PlwTokenKind.Record,
            "FOUND" => PlwTokenKind.RecordFound,
            "OPEN" => PlwTokenKind.Open,
            "FETCH" => PlwTokenKind.Fetch,
            "CLOSE" => PlwTokenKind.Close,

            "NULL" => PlwTokenKind.Null,
            "TRUE" => PlwTokenKind.True,
            "FALSE" => PlwTokenKind.False,
            "AND" => PlwTokenKind.And,
            "OR" => PlwTokenKind.Or,
            "NOT" => PlwTokenKind.Not,

            _ => PlwTokenKind.Identifier
        };
    }

    /// <summary>
    /// Hilfsstruktur zum Durchlaufen des Quelltextes mit Positions- und Zeilennachverfolgung.
    /// </summary>
    private struct SourceReader
    {
        public readonly string Source;
        public int Position;
        public int Line;
        public int Column;

        public SourceReader(string source)
        {
            Source = source;
            Position = 0;
            Line = 1;
            Column = 1;
        }

        public bool IsAtEnd => Position >= Source.Length;
        public char Current => IsAtEnd ? '\0' : Source[Position];

        public char? Peek(int offset)
            => Position + offset < Source.Length ? Source[Position + offset] : (char?)null;

        public void Advance()
        {
            if (IsAtEnd) return;
            if (Source[Position] == '\n')
            {
                Line++;
                Column = 1;
            }
            else
            {
                Column++;
            }
            Position++;
        }

        public void Advance(int count)
        {
            for (int i = 0; i < count; i++)
                Advance();
        }
    }
}
