using System;
using System.Collections.Generic;
using System.Text;

namespace WalhallaSql.Parsing.Plw;

/// <summary>
/// Rekursiv-absteigender Parser für PLW.
/// Baut aus der Token-Liste einen AST (PlwProgram + PlwBlock).
/// </summary>
internal static class PlwParser
{
    public static PlwProgram Parse(IReadOnlyList<PlwToken> tokens)
    {
        var reader = new TokenReader(tokens);
        var parameters = ParseParameterList(ref reader);
        var body = ParseBlock(ref reader);
        reader.Expect(PlwTokenKind.Eof);
        return new PlwProgram(parameters, body);
    }

    private static IReadOnlyList<PlwVariableDeclaration> ParseParameterList(ref TokenReader reader)
    {
        var parameters = new List<PlwVariableDeclaration>();
        if (reader.Current.Kind == PlwTokenKind.LeftParen)
        {
            reader.Advance(); // (
            while (reader.Current.Kind != PlwTokenKind.RightParen && reader.Current.Kind != PlwTokenKind.Eof)
            {
                var direction = ParseOptionalDirection(ref reader);
                var name = reader.Expect(PlwTokenKind.Identifier).Text;
                var typeName = reader.Expect(PlwTokenKind.Identifier).Text;
                PlwExpression? defaultValue = null;
                if (reader.Current.Kind == PlwTokenKind.Equals)
                {
                    reader.Advance();
                    defaultValue = ParseExpression(ref reader);
                }
                // Führende Richtung ignorieren wir im AST-Parameter; sie wird im Statement-Parser bereits gesetzt.
                _ = direction;
                parameters.Add(new PlwVariableDeclaration(name, typeName, defaultValue, true));
                if (reader.Current.Kind == PlwTokenKind.Comma)
                    reader.Advance();
            }
            reader.Expect(PlwTokenKind.RightParen);
        }
        return parameters;
    }

    private static string? ParseOptionalDirection(ref TokenReader reader)
    {
        if (reader.Current.Kind == PlwTokenKind.In ||
            reader.Current.Kind == PlwTokenKind.Or || // OUT fallschlägt wegen In-Or-Abfolge? Korrigiert unten.
            reader.Current.Kind == PlwTokenKind.Identifier && IsDirection(reader.Current.Text))
        {
            var text = reader.Current.Text;
            if (IsDirection(text))
            {
                reader.Advance();
                return text;
            }
        }
        return null;
    }

    private static bool IsDirection(string text)
        => text.Equals("IN", StringComparison.OrdinalIgnoreCase)
        || text.Equals("OUT", StringComparison.OrdinalIgnoreCase)
        || text.Equals("INOUT", StringComparison.OrdinalIgnoreCase);

    private static PlwBlock ParseBlock(ref TokenReader reader)
    {
        var declarations = new List<PlwVariableDeclaration>();
        if (reader.Current.Kind == PlwTokenKind.Declare)
        {
            reader.Advance();
            while (reader.Current.Kind != PlwTokenKind.Begin && reader.Current.Kind != PlwTokenKind.Eof)
            {
                var decl = ParseVariableDeclaration(ref reader);
                if (decl is not null)
                    declarations.Add(decl);
            }
        }

        reader.Expect(PlwTokenKind.Begin);
        var body = new List<PlwNode>();
        while (reader.Current.Kind != PlwTokenKind.End && reader.Current.Kind != PlwTokenKind.Eof)
        {
            body.Add(ParseStatement(ref reader));
        }
        reader.Expect(PlwTokenKind.End);
        if (reader.Current.Kind == PlwTokenKind.Semicolon)
            reader.Advance();

        return new PlwBlock(declarations, body);
    }

    private static PlwVariableDeclaration? ParseVariableDeclaration(ref TokenReader reader)
    {
        if (reader.Current.Kind == PlwTokenKind.Begin)
            return null;

        var name = reader.Expect(PlwTokenKind.Identifier).Text;
        var typeName = reader.Expect(PlwTokenKind.Identifier).Text;
        PlwExpression? defaultValue = null;
        if (reader.Current.Kind == PlwTokenKind.ColonEquals)
        {
            reader.Advance();
            defaultValue = ParseExpression(ref reader);
        }
        SkipOptionalSemicolon(ref reader);
        return new PlwVariableDeclaration(name, typeName, defaultValue);
    }

    private static PlwNode ParseStatement(ref TokenReader reader)
    {
        switch (reader.Current.Kind)
        {
            case PlwTokenKind.Begin:
                return ParseBlock(ref reader);
            case PlwTokenKind.If:
                return ParseIf(ref reader);
            case PlwTokenKind.Loop:
                return ParseSimpleLoop(ref reader);
            case PlwTokenKind.While:
                return ParseWhileLoop(ref reader);
            case PlwTokenKind.For:
                return ParseForLoop(ref reader);
            case PlwTokenKind.Exit:
                return ParseExit(ref reader);
            case PlwTokenKind.Continue:
                return ParseContinue(ref reader);
            case PlwTokenKind.Return:
                return ParseReturn(ref reader);
            case PlwTokenKind.Raise:
                return ParseRaise(ref reader);
            case PlwTokenKind.Perform:
                return ParsePerform(ref reader);
            case PlwTokenKind.Execute:
                return ParseExecute(ref reader);
            case PlwTokenKind.Open:
            case PlwTokenKind.Fetch:
            case PlwTokenKind.Close:
                return ParseCursorStatement(ref reader);
            default:
                // Möglichkeiten: Zuweisung, SELECT INTO, eingebettetes SQL, Variablen-Deklaration im falschen Block
                return ParseAssignmentOrSqlStatement(ref reader);
        }
    }

    private static PlwNode ParseStatementListOrSingle(ref TokenReader reader)
    {
        var stmts = new List<PlwNode>();
        while (reader.Current.Kind != PlwTokenKind.End && reader.Current.Kind != PlwTokenKind.Elsif && reader.Current.Kind != PlwTokenKind.Else && reader.Current.Kind != PlwTokenKind.Eof)
        {
            stmts.Add(ParseStatement(ref reader));
        }
        return stmts.Count == 1 ? stmts[0] : new PlwBlock(System.Array.Empty<PlwVariableDeclaration>(), stmts);
    }

    private static PlwIf ParseIf(ref TokenReader reader)
    {
        reader.Advance(); // IF
        var condition = ParseExpression(ref reader);
        reader.Expect(PlwTokenKind.Then);
        var then = ParseStatementListOrSingle(ref reader);
        var elsifBranches = new List<PlwElsif>();
        while (reader.Current.Kind == PlwTokenKind.Elsif)
        {
            reader.Advance();
            var elsifCond = ParseExpression(ref reader);
            reader.Expect(PlwTokenKind.Then);
            var elsifThen = ParseStatementListOrSingle(ref reader);
            elsifBranches.Add(new PlwElsif(elsifCond, elsifThen));
        }
        PlwNode? elseBranch = null;
        if (reader.Current.Kind == PlwTokenKind.Else)
        {
            reader.Advance();
            elseBranch = ParseStatementListOrSingle(ref reader);
        }
        reader.Expect(PlwTokenKind.End);
        if (reader.Current.Kind == PlwTokenKind.If ||
            (reader.Current.Kind == PlwTokenKind.Identifier && reader.Current.Text.Equals("IF", StringComparison.OrdinalIgnoreCase)))
            reader.Advance();
        SkipOptionalSemicolon(ref reader);
        return new PlwIf(condition, then, elsifBranches, elseBranch);
    }

    private static PlwNode ParseStatementListUntil(PlwTokenKind terminator, ref TokenReader reader)
    {
        var stmts = new List<PlwNode>();
        while (reader.Current.Kind != terminator && reader.Current.Kind != PlwTokenKind.Eof)
        {
            stmts.Add(ParseStatement(ref reader));
        }
        return stmts.Count == 1 ? stmts[0] : new PlwBlock(System.Array.Empty<PlwVariableDeclaration>(), stmts);
    }

    private static PlwNode ParseStatementListUntil(PlwTokenKind terminator, PlwTokenKind alt1, PlwTokenKind alt2, ref TokenReader reader)
    {
        var stmts = new List<PlwNode>();
        while (reader.Current.Kind != terminator && reader.Current.Kind != alt1 && reader.Current.Kind != alt2 && reader.Current.Kind != PlwTokenKind.Eof)
        {
            stmts.Add(ParseStatement(ref reader));
        }
        return stmts.Count == 1 ? stmts[0] : new PlwBlock(System.Array.Empty<PlwVariableDeclaration>(), stmts);
    }

    private static PlwNode ParseSimpleLoop(ref TokenReader reader)
    {
        reader.Advance(); // LOOP
        var body = new List<PlwNode>();
        while (reader.Current.Kind != PlwTokenKind.End && reader.Current.Kind != PlwTokenKind.Eof)
        {
            body.Add(ParseStatement(ref reader));
        }
        reader.Expect(PlwTokenKind.End);
        if (reader.Current.Kind == PlwTokenKind.Loop ||
            (reader.Current.Kind == PlwTokenKind.Identifier && reader.Current.Text.Equals("LOOP", StringComparison.OrdinalIgnoreCase)))
            reader.Advance();
        SkipOptionalSemicolon(ref reader);
        return new PlwSimpleLoop(new PlwBlock(System.Array.Empty<PlwVariableDeclaration>(), body));
    }

    private static PlwNode ParseWhileLoop(ref TokenReader reader)
    {
        reader.Advance(); // WHILE
        var condition = ParseExpression(ref reader);
        reader.Expect(PlwTokenKind.Loop);
        var body = ParseLoopBody(ref reader);
        return new PlwWhileLoop(condition, body);
    }

    private static PlwNode ParseForLoop(ref TokenReader reader)
    {
        reader.Advance(); // FOR
        var variable = reader.Expect(PlwTokenKind.Identifier).Text;
        bool reverse = false;
        if (reader.Current.Kind == PlwTokenKind.Identifier && reader.Current.Text.Equals("REVERSE", StringComparison.OrdinalIgnoreCase))
        {
            reverse = true;
            reader.Advance();
        }
        reader.Expect(PlwTokenKind.In);
        if (LooksLikeIntegerLoop(reader))
        {
            var first = ParseExpression(ref reader);
            reader.Expect(PlwTokenKind.DoubleDot);
            var second = ParseExpression(ref reader);
            reader.Expect(PlwTokenKind.Loop);
            var body = ParseLoopBody(ref reader);
            return new PlwForIntegerLoop(variable, first, second, body, reverse);
        }

        // FOR rec IN query LOOP
        var query = ParseSqlFragment(ref reader, PlwTokenKind.Loop);
        reader.Expect(PlwTokenKind.Loop);
        var loopBody = ParseLoopBody(ref reader);
        return new PlwForQueryLoop(variable, query, loopBody);
    }

    private static bool LooksLikeIntegerLoop(TokenReader reader)
    {
        var depth = 0;
        for (var i = reader.Position; i < reader.Tokens.Count; i++)
        {
            var token = reader.Tokens[i];
            if (token.Kind == PlwTokenKind.LeftParen)
            {
                depth++;
                continue;
            }
            if (token.Kind == PlwTokenKind.RightParen)
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }
            if (depth == 0 && token.Kind == PlwTokenKind.DoubleDot)
                return true;
            if (token.Kind == PlwTokenKind.Loop || token.Kind == PlwTokenKind.End || token.Kind == PlwTokenKind.Eof)
                return false;
        }
        return false;
    }

    private static PlwNode ParseLoopBody(ref TokenReader reader)
    {
        var body = new List<PlwNode>();
        while (reader.Current.Kind != PlwTokenKind.End && reader.Current.Kind != PlwTokenKind.Eof)
        {
            body.Add(ParseStatement(ref reader));
        }
        reader.Expect(PlwTokenKind.End);
        if (reader.Current.Kind == PlwTokenKind.Loop ||
            (reader.Current.Kind == PlwTokenKind.Identifier && reader.Current.Text.Equals("LOOP", StringComparison.OrdinalIgnoreCase)))
            reader.Advance();
        SkipOptionalSemicolon(ref reader);
        return new PlwBlock(System.Array.Empty<PlwVariableDeclaration>(), body);
    }

    private static PlwNode ParseExit(ref TokenReader reader)
    {
        reader.Advance(); // EXIT
        PlwExpression? when = null;
        if (reader.Current.Kind == PlwTokenKind.Identifier && reader.Current.Text.Equals("WHEN", StringComparison.OrdinalIgnoreCase))
        {
            reader.Advance();
            when = ParseExpression(ref reader);
        }
        SkipOptionalSemicolon(ref reader);
        return new PlwExit(when);
    }

    private static PlwNode ParseContinue(ref TokenReader reader)
    {
        reader.Advance(); // CONTINUE
        PlwExpression? when = null;
        if (reader.Current.Kind == PlwTokenKind.Identifier && reader.Current.Text.Equals("WHEN", StringComparison.OrdinalIgnoreCase))
        {
            reader.Advance();
            when = ParseExpression(ref reader);
        }
        SkipOptionalSemicolon(ref reader);
        return new PlwContinue(when);
    }

    private static PlwNode ParseReturn(ref TokenReader reader)
    {
        reader.Advance(); // RETURN
        if (reader.Current.Kind == PlwTokenKind.Query)
        {
            reader.Advance();
            var query = ParseSqlFragment(ref reader, PlwTokenKind.Semicolon, PlwTokenKind.End);
            SkipOptionalSemicolon(ref reader);
            return new PlwReturnQuery(query);
        }
        if (reader.Current.Kind == PlwTokenKind.Semicolon || reader.Current.Kind == PlwTokenKind.End)
        {
            SkipOptionalSemicolon(ref reader);
            return new PlwReturn(null);
        }
        var value = ParseExpression(ref reader);
        SkipOptionalSemicolon(ref reader);
        return new PlwReturn(value);
    }

    private static PlwNode ParseRaise(ref TokenReader reader)
    {
        reader.Advance(); // RAISE
        var level = "NOTICE";
        if (reader.Current.Kind == PlwTokenKind.Identifier || reader.Current.Kind == PlwTokenKind.Notice || reader.Current.Kind == PlwTokenKind.Exception)
        {
            level = reader.Current.Text.ToUpperInvariant();
            reader.Advance();
        }
        PlwExpression? message = null;
        var args = new List<PlwExpression>();
        if (reader.Current.Kind == PlwTokenKind.String)
        {
            message = ParseExpression(ref reader);
            while (reader.Current.Kind == PlwTokenKind.Comma)
            {
                reader.Advance();
                args.Add(ParseExpression(ref reader));
            }
        }
        SkipOptionalSemicolon(ref reader);
        return new PlwRaise(level, message, args);
    }

    private static PlwNode ParsePerform(ref TokenReader reader)
    {
        reader.Advance(); // PERFORM
        var sql = ParseSqlFragment(ref reader, PlwTokenKind.Semicolon, PlwTokenKind.End);
        SkipOptionalSemicolon(ref reader);
        return new PlwPerform(sql);
    }

    private static PlwNode ParseExecute(ref TokenReader reader)
    {
        reader.Advance(); // EXECUTE
        var sqlExpr = ParseExpression(ref reader);
        var intoTargets = new List<PlwExpression>();
        if (reader.Current.Kind == PlwTokenKind.Into)
        {
            reader.Advance();
            do
            {
                intoTargets.Add(new PlwIdentifierExpression(reader.Expect(PlwTokenKind.Identifier).Text));
            }
            while (reader.Current.Kind == PlwTokenKind.Comma && reader.AdvanceComma());
        }
        var args = new List<PlwExpression>();
        if (reader.Current.Kind == PlwTokenKind.Using)
        {
            reader.Advance();
            do
            {
                args.Add(ParseExpression(ref reader));
            }
            while (reader.Current.Kind == PlwTokenKind.Comma && reader.AdvanceComma());
        }
        SkipOptionalSemicolon(ref reader);
        return new PlwExecute(sqlExpr, intoTargets, args);
    }

    private static PlwNode ParseCursorStatement(ref TokenReader reader)
    {
        // OPEN/FETCH/CLOSE: als eingebettetes SQL weitergeben.
        var keyword = reader.Current.Text;
        reader.Advance();
        var sql = ParseSqlFragment(ref reader, PlwTokenKind.Semicolon, PlwTokenKind.End);
        SkipOptionalSemicolon(ref reader);
        return new PlwSqlStatement(new PlwSqlFragment(keyword + " " + sql.Text, sql.Arguments));
    }

    private static PlwNode ParseAssignmentOrSqlStatement(ref TokenReader reader)
    {
        var start = reader.Position;
        // Prüfe auf Zuweisung: identifier ( . identifier )* :=
        if (reader.Current.Kind == PlwTokenKind.Identifier)
        {
            var checkpoint = reader.Save();
            reader.Advance();
            while (reader.Current.Kind == PlwTokenKind.Dot)
            {
                reader.Advance();
                if (reader.Current.Kind == PlwTokenKind.Identifier)
                    reader.Advance();
            }
            if (reader.Current.Kind == PlwTokenKind.ColonEquals)
            {
                reader.Advance();
                var target = BuildTargetExpression(start, ref reader);
                var value = ParseExpression(ref reader);
                SkipOptionalSemicolon(ref reader);
                return new PlwAssignment(target, value);
            }
            reader.Restore(checkpoint);
        }

        // Prüfe auf SELECT INTO
        if (reader.Current.Kind == PlwTokenKind.Identifier && reader.Current.Text.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            var checkpoint = reader.Save();
            var sql = ParseSqlFragment(ref reader, PlwTokenKind.Semicolon, PlwTokenKind.End);
            // Wenn im SQL-Fragment "INTO" vor dem FROM vorkommt, ist es SELECT INTO.
            if (ContainsIntoBeforeFrom(sql.Text))
            {
                var (selectSql, targets) = SplitSelectInto(sql);
                SkipOptionalSemicolon(ref reader);
                return new PlwSelectInto(selectSql, targets);
            }
            reader.Restore(checkpoint);
        }

        // Sonst eingebettetes SQL bis Semikolon/End.
        var plainSql = ParseSqlFragment(ref reader, PlwTokenKind.Semicolon, PlwTokenKind.End);
        SkipOptionalSemicolon(ref reader);
        return new PlwSqlStatement(plainSql);
    }

    private static bool ContainsIntoBeforeFrom(string sql)
    {
        var intoIndex = sql.IndexOf(" INTO ", StringComparison.OrdinalIgnoreCase);
        var fromIndex = sql.IndexOf(" FROM ", StringComparison.OrdinalIgnoreCase);
        return intoIndex >= 0 && (fromIndex < 0 || intoIndex < fromIndex);
    }

    private static (PlwSqlFragment selectSql, IReadOnlyList<PlwExpression> targets) SplitSelectInto(PlwSqlFragment sql)
    {
        var text = sql.Text;
        var intoIndex = text.IndexOf(" INTO ", StringComparison.OrdinalIgnoreCase);
        var fromIndex = text.IndexOf(" FROM ", StringComparison.OrdinalIgnoreCase);
        if (intoIndex < 0)
            return (sql, Array.Empty<PlwExpression>());

        int intoEnd = intoIndex + 6;
        var targetSection = fromIndex > intoIndex ? text[intoEnd..fromIndex] : text[intoEnd..];
        var targetNames = targetSection.Split(',');
        var targets = new List<PlwExpression>();
        foreach (var t in targetNames)
        {
            var name = t.Trim();
            if (!string.IsNullOrEmpty(name))
                targets.Add(new PlwIdentifierExpression(name));
        }

        var selectText = text[..intoIndex] + (fromIndex > intoIndex ? text[fromIndex..] : string.Empty);
        var selectSql = new PlwSqlFragment(selectText, sql.Arguments, sql.IsDollarQuoted, sql.DollarTag);
        return (selectSql, targets);
    }

    private static PlwExpression BuildTargetExpression(int startPosition, ref TokenReader reader)
    {
        // Wir rekonstruieren den Ziel-Ausdruck aus dem bisher verbrauchten Text.
        // Der Reader ist bereits auf := gestanden; wir gehen zurück.
        var consumed = reader.Position - startPosition - 2; // "-2" für :=
        var text = reader.Tokens[startPosition].Text;
        var start = reader.Tokens[startPosition];
        for (int i = startPosition + 1; i < reader.Position - 1; i++)
        {
            var t = reader.Tokens[i];
            if (t.Kind == PlwTokenKind.Dot)
                text += ".";
            else if (t.Kind == PlwTokenKind.Identifier)
                text += t.Text;
        }
        var parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries);
        PlwExpression expr = new PlwIdentifierExpression(parts[0]);
        for (int i = 1; i < parts.Length; i++)
            expr = new PlwBinaryExpression(expr, PlwTokenKind.Dot, new PlwIdentifierExpression(parts[i]));
        return expr;
    }

    private static PlwSqlFragment ParseSqlFragment(ref TokenReader reader, params PlwTokenKind[] terminators)
    {
        var start = reader.Position;
        var sb = new StringBuilder();
        var args = new List<PlwExpression>();

        while (!reader.IsAtEnd && !IsTerminator(reader.Current.Kind, terminators))
        {
            var token = reader.Current;
            if (token.Kind == PlwTokenKind.DollarString)
            {
                var placeholder = $"{{{args.Count}}}";
                args.Add(new PlwStringExpression(token.Text)); // Inhalt als String-Ausdruck
                sb.Append(placeholder);
                reader.Advance();
                continue;
            }
            if (token.Kind == PlwTokenKind.String)
            {
                sb.Append('\'');
                sb.Append(token.Text.Replace("'", "''"));
                sb.Append('\'');
                reader.Advance();
                continue;
            }
            if (token.Kind == PlwTokenKind.Identifier && token.Text.StartsWith("@"))
            {
                // PLW-Variable im SQL: behalte @Name bei, später durch Wert ersetzt.
                sb.Append(token.Text);
                reader.Advance();
                continue;
            }
            if (token.Kind == PlwTokenKind.LeftParen || token.Kind == PlwTokenKind.RightParen ||
                token.Kind == PlwTokenKind.Comma || token.Kind == PlwTokenKind.Semicolon)
            {
                if (token.Kind == PlwTokenKind.Comma && sb.Length > 0 && sb[sb.Length - 1] == ' ')
                    sb.Length--;
                sb.Append(token.Text);
                if (token.Kind == PlwTokenKind.Comma)
                    sb.Append(' ');
                reader.Advance();
                continue;
            }
            sb.Append(token.Text);
            if (token.Kind != PlwTokenKind.Dot)
                sb.Append(' ');
            reader.Advance();
        }

        var text = sb.ToString().Trim();
        return new PlwSqlFragment(text, args);
    }

    private static bool IsTerminator(PlwTokenKind kind, PlwTokenKind[] terminators)
    {
        foreach (var t in terminators)
            if (kind == t) return true;
        return false;
    }

    private static void SkipOptionalSemicolon(ref TokenReader reader)
    {
        if (reader.Current.Kind == PlwTokenKind.Semicolon)
            reader.Advance();
    }

    // ── Ausdrücke ──────────────────────────────────────────────────────────────

    private static PlwExpression ParseExpression(ref TokenReader reader)
    {
        return ParseOr(ref reader);
    }

    private static PlwExpression ParseOr(ref TokenReader reader)
    {
        var left = ParseAnd(ref reader);
        while (reader.Current.Kind == PlwTokenKind.Or)
        {
            var op = reader.Current.Kind;
            reader.Advance();
            left = new PlwBinaryExpression(left, op, ParseAnd(ref reader));
        }
        return left;
    }

    private static PlwExpression ParseAnd(ref TokenReader reader)
    {
        var left = ParseComparison(ref reader);
        while (reader.Current.Kind == PlwTokenKind.And)
        {
            var op = reader.Current.Kind;
            reader.Advance();
            left = new PlwBinaryExpression(left, op, ParseComparison(ref reader));
        }
        return left;
    }

    private static PlwExpression ParseComparison(ref TokenReader reader)
    {
        var left = ParseConcat(ref reader);
        while (reader.Current.Kind is PlwTokenKind.Equals or PlwTokenKind.NotEquals
               or PlwTokenKind.LessThan or PlwTokenKind.GreaterThan
               or PlwTokenKind.LessEquals or PlwTokenKind.GreaterEquals)
        {
            var op = reader.Current.Kind;
            reader.Advance();
            left = new PlwBinaryExpression(left, op, ParseConcat(ref reader));
        }
        return left;
    }

    private static PlwExpression ParseConcat(ref TokenReader reader)
    {
        var left = ParseTerm(ref reader);
        while (reader.Current.Kind == PlwTokenKind.Concat)
        {
            var op = reader.Current.Kind;
            reader.Advance();
            left = new PlwBinaryExpression(left, op, ParseTerm(ref reader));
        }
        return left;
    }

    private static PlwExpression ParseTerm(ref TokenReader reader)
    {
        var left = ParseFactor(ref reader);
        while (reader.Current.Kind is PlwTokenKind.Plus or PlwTokenKind.Minus)
        {
            var op = reader.Current.Kind;
            reader.Advance();
            left = new PlwBinaryExpression(left, op, ParseFactor(ref reader));
        }
        return left;
    }

    private static PlwExpression ParseFactor(ref TokenReader reader)
    {
        var left = ParseUnary(ref reader);
        while (reader.Current.Kind is PlwTokenKind.Star or PlwTokenKind.Slash or PlwTokenKind.Percent)
        {
            var op = reader.Current.Kind;
            reader.Advance();
            left = new PlwBinaryExpression(left, op, ParseUnary(ref reader));
        }
        return left;
    }

    private static PlwExpression ParseUnary(ref TokenReader reader)
    {
        if (reader.Current.Kind is PlwTokenKind.Minus or PlwTokenKind.Not)
        {
            var op = reader.Current.Kind;
            reader.Advance();
            return new PlwUnaryExpression(op, ParsePrimary(ref reader));
        }
        return ParsePrimary(ref reader);
    }

    private static PlwExpression ParsePrimary(ref TokenReader reader)
    {
        var token = reader.Current;
        switch (token.Kind)
        {
            case PlwTokenKind.Number:
                reader.Advance();
                return new PlwNumberExpression(token.Text);
            case PlwTokenKind.String:
                reader.Advance();
                return new PlwStringExpression(token.Text);
            case PlwTokenKind.True:
                reader.Advance();
                return new PlwBooleanExpression(true);
            case PlwTokenKind.False:
                reader.Advance();
                return new PlwBooleanExpression(false);
            case PlwTokenKind.Null:
                reader.Advance();
                return new PlwNullExpression();
            case PlwTokenKind.Identifier:
                reader.Advance();
                var name = token.Text;
                if (reader.Current.Kind == PlwTokenKind.LeftParen)
                {
                    // Funktionsaufruf
                    reader.Advance();
                    var args = new List<PlwExpression>();
                    if (reader.Current.Kind != PlwTokenKind.RightParen)
                    {
                        do
                        {
                            args.Add(ParseExpression(ref reader));
                        }
                        while (reader.Current.Kind == PlwTokenKind.Comma && reader.AdvanceComma());
                    }
                    reader.Expect(PlwTokenKind.RightParen);
                    PlwExpression callArgs = args.Count == 1 ? args[0] : new PlwSqlFragment(string.Empty, args);
                    return new PlwBinaryExpression(
                        new PlwIdentifierExpression(name),
                        PlwTokenKind.LeftParen,
                        callArgs);
                }
                if (reader.Current.Kind == PlwTokenKind.Dot)
                {
                    reader.Advance();
                    var member = reader.Expect(PlwTokenKind.Identifier).Text;
                    return new PlwBinaryExpression(
                        new PlwIdentifierExpression(name),
                        PlwTokenKind.Dot,
                        new PlwIdentifierExpression(member));
                }
                return new PlwIdentifierExpression(name);
            case PlwTokenKind.LeftParen:
                reader.Advance();
                var expr = ParseExpression(ref reader);
                reader.Expect(PlwTokenKind.RightParen);
                return expr;
            default:
                throw new WalhallaSyntaxException($"Unerwarteter Token '{token.Text}' in Ausdruck bei Zeile {token.Line}, Spalte {token.Column}.");
        }
    }

    /// <summary>
    /// Hilfsstruktur zum Durchlaufen der Token-Liste.
    /// </summary>
    private struct TokenReader
    {
        public readonly IReadOnlyList<PlwToken> Tokens;
        public int Position;

        public TokenReader(IReadOnlyList<PlwToken> tokens)
        {
            Tokens = tokens;
            Position = 0;
        }

        public PlwToken Current => Position < Tokens.Count ? Tokens[Position] : Tokens[^1];
        public bool IsAtEnd => Current.Kind == PlwTokenKind.Eof;

        public void Advance() => Position++;

        public bool AdvanceComma()
        {
            if (Current.Kind == PlwTokenKind.Comma)
            {
                Advance();
                return true;
            }
            return false;
        }

        public PlwToken Expect(PlwTokenKind kind)
        {
            var token = Current;
            if (token.Kind != kind)
                throw new WalhallaSyntaxException($"Erwartet {kind}, gefunden '{token.Text}' bei Zeile {token.Line}, Spalte {token.Column}.");
            Position++;
            return token;
        }

        public int Save() => Position;
        public void Restore(int checkpoint) => Position = checkpoint;
    }
}
