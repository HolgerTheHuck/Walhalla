using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

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
                var typeName = ParseTypeName(ref reader);
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

    private static PlwBlock ParseBlock(ref TokenReader reader, string? label = null)
    {
        var declarations = new List<PlwNode>();
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
        while (reader.Current.Kind != PlwTokenKind.End
               && reader.Current.Kind != PlwTokenKind.Exception
               && reader.Current.Kind != PlwTokenKind.Eof)
        {
            body.Add(ParseStatement(ref reader));
        }

        var handlers = new List<PlwExceptionHandler>();
        if (reader.Current.Kind == PlwTokenKind.Exception)
        {
            reader.Advance(); // EXCEPTION
            while (reader.Current.Kind == PlwTokenKind.When)
            {
                reader.Advance(); // WHEN
                var condition = ParseExceptionCondition(ref reader);
                reader.Expect(PlwTokenKind.Then);
                var handlerBody = ParseStatementListUntil(PlwTokenKind.When, PlwTokenKind.End, ref reader);
                handlers.Add(new PlwExceptionHandler(condition, handlerBody));
            }
        }

        reader.Expect(PlwTokenKind.End);
        ConsumeOptionalBlockLabel(ref reader, label);
        if (reader.Current.Kind == PlwTokenKind.Semicolon)
            reader.Advance();

        return new PlwBlock(declarations, body, handlers, label);
    }

    private static void ConsumeOptionalBlockLabel(ref TokenReader reader, string? expectedLabel)
    {
        if (reader.Current.Kind != PlwTokenKind.Identifier)
            return;
        var text = reader.Current.Text;
        if (expectedLabel != null && !text.Equals(expectedLabel, StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"Block-Label stimmt nicht ueberein: Ende '{text}', erwartet '{expectedLabel}'.");
        reader.Advance();
    }

    private static string ParseExceptionCondition(ref TokenReader reader)
    {
        // Erlaubt: OTHERS, identifier (z. B. division_by_zero), String-Literal (SQLSTATE)
        if (reader.Current.Kind == PlwTokenKind.Others)
        {
            reader.Advance();
            return "OTHERS";
        }

        if (reader.Current.Kind == PlwTokenKind.String)
        {
            var text = reader.Current.Text;
            reader.Advance();
            return text;
        }

        return reader.Expect(PlwTokenKind.Identifier).Text;
    }

    private static string ParseTypeName(ref TokenReader reader)
    {
        var sb = new StringBuilder();
        if (reader.Current.Kind == PlwTokenKind.Record)
        {
            sb.Append(reader.Current.Text);
            reader.Advance();
        }
        else
        {
            sb.Append(reader.Expect(PlwTokenKind.Identifier).Text);
        }

        if (reader.Current.Kind == PlwTokenKind.Percent)
        {
            reader.Advance();
            if (reader.Current.Kind == PlwTokenKind.Row)
            {
                sb.Append("%ROWTYPE");
                reader.Advance();
            }
            else
            {
                var typeOrName = reader.Expect(PlwTokenKind.Identifier).Text;
                sb.Append($"%{typeOrName}");
            }
        }

        while (reader.Current.Kind == PlwTokenKind.LeftBracket)
        {
            reader.Advance();
            reader.Expect(PlwTokenKind.RightBracket);
            sb.Append("[]");
        }

        return sb.ToString();
    }

    private static PlwNode? ParseVariableDeclaration(ref TokenReader reader)
    {
        if (reader.Current.Kind == PlwTokenKind.Begin)
            return null;

        var name = reader.Expect(PlwTokenKind.Identifier).Text;

        // Cursor-Deklaration: name CURSOR FOR query;
        if (reader.Current.Kind == PlwTokenKind.Cursor)
        {
            reader.Advance();
            reader.Expect(PlwTokenKind.For);
            var query = ParseSqlFragment(ref reader, PlwTokenKind.Semicolon, PlwTokenKind.End);
            SkipOptionalSemicolon(ref reader);
            return new PlwCursorDeclaration(name, query);
        }

        var typeName = ParseTypeName(ref reader);

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
        var label = TryParseLabel(ref reader);
        switch (reader.Current.Kind)
        {
            case PlwTokenKind.Begin:
                if (label != null)
                    return ParseBlock(ref reader, label);
                return ParseBlock(ref reader);
            case PlwTokenKind.If:
                if (label != null)
                    throw new FormatException($"Label '{label}' vor IF nicht erlaubt.");
                return ParseIf(ref reader);
            case PlwTokenKind.Loop:
                return ParseSimpleLoop(ref reader, label);
            case PlwTokenKind.While:
                return ParseWhileLoop(ref reader, label);
            case PlwTokenKind.For:
                return ParseForLoop(ref reader, label);
            case PlwTokenKind.Foreach:
                return ParseForeachLoop(ref reader, label);
            case PlwTokenKind.Forall:
                return ParseForallLoop(ref reader, label);
            case PlwTokenKind.Exit:
                return ParseExit(ref reader);
            case PlwTokenKind.Continue:
                return ParseContinue(ref reader);
            case PlwTokenKind.Return:
                if (label != null)
                    throw new FormatException($"Label '{label}' vor RETURN nicht erlaubt.");
                return ParseReturn(ref reader);
            case PlwTokenKind.Raise:
                if (label != null)
                    throw new FormatException($"Label '{label}' vor RAISE nicht erlaubt.");
                return ParseRaise(ref reader);
            case PlwTokenKind.Perform:
                if (label != null)
                    throw new FormatException($"Label '{label}' vor PERFORM nicht erlaubt.");
                return ParsePerform(ref reader);
            case PlwTokenKind.Execute:
                if (label != null)
                    throw new FormatException($"Label '{label}' vor EXECUTE nicht erlaubt.");
                return ParseExecute(ref reader);
            case PlwTokenKind.Open:
            case PlwTokenKind.Fetch:
            case PlwTokenKind.Close:
                if (label != null)
                    throw new FormatException($"Label '{label}' vor Cursor-Anweisung nicht erlaubt.");
                return ParseCursorStatement(ref reader);
            default:
                if (label != null)
                    throw new FormatException($"Label '{label}' vor diesem Statement nicht erlaubt.");
                // Möglichkeiten: Zuweisung, SELECT INTO, eingebettetes SQL, Variablen-Deklaration im falschen Block
                return ParseAssignmentOrSqlStatement(ref reader);
        }
    }

    private static string? TryParseLabel(ref TokenReader reader)
    {
        if (reader.Current.Kind == PlwTokenKind.LabelStart)
        {
            var label = reader.Current.Text;
            reader.Advance();
            return label;
        }
        return null;
    }

    private static PlwNode ParseStatementListOrSingle(ref TokenReader reader)
    {
        var stmts = new List<PlwNode>();
        while (reader.Current.Kind != PlwTokenKind.End && reader.Current.Kind != PlwTokenKind.Elsif && reader.Current.Kind != PlwTokenKind.Else && reader.Current.Kind != PlwTokenKind.Eof)
        {
            stmts.Add(ParseStatement(ref reader));
        }
        return stmts.Count == 1 ? stmts[0] : new PlwBlock(System.Array.Empty<PlwNode>(), stmts);
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
        return stmts.Count == 1 ? stmts[0] : new PlwBlock(System.Array.Empty<PlwNode>(), stmts);
    }

    private static PlwNode ParseStatementListUntil(PlwTokenKind terminator, PlwTokenKind alt1, PlwTokenKind alt2, ref TokenReader reader)
    {
        var stmts = new List<PlwNode>();
        while (reader.Current.Kind != terminator && reader.Current.Kind != alt1 && reader.Current.Kind != alt2 && reader.Current.Kind != PlwTokenKind.Eof)
        {
            stmts.Add(ParseStatement(ref reader));
        }
        return stmts.Count == 1 ? stmts[0] : new PlwBlock(System.Array.Empty<PlwNode>(), stmts);
    }

    private static PlwNode ParseStatementListUntil(PlwTokenKind alt1, PlwTokenKind alt2, ref TokenReader reader)
    {
        var stmts = new List<PlwNode>();
        while (reader.Current.Kind != alt1 && reader.Current.Kind != alt2 && reader.Current.Kind != PlwTokenKind.Eof)
        {
            stmts.Add(ParseStatement(ref reader));
        }
        return stmts.Count == 1 ? stmts[0] : new PlwBlock(System.Array.Empty<PlwNode>(), stmts);
    }

    private static PlwNode ParseSimpleLoop(ref TokenReader reader, string? label = null)
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
        ConsumeOptionalBlockLabel(ref reader, label);
        SkipOptionalSemicolon(ref reader);
        return new PlwSimpleLoop(new PlwBlock(System.Array.Empty<PlwNode>(), body), label);
    }

    private static PlwNode ParseWhileLoop(ref TokenReader reader, string? label = null)
    {
        reader.Advance(); // WHILE
        var condition = ParseExpression(ref reader);
        reader.Expect(PlwTokenKind.Loop);
        var body = ParseLoopBody(ref reader, label);
        return new PlwWhileLoop(condition, body, label);
    }

    private static PlwNode ParseForLoop(ref TokenReader reader, string? label = null)
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
            var body = ParseLoopBody(ref reader, label);
            return new PlwForIntegerLoop(variable, first, second, body, reverse, label);
        }

        // FOR rec IN query LOOP
        var query = ParseSqlFragment(ref reader, PlwTokenKind.Loop);
        reader.Expect(PlwTokenKind.Loop);
        var loopBody = ParseLoopBody(ref reader, label);
        return new PlwForQueryLoop(variable, query, loopBody, label);
    }

    private static PlwNode ParseForeachLoop(ref TokenReader reader, string? label = null)
    {
        reader.Advance(); // FOREACH
        var variable = reader.Expect(PlwTokenKind.Identifier).Text;
        reader.Expect(PlwTokenKind.In);
        var arrayExpr = ParseExpression(ref reader);
        reader.Expect(PlwTokenKind.Loop);
        var body = ParseLoopBody(ref reader, label);
        return new PlwForeachLoop(variable, arrayExpr, body, label);
    }

    private static PlwNode ParseForallLoop(ref TokenReader reader, string? label = null)
    {
        reader.Advance(); // FORALL
        var variable = reader.Expect(PlwTokenKind.Identifier).Text;
        reader.Expect(PlwTokenKind.In);

        PlwForallRange range;
        if (reader.Current.Kind == PlwTokenKind.Indices)
        {
            reader.Advance();
            reader.Expect(PlwTokenKind.Of);
            var arrayExpr = ParseExpression(ref reader);
            range = new PlwForallIndicesOfRange(arrayExpr);
        }
        else
        {
            var lower = ParseExpression(ref reader);
            reader.Expect(PlwTokenKind.DoubleDot);
            var upper = ParseExpression(ref reader);
            range = new PlwForallIntegerRange(lower, upper);
        }

        reader.Expect(PlwTokenKind.Loop);
        var body = ParseLoopBody(ref reader, label);
        return new PlwForallLoop(variable, range, body, label);
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

    private static PlwNode ParseLoopBody(ref TokenReader reader, string? label = null)
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
        ConsumeOptionalBlockLabel(ref reader, label);
        SkipOptionalSemicolon(ref reader);
        return new PlwBlock(System.Array.Empty<PlwNode>(), body, null, label);
    }

    private static PlwNode ParseExit(ref TokenReader reader)
    {
        reader.Advance(); // EXIT
        string? label = null;
        if (reader.Current.Kind == PlwTokenKind.Identifier)
        {
            label = reader.Current.Text;
            reader.Advance();
        }
        PlwExpression? when = null;
        if (reader.Current.Kind == PlwTokenKind.When)
        {
            reader.Advance();
            when = ParseExpression(ref reader);
        }
        SkipOptionalSemicolon(ref reader);
        return new PlwExit(when, label);
    }

    private static PlwNode ParseContinue(ref TokenReader reader)
    {
        reader.Advance(); // CONTINUE
        string? label = null;
        if (reader.Current.Kind == PlwTokenKind.Identifier)
        {
            label = reader.Current.Text;
            reader.Advance();
        }
        PlwExpression? when = null;
        if (reader.Current.Kind == PlwTokenKind.When)
        {
            reader.Advance();
            when = ParseExpression(ref reader);
        }
        SkipOptionalSemicolon(ref reader);
        return new PlwContinue(when, label);
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

        PlwExpression? sqlState = null;
        PlwExpression? hint = null;
        PlwExpression? detail = null;
        if (reader.Current.Kind == PlwTokenKind.Using)
        {
            reader.Advance();
            do
            {
                if (reader.Current.Kind == PlwTokenKind.SqlState)
                {
                    reader.Advance();
                    reader.Expect(PlwTokenKind.Equals);
                    sqlState = ParseExpression(ref reader);
                }
                else if (reader.Current.Kind == PlwTokenKind.Hint)
                {
                    reader.Advance();
                    reader.Expect(PlwTokenKind.Equals);
                    hint = ParseExpression(ref reader);
                }
                else if (reader.Current.Kind == PlwTokenKind.Detail)
                {
                    reader.Advance();
                    reader.Expect(PlwTokenKind.Equals);
                    detail = ParseExpression(ref reader);
                }
                else
                {
                    throw new WalhallaSyntaxException($"Unerwartete USING-Option '{reader.Current.Text}' in RAISE.");
                }
            }
            while (reader.Current.Kind == PlwTokenKind.Comma && reader.AdvanceComma());
        }

        SkipOptionalSemicolon(ref reader);
        return new PlwRaise(level, message, args, sqlState, hint, detail);
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
        var kind = reader.Current.Kind;
        reader.Advance();
        var cursorName = reader.Expect(PlwTokenKind.Identifier).Text;

        if (kind == PlwTokenKind.Open)
        {
            SkipOptionalSemicolon(ref reader);
            return new PlwOpenCursor(cursorName);
        }

        if (kind == PlwTokenKind.Close)
        {
            SkipOptionalSemicolon(ref reader);
            return new PlwCloseCursor(cursorName);
        }

        // FETCH cursor INTO target1, target2, ...
        reader.Expect(PlwTokenKind.Into);
        var targets = new List<PlwExpression>();
        do
        {
            targets.Add(new PlwIdentifierExpression(reader.Expect(PlwTokenKind.Identifier).Text));
        }
        while (reader.Current.Kind == PlwTokenKind.Comma && reader.AdvanceComma());
        SkipOptionalSemicolon(ref reader);
        return new PlwFetchCursor(cursorName, targets);
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
        var intoMatch = Regex.Match(sql, @"\bINTO\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!intoMatch.Success)
            return false;

        var fromMatch = Regex.Match(sql, @"\bFROM\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return !fromMatch.Success || intoMatch.Index < fromMatch.Index;
    }

    private static (PlwSqlFragment selectSql, IReadOnlyList<PlwExpression> targets) SplitSelectInto(PlwSqlFragment sql)
    {
        var text = sql.Text;
        var intoMatch = Regex.Match(text, @"\bINTO\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var fromMatch = Regex.Match(text, @"\bFROM\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!intoMatch.Success)
            return (sql, Array.Empty<PlwExpression>());

        var intoIndex = intoMatch.Index;
        var fromIndex = fromMatch.Success ? fromMatch.Index : -1;
        int intoEnd = intoMatch.Index + intoMatch.Length;
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
            var nextToken = reader.Position + 1 < reader.Tokens.Count ? reader.Tokens[reader.Position + 1] : null;
            if (token.Kind != PlwTokenKind.Dot && nextToken?.Kind != PlwTokenKind.Dot)
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
        PlwExpression expr;
        switch (token.Kind)
        {
            case PlwTokenKind.Number:
                reader.Advance();
                expr = new PlwNumberExpression(token.Text);
                break;
            case PlwTokenKind.String:
                reader.Advance();
                expr = new PlwStringExpression(token.Text);
                break;
            case PlwTokenKind.True:
                reader.Advance();
                expr = new PlwBooleanExpression(true);
                break;
            case PlwTokenKind.False:
                reader.Advance();
                expr = new PlwBooleanExpression(false);
                break;
            case PlwTokenKind.Null:
                reader.Advance();
                expr = new PlwNullExpression();
                break;
            case PlwTokenKind.RecordFound:
                // FOUND ist die boolesche Systemvariable des PLW-Interpreters.
                reader.Advance();
                expr = new PlwIdentifierExpression("FOUND");
                break;
            case PlwTokenKind.SqlState:
                // SQLSTATE ist die Systemvariable fuer den aktuellen SQLSTATE.
                reader.Advance();
                expr = new PlwIdentifierExpression("SQLSTATE");
                break;
            case PlwTokenKind.Row:
                reader.Advance();
                reader.Expect(PlwTokenKind.LeftParen);
                var rowArgs = new List<PlwExpression>();
                if (reader.Current.Kind != PlwTokenKind.RightParen)
                {
                    do
                    {
                        rowArgs.Add(ParseExpression(ref reader));
                    }
                    while (reader.Current.Kind == PlwTokenKind.Comma && reader.AdvanceComma());
                }
                reader.Expect(PlwTokenKind.RightParen);
                expr = new PlwRowLiteralExpression(rowArgs);
                break;
            case PlwTokenKind.Identifier:
                reader.Advance();
                var name = token.Text;
                if (name.Equals("ARRAY", StringComparison.OrdinalIgnoreCase)
                    && reader.Current.Kind == PlwTokenKind.LeftBracket)
                {
                    reader.Advance(); // [
                    var elements = new List<PlwExpression>();
                    if (reader.Current.Kind != PlwTokenKind.RightBracket)
                    {
                        do
                        {
                            elements.Add(ParseExpression(ref reader));
                        }
                        while (reader.Current.Kind == PlwTokenKind.Comma && reader.AdvanceComma());
                    }
                    reader.Expect(PlwTokenKind.RightBracket);
                    expr = new PlwArrayLiteralExpression(elements);
                    break;
                }
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
                    expr = new PlwBinaryExpression(
                        new PlwIdentifierExpression(name),
                        PlwTokenKind.LeftParen,
                        callArgs);
                    break;
                }
                expr = new PlwIdentifierExpression(name);
                break;
            case PlwTokenKind.LeftParen:
                reader.Advance();
                expr = ParseExpression(ref reader);
                reader.Expect(PlwTokenKind.RightParen);
                break;
            default:
                throw new WalhallaSyntaxException($"Unerwarteter Token '{token.Text}' in Ausdruck bei Zeile {token.Line}, Spalte {token.Column}.");
        }

        // Postfix-Operatoren: Feldzugriff und Array-Index
        while (reader.Current.Kind is PlwTokenKind.Dot or PlwTokenKind.LeftBracket)
        {
            if (reader.Current.Kind == PlwTokenKind.Dot)
            {
                reader.Advance();
                var member = reader.Expect(PlwTokenKind.Identifier).Text;
                expr = new PlwFieldAccessExpression(expr, member);
            }
            else
            {
                reader.Advance(); // [
                var index = ParseExpression(ref reader);
                reader.Expect(PlwTokenKind.RightBracket);
                expr = new PlwArraySubscriptExpression(expr, index);
            }
        }

        return expr;
    }

    /// <summary>
    /// Parst einen einzelnen PLW-Ausdruck aus einer Zeichenkette.
    /// Liefert null, wenn der Text kein gueltiger Ausdruck ist.
    /// </summary>
    internal static PlwExpression? TryParseExpression(string source)
    {
        try
        {
            var tokens = PlwTokenizer.Tokenize(source);
            var reader = new TokenReader(tokens);
            var expr = ParseExpression(ref reader);
            if (!reader.IsAtEnd)
                return null;
            return expr;
        }
        catch
        {
            return null;
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
