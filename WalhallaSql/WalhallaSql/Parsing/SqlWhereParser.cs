using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using WalhallaSql.Sql;

namespace WalhallaSql.Parsing;

internal static class SqlWhereParser
{
    public static SqlWhereExpression Parse(string whereClause)
    {
        if (string.IsNullOrWhiteSpace(whereClause))
            throw new ArgumentException("WHERE clause must not be empty.", nameof(whereClause));

        try
        {
            return new Parser(whereClause).Parse();
        }
        catch (Exception ex) when (ex is NotSupportedException or FormatException)
        {
            throw new NotSupportedException($"{ex.Message} WHERE clause: {whereClause}", ex);
        }
    }

    public static (SqlWhereExpression Expression, IReadOnlyList<string> Parameters) ParseWithParameters(
        string whereClause)
    {
        if (string.IsNullOrWhiteSpace(whereClause))
            throw new ArgumentException("WHERE clause must not be empty.", nameof(whereClause));

        try
        {
            var parser = new Parser(whereClause);
            var expr = parser.Parse();
            return (expr, parser.GetParameters());
        }
        catch (Exception ex) when (ex is NotSupportedException or FormatException)
        {
            throw new NotSupportedException($"{ex.Message} WHERE clause: {whereClause}", ex);
        }
    }

    public static SqlWhereValueExpression ParseValueExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new ArgumentException("Value expression must not be empty.", nameof(expression));

        try
        {
            return new Parser(expression).ParseValueOnly();
        }
        catch (Exception ex) when (ex is NotSupportedException or FormatException)
        {
            throw new NotSupportedException($"{ex.Message} Value expression: {expression}", ex);
        }
    }

    private sealed class Parser
    {
        private readonly string _text;
        private int _position;
        private readonly Dictionary<string, int> _parameters = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _parameterNames = new();

        public Parser(string text)
        {
            _text = text ?? throw new ArgumentNullException(nameof(text));
        }

        public SqlWhereExpression Parse()
        {
            var expression = ParseOr();
            SkipWhitespace();
            if (!IsEnd())
                throw new NotSupportedException($"Unexpected token near '{_text[_position..]}'.");
            return expression;
        }

        public IReadOnlyList<string> GetParameters() => _parameterNames.AsReadOnly();

        public SqlWhereValueExpression ParseValueOnly()
        {
            var expression = ParseValueExpression();
            SkipWhitespace();
            if (!IsEnd())
                throw new NotSupportedException($"Unexpected token near '{_text[_position..]}'.");
            return expression;
        }

        private SqlWhereExpression ParseOr()
        {
            var children = new List<SqlWhereExpression> { ParseAnd() };
            while (TryConsumeKeyword("OR"))
                children.Add(ParseAnd());
            return children.Count == 1 ? children[0] : new SqlWhereOrExpression(children);
        }

        private SqlWhereExpression ParseAnd()
        {
            var children = new List<SqlWhereExpression> { ParseBitwiseOr() };
            while (TryConsumeKeyword("AND"))
                children.Add(ParseBitwiseOr());
            return children.Count == 1 ? children[0] : new SqlWhereAndExpression(children);
        }

        private SqlWhereExpression ParseBitwiseOr()
        {
            var children = new List<SqlWhereExpression> { ParseBitwiseAnd() };
            while (TryConsumeBitwiseOr())
                children.Add(ParseBitwiseAnd());
            return children.Count == 1 ? children[0] : new SqlWhereOrExpression(children);
        }

        private SqlWhereExpression ParseBitwiseAnd()
        {
            var children = new List<SqlWhereExpression> { ParseUnary() };
            while (TryConsumeBitwiseAnd())
                children.Add(ParseUnary());
            return children.Count == 1 ? children[0] : new SqlWhereAndExpression(children);
        }

        private SqlWhereExpression ParseUnary()
        {
            if (TryConsumeKeyword("NOT"))
                return new SqlWhereNotExpression(ParseUnary());

            if (TryConsumeKeyword("EXISTS"))
                return new SqlWhereExistsExpression(ParseSubqueryParenthesized(), false);

            if (LooksLikeSubqueryParenthesized())
                return ParsePredicate();

            if (LooksLikeParenthesizedValuePredicate())
                return ParsePredicate();

            if (TryConsumeChar('('))
            {
                var inner = ParseOr();
                ExpectChar(')');
                return inner;
            }

            return ParsePredicate();
        }

        private bool LooksLikeParenthesizedValuePredicate()
        {
            SkipWhitespace();
            if (!PeekChar('(')) return false;

            var snapshot = _position;
            try
            {
                _position++;
                var _ = ParseValueExpression();
                SkipWhitespace();
                if (!TryConsumeChar(')')) return false;

                SkipWhitespace();
                return IsEnd()
                    || PeekKeyword("IS")
                    || PeekKeyword("BETWEEN")
                    || PeekKeyword("IN")
                    || PeekKeyword("LIKE")
                    || PeekKeyword("AND")
                    || PeekKeyword("OR")
                    || LooksLikeComparisonOperator();
            }
            catch (NotSupportedException)
            {
                return false;
            }
            finally
            {
                _position = snapshot;
            }
        }

        private SqlWhereExpression ParsePredicate()
        {
            var left = ParseValueExpression();

            if (TryConsumeText("@>"))
                return new SqlWhereJsonContainsExpression(left, ParseValueExpression(), SqlJsonContainmentOperator.Contains);

            if (TryConsumeText("<@"))
                return new SqlWhereJsonContainsExpression(left, ParseValueExpression(), SqlJsonContainmentOperator.ContainedBy);

            if (TryConsumeText("?|"))
                return new SqlWhereJsonKeyExistsExpression(left, ParseValueExpression(), SqlJsonKeyExistsOperator.HasAnyKey);

            if (TryConsumeText("?&"))
                return new SqlWhereJsonKeyExistsExpression(left, ParseValueExpression(), SqlJsonKeyExistsOperator.HasAllKeys);

            if (TryConsumeText("?"))
                return new SqlWhereJsonKeyExistsExpression(left, ParseValueExpression(), SqlJsonKeyExistsOperator.HasKey);

            if (TryConsumeKeyword("IS"))
            {
                var negated = TryConsumeKeyword("NOT");
                if (TryConsumeKeyword("DISTINCT"))
                {
                    ExpectKeyword("FROM");
                    var distinctRight = ParseValueExpression();
                    return new SqlWhereDistinctFromExpression(left, distinctRight, negated);
                }
                if (!negated) ExpectKeyword("NULL");
                else ExpectKeyword("NULL");
                return new SqlWhereNullCheckExpression(left, negated);
            }

            var negatedPredicate = TryConsumeKeyword("NOT");

            if (TryConsumeKeyword("BETWEEN"))
            {
                var lower = ParseValueExpression();
                ExpectKeyword("AND");
                var upper = ParseValueExpression();
                return new SqlWhereBetweenExpression(left, lower, upper, negatedPredicate);
            }

            if (TryConsumeKeyword("IN"))
            {
                if (LooksLikeSubqueryParenthesized())
                    return new SqlWhereInSubqueryExpression(left, ParseSubqueryParenthesized(), negatedPredicate);

                ExpectChar('(');
                var values = new List<SqlWhereValueExpression>();
                do { values.Add(ParseValueExpression()); }
                while (TryConsumeChar(','));

                ExpectChar(')');
                return new SqlWhereInListExpression(left, values, negatedPredicate);
            }

            if (TryConsumeKeyword("LIKE"))
                return new SqlWhereLikeExpression(left, ParseValueExpression(), negatedPredicate);

            if (negatedPredicate)
                throw new NotSupportedException("Only NOT BETWEEN, NOT IN, NOT LIKE, NOT EXISTS, and logical NOT are supported.");

            if (!TryConsumeComparison(out var op))
            {
                if (left is SqlWhereColumnExpression)
                    return new SqlWhereComparisonExpression(left, SqlWhereComparisonOperator.Equal, new SqlWhereLiteralExpression(true));

                if (left is SqlWhereLiteralExpression or SqlWhereCaseExpression or SqlWhereFunctionCallExpression)
                    return new SqlWhereTruthyExpression(left);

                throw new NotSupportedException($"Expected predicate operator near '{_text[_position..]}'.");
            }

            if (TryConsumeKeyword("ANY"))
                return new SqlWhereQuantifiedComparisonExpression(left, op, SqlWhereQuantifier.Any, ParseSubqueryParenthesized());

            if (TryConsumeKeyword("SOME"))
                return new SqlWhereQuantifiedComparisonExpression(left, op, SqlWhereQuantifier.Some, ParseSubqueryParenthesized());

            if (TryConsumeKeyword("ALL"))
                return new SqlWhereQuantifiedComparisonExpression(left, op, SqlWhereQuantifier.All, ParseSubqueryParenthesized());

            var right = ParseValueExpression();
            return new SqlWhereComparisonExpression(left, op, right);
        }

        private SqlWhereValueExpression ParseValueExpression() => ParseAdditiveValueExpression();

        private SqlWhereValueExpression ParseAdditiveValueExpression()
        {
            var left = ParseMultiplicativeValueExpression();
            while (true)
            {
                if (TryConsumeChar('+'))
                {
                    left = new SqlWhereBinaryValueExpression(left, SqlWhereBinaryOperator.Add, ParseMultiplicativeValueExpression());
                    continue;
                }
                if (TryConsumeChar('-'))
                {
                    left = new SqlWhereBinaryValueExpression(left, SqlWhereBinaryOperator.Subtract, ParseMultiplicativeValueExpression());
                    continue;
                }
                return left;
            }
        }

        private SqlWhereValueExpression ParseMultiplicativeValueExpression()
        {
            var left = ParseUnaryValueExpression();
            while (true)
            {
                if (TryConsumeChar('*'))
                {
                    left = new SqlWhereBinaryValueExpression(left, SqlWhereBinaryOperator.Multiply, ParseUnaryValueExpression());
                    continue;
                }
                if (TryConsumeChar('/'))
                {
                    left = new SqlWhereBinaryValueExpression(left, SqlWhereBinaryOperator.Divide, ParseUnaryValueExpression());
                    continue;
                }
                if (TryConsumeChar('%'))
                {
                    left = new SqlWhereBinaryValueExpression(left, SqlWhereBinaryOperator.Modulo, ParseUnaryValueExpression());
                    continue;
                }
                return left;
            }
        }

        private SqlWhereValueExpression ParseUnaryValueExpression()
        {
            if (TryConsumeChar('+')) return new SqlWhereUnaryValueExpression(SqlWhereUnaryOperator.Plus, ParseUnaryValueExpression());
            if (TryConsumeChar('-')) return new SqlWhereUnaryValueExpression(SqlWhereUnaryOperator.Minus, ParseUnaryValueExpression());
            if (TryConsumeChar('~')) return new SqlWhereUnaryValueExpression(SqlWhereUnaryOperator.BitwiseNot, ParseUnaryValueExpression());
            return ParsePrimaryValueExpression();
        }

        private SqlWhereValueExpression ParsePrimaryValueExpression()
        {
            SkipWhitespace();
            if (IsEnd()) throw new NotSupportedException("Unexpected end of WHERE clause.");

            if (LooksLikeSubqueryParenthesized())
                return new SqlWhereScalarSubqueryValueExpression(ParseSubqueryParenthesized());

            if (PeekKeyword("CASE")) return ParseCaseExpression();
            if (PeekKeyword("COALESCE")) return ParseCoalesceExpression();
            if (PeekKeyword("CAST")) return ParseCastExpression();
            if (PeekKeyword("DATE") || PeekKeyword("TIME") || PeekKeyword("TIMESTAMP"))
                return ParseTypedLiteralExpression();

            if (TryConsumeChar('('))
            {
                var inner = ParseValueExpression();
                ExpectChar(')');
                return inner;
            }

            if (PeekChar('\'')) return new SqlWhereLiteralExpression(ParseStringLiteral());

            if (PeekHexBinaryLiteral())
                return new SqlWhereLiteralExpression(ParseHexBinaryLiteral());

            if (PeekKeyword("NULL")) { ExpectKeyword("NULL"); return new SqlWhereLiteralExpression(null); }
            if (PeekKeyword("TRUE")) { ExpectKeyword("TRUE"); return new SqlWhereLiteralExpression(true); }
            if (PeekKeyword("FALSE")) { ExpectKeyword("FALSE"); return new SqlWhereLiteralExpression(false); }

            if (PeekNumeric()) return new SqlWhereLiteralExpression(ParseNumberLiteral());

            if (PeekChar('@'))
                return ParseParameter();

            var identifier = ParseQualifiedIdentifier();
            SkipWhitespace();
            if (!IsEnd() && CurrentChar == '(')
            {
                // Function call: identifier(args)
                var functionName = identifier.ToUpperInvariant();
                ExpectChar('(');
                var args = new List<SqlWhereValueExpression> { ParseValueExpression() };
                while (TryConsumeChar(','))
                    args.Add(ParseValueExpression());
                ExpectChar(')');
                return new SqlWhereFunctionCallExpression(functionName, args);
            }
            var segments = identifier.Split('.');
            var simple = segments[^1];
            var expr = (SqlWhereValueExpression)new SqlWhereColumnExpression(identifier, simple);

            // JSON arrow operators: col->'$.path' or col->>'$.path'
            while (true)
            {
                SkipWhitespace();
                // Postgres path-array accessors: col#>'{a,b}' (jsonb) / col#>>'{a,b}' (text)
                if (!IsEnd() && CurrentChar == '#' && _position + 1 < _text.Length && _text[_position + 1] == '>')
                {
                    _position += 2; // consume #>
                    var unquotePath = !IsEnd() && CurrentChar == '>';
                    if (unquotePath) _position++; // consume second >
                    SkipWhitespace();
                    var pathArray = ParseStringLiteral();
                    expr = new SqlWhereJsonArrowExpression(expr, pathArray, unquotePath, SqlJsonPathKind.Path);
                    continue;
                }
                if (!IsEnd() && CurrentChar == '-' && _position + 1 < _text.Length && _text[_position + 1] == '>')
                {
                    // Check for ->> (unquote) vs -> (raw json)
                    _position += 2; // consume ->
                    var unquote = !IsEnd() && CurrentChar == '>';
                    if (unquote) _position++; // consume second >
                    SkipWhitespace();
                    // Operand is a quoted key/$.path ('key', '$.a.b') or an integer array index (2).
                    string member;
                    if (!IsEnd() && CurrentChar == '\'')
                    {
                        member = ParseStringLiteral();
                    }
                    else
                    {
                        var idxStart = _position;
                        while (!IsEnd() && char.IsDigit(CurrentChar)) _position++;
                        if (_position == idxStart)
                            throw new NotSupportedException("Expected string key or integer index after JSON '->' operator.");
                        member = _text[idxStart.._position];
                    }
                    expr = new SqlWhereJsonArrowExpression(expr, member, unquote, SqlJsonPathKind.Member);
                    continue;
                }
                return expr;
            }
        }

        private SqlWhereValueExpression ParseCaseExpression()
        {
            var start = _position;
            ExpectKeyword("CASE");
            var depth = 1;

            while (!IsEnd() && depth > 0)
            {
                if (PeekKeyword("CASE")) { _position += 4; depth++; continue; }
                if (PeekKeyword("END")) { _position += 3; depth--; continue; }
                if (CurrentChar == '\'') { ParseStringLiteral(); continue; }
                _position++;
            }

            if (depth != 0) throw new NotSupportedException("Unterminated CASE expression.");
            return new SqlWhereCaseExpression(_text[start.._position].Trim());
        }

        private SqlWhereValueExpression ParseCoalesceExpression()
        {
            ExpectKeyword("COALESCE");
            ExpectChar('(');
            var args = new List<string> { ParseCoalesceArgument() };
            while (TryConsumeChar(','))
                args.Add(ParseCoalesceArgument());
            ExpectChar(')');

            // Desugar COALESCE(a, b, c) → CASE WHEN a IS NOT NULL THEN a WHEN b IS NOT NULL THEN b ELSE c END
            var whenClauses = new System.Text.StringBuilder();
            for (int i = 0; i < args.Count - 1; i++)
                whenClauses.Append($"WHEN {args[i]} IS NOT NULL THEN {args[i]} ");
            var result = $"CASE {whenClauses}ELSE {args[^1]} END";
            return new SqlWhereCaseExpression(result);
        }

        private string ParseCoalesceArgument()
        {
            SkipWhitespace();
            var depth = 0;
            var start = _position;
            while (!IsEnd())
            {
                var c = CurrentChar;
                if (c == '\'') { ParseStringLiteral(); continue; }
                if (c == '(') { depth++; _position++; continue; }
                if (c == ')')
                {
                    if (depth == 0) break;
                    depth--;
                    _position++;
                    continue;
                }
                if (c == ',' && depth == 0) break;
                _position++;
            }
            return _text[start.._position].Trim();
        }

        private SqlWhereValueExpression ParseCastExpression()
        {
            ExpectKeyword("CAST");
            ExpectChar('(');
            var inner = ParseValueExpression();
            ExpectKeyword("AS");
            var typeName = ParseTypeName();
            ExpectChar(')');
            var targetType = ResolveCastTargetType(typeName);
            return new SqlWhereCastExpression(inner, targetType);
        }

        private static SqlScalarType ResolveCastTargetType(string typeName)
        {
            return typeName.ToUpperInvariant() switch
            {
                "INT" or "INTEGER" or "INT32" => SqlScalarType.Int32,
                "BIGINT" or "INT64" or "LONG" => SqlScalarType.Int64,
                "SMALLINT" or "INT16" => SqlScalarType.Int16,
                "FLOAT" or "REAL" or "DOUBLE" => SqlScalarType.Double,
                "DECIMAL" or "NUMERIC" => SqlScalarType.Decimal,
                "VARCHAR" or "NVARCHAR" or "TEXT" or "STRING" or "CHAR" or "NCHAR" => SqlScalarType.String,
                "BIT" or "BOOL" or "BOOLEAN" => SqlScalarType.Boolean,
                "DATETIME" or "TIMESTAMP" => SqlScalarType.DateTime,
                "DATE" => SqlScalarType.Date,
                "TIME" => SqlScalarType.Time,
                "GUID" or "UUID" or "UNIQUEIDENTIFIER" => SqlScalarType.Guid,
                "BINARY" or "BLOB" => SqlScalarType.Binary,
                "JSON" => SqlScalarType.Json,
                _ => SqlScalarType.String
            };
        }

        private SqlWhereValueExpression ParseTypedLiteralExpression()
        {
            SqlScalarType literalType;
            if (PeekKeyword("TIMESTAMP")) { ExpectKeyword("TIMESTAMP"); literalType = SqlScalarType.DateTime; }
            else if (PeekKeyword("DATE")) { ExpectKeyword("DATE"); literalType = SqlScalarType.Date; }
            else { ExpectKeyword("TIME"); literalType = SqlScalarType.Time; }

            SkipWhitespace();
            var strVal = ParseStringLiteral();
            object? typedVal = literalType switch
            {
                SqlScalarType.Date => DateTime.Parse(strVal, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).Date,
                SqlScalarType.Time => TimeSpan.Parse(strVal, CultureInfo.InvariantCulture),
                _ => DateTime.Parse(strVal, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            };
            return new SqlWhereLiteralExpression(typedVal);
        }

        private string ParseTypeName()
        {
            var start = _position;
            var id = ParseQualifiedIdentifier();
            SkipWhitespace();
            if (!TryConsumeChar('('))
                return id;

            var depth = 1;
            while (!IsEnd() && depth > 0)
            {
                if (CurrentChar == '(') { depth++; _position++; continue; }
                if (CurrentChar == ')') { depth--; _position++; continue; }
                _position++;
            }

            if (depth != 0) throw new NotSupportedException("Unterminated CAST type specification.");
            return id;
        }

        private string ParseQualifiedIdentifier()
        {
            var segments = new List<string> { ParseIdentifierSegment() };
            while (true)
            {
                SkipWhitespace();
                if (!TryConsumeChar('.')) break;
                segments.Add(ParseIdentifierSegment());
            }
            return string.Join('.', segments);
        }

        private SqlWhereParameterExpression ParseParameter()
        {
            ExpectChar('@');
            var name = "@" + ParseIdentifierSegment();

            if (!_parameters.TryGetValue(name, out var idx))
            {
                idx = _parameterNames.Count;
                _parameters[name] = idx;
                _parameterNames.Add(name);
            }

            return new SqlWhereParameterExpression(name, idx);
        }

        private string ParseIdentifierSegment()
        {
            SkipWhitespace();
            if (IsEnd()) throw new NotSupportedException("Expected identifier but reached end.");

            if (PeekChar('"') || PeekChar('[') || PeekChar('`'))
                return ParseQuotedIdentifier();

            if (!IsIdentifierStart(CurrentChar))
                throw new NotSupportedException($"Expected identifier near '{_text[_position..]}'.");

            var start = _position;
            _position++;
            while (!IsEnd() && IsIdentifierPart(CurrentChar))
                _position++;
            return _text[start.._position];
        }

        private string ParseQuotedIdentifier()
        {
            var startQuote = CurrentChar;
            var endQuote = startQuote == '[' ? ']' : startQuote;
            _position++;
            var start = _position;
            while (!IsEnd() && CurrentChar != endQuote)
                _position++;

            if (IsEnd()) throw new NotSupportedException("Unterminated quoted identifier.");
            var identifier = _text[start.._position];
            _position++;
            return identifier;
        }

        private string ParseStringLiteral()
        {
            // Manually consume opening quote — avoid TryConsumeChar which would
            // skip whitespace inside the literal via its post-consume SkipWhitespace.
            SkipWhitespace();
            if (IsEnd() || CurrentChar != '\'')
                throw new NotSupportedException("Expected '''.");
            _position++; // consume the opening quote
            var result = new StringBuilder();
            while (!IsEnd())
            {
                if (CurrentChar == '\'')
                {
                    _position++;
                    if (!IsEnd() && CurrentChar == '\'')
                    {
                        result.Append('\'');
                        _position++;
                        continue;
                    }
                    return result.ToString();
                }
                result.Append(CurrentChar);
                _position++;
            }
            throw new NotSupportedException("Unterminated string literal.");
        }

        private bool PeekHexBinaryLiteral()
        {
            SkipWhitespace();
            return !IsEnd()
                && (CurrentChar == 'X' || CurrentChar == 'x')
                && _position + 1 < _text.Length
                && _text[_position + 1] == '\'';
        }

        private byte[] ParseHexBinaryLiteral()
        {
            if (!PeekHexBinaryLiteral())
                throw new NotSupportedException($"Expected hex binary literal near '{_text[_position..]}'.");
            _position++;
            var hex = ParseStringLiteral();
            if ((hex.Length & 1) != 0)
                throw new NotSupportedException("Hex binary literal must have an even number of digits.");
            return Convert.FromHexString(hex);
        }

        private object ParseNumberLiteral()
        {
            SkipWhitespace();
            var start = _position;
            if (CurrentChar == '-') _position++;

            while (!IsEnd() && (char.IsDigit(CurrentChar) || CurrentChar is '.' or 'e' or 'E' or '+' or '-'))
            {
                if ((CurrentChar == '+' || CurrentChar == '-') && _position > start && _text[_position - 1] is not ('e' or 'E'))
                    break;
                _position++;
            }

            var token = _text[start.._position];
            if (!token.Contains('.') && !token.Contains('e', StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLong))
                {
                    if (parsedLong >= int.MinValue && parsedLong <= int.MaxValue)
                        return (int)parsedLong;
                    return parsedLong;
                }
            }

            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDouble))
                return parsedDouble;

            throw new NotSupportedException($"Number literal '{token}' is not supported.");
        }

        private string ParseSubqueryParenthesized()
        {
            SkipWhitespace();
            var openIndex = _position;
            ExpectChar('(');
            var contentStart = _position;
            var depth = 1;
            var inString = false;

            while (_position < _text.Length)
            {
                var current = _text[_position];
                if (current == '\'' && (_position == 0 || _text[_position - 1] != '\\'))
                {
                    if (_position + 1 < _text.Length && _text[_position + 1] == '\'')
                    {
                        _position += 2;
                        continue;
                    }
                    inString = !inString;
                    _position++;
                    continue;
                }

                if (inString) { _position++; continue; }

                if (current == '(') { depth++; _position++; continue; }
                if (current == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        var sql = _text[contentStart.._position].Trim();
                        _position++;
                        if (string.IsNullOrWhiteSpace(sql))
                            throw new NotSupportedException("Subquery must not be empty.");
                        return sql;
                    }
                    _position++;
                    continue;
                }
                _position++;
            }

            throw new NotSupportedException($"Unterminated subquery starting near '{_text[openIndex..]}'.");
        }

        private bool LooksLikeSubqueryParenthesized()
        {
            var snapshot = _position;
            SkipWhitespace();
            if (!PeekChar('(')) { _position = snapshot; return false; }

            _position++;
            SkipWhitespace();
            var looksLikeSubquery = PeekKeyword("SELECT") || PeekKeyword("WITH");
            _position = snapshot;
            return looksLikeSubquery;
        }

        private bool TryConsumeComparison(out SqlWhereComparisonOperator op)
        {
            SkipWhitespace();
            if (TryConsumeText(">=")) { op = SqlWhereComparisonOperator.GreaterThanOrEqual; return true; }
            if (TryConsumeText("<=")) { op = SqlWhereComparisonOperator.LessThanOrEqual; return true; }
            if (TryConsumeText("<>")) { op = SqlWhereComparisonOperator.NotEqual; return true; }
            if (TryConsumeText("!=")) { op = SqlWhereComparisonOperator.NotEqual; return true; }
            if (TryConsumeText("=")) { op = SqlWhereComparisonOperator.Equal; return true; }
            if (TryConsumeText(">")) { op = SqlWhereComparisonOperator.GreaterThan; return true; }
            if (TryConsumeText("<")) { op = SqlWhereComparisonOperator.LessThan; return true; }

            op = default;
            return false;
        }

        private bool LooksLikeComparisonOperator()
        {
            var snapshot = _position;
            var result = TryConsumeComparison(out _);
            _position = snapshot;
            return result;
        }

        private bool TryConsumeKeyword(string keyword)
        {
            SkipWhitespace();
            if (!PeekKeyword(keyword)) return false;
            _position += keyword.Length;
            SkipWhitespace();
            return true;
        }

        private bool TryConsumeBitwiseOr()
        {
            SkipWhitespace();
            if (IsEnd() || CurrentChar != '|' || (_position + 1 < _text.Length && _text[_position + 1] == '|'))
                return false;
            _position++;
            SkipWhitespace();
            return true;
        }

        private bool TryConsumeBitwiseAnd()
        {
            SkipWhitespace();
            if (IsEnd() || CurrentChar != '&' || (_position + 1 < _text.Length && _text[_position + 1] == '&'))
                return false;
            _position++;
            SkipWhitespace();
            return true;
        }

        private bool PeekKeyword(string keyword)
        {
            SkipWhitespace();
            if (_position + keyword.Length > _text.Length) return false;
            if (!_text.AsSpan(_position, keyword.Length).Equals(keyword.AsSpan(), StringComparison.OrdinalIgnoreCase))
                return false;

            var beforeOk = _position == 0 || !IsIdentifierPart(_text[_position - 1]);
            var end = _position + keyword.Length;
            var afterOk = end >= _text.Length || !IsIdentifierPart(_text[end]);
            return beforeOk && afterOk;
        }

        private void ExpectKeyword(string keyword)
        {
            if (!TryConsumeKeyword(keyword))
                throw new NotSupportedException($"Expected keyword '{keyword}' near '{_text[_position..]}'.");
        }

        private bool TryConsumeChar(char value)
        {
            SkipWhitespace();
            if (IsEnd() || CurrentChar != value) return false;
            _position++;
            SkipWhitespace();
            return true;
        }

        private bool PeekChar(char value)
        {
            SkipWhitespace();
            return !IsEnd() && CurrentChar == value;
        }

        private void ExpectChar(char value)
        {
            if (!TryConsumeChar(value))
                throw new NotSupportedException($"Expected '{value}' near '{_text[_position..]}'.");
        }

        private bool TryConsumeText(string text)
        {
            SkipWhitespace();
            if (_position + text.Length > _text.Length) return false;
            if (!_text.AsSpan(_position, text.Length).Equals(text.AsSpan(), StringComparison.Ordinal)) return false;

            _position += text.Length;
            SkipWhitespace();
            return true;
        }

        private bool PeekNumeric()
        {
            SkipWhitespace();
            return !IsEnd() && (char.IsDigit(CurrentChar) || (CurrentChar == '-' && _position + 1 < _text.Length && char.IsDigit(_text[_position + 1])));
        }

        private void SkipWhitespace()
        {
            while (!IsEnd() && char.IsWhiteSpace(CurrentChar))
                _position++;
        }

        private bool IsEnd() => _position >= _text.Length;
        private char CurrentChar => _text[_position];

        private static bool IsIdentifierStart(char value)
            => char.IsLetter(value) || value is '_' or '"' or '[' or '`';

        private static bool IsIdentifierPart(char value)
            => char.IsLetterOrDigit(value) || value is '_' or '$';
    }
}
