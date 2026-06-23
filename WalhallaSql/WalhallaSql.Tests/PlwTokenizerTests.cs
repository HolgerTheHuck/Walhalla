using System.Linq;
using WalhallaSql.Parsing.Plw;
using Xunit;

namespace WalhallaSql.Tests;

/// <summary>
/// Tests für den PLW-Tokenizer (Phase 2).
/// </summary>
public sealed class PlwTokenizerTests
{
    [Fact]
    public void Tokenize_SimpleBlock_RecognizesKeywords()
    {
        var source = """
            BEGIN
                x := 1;
            END;
            """;

        var tokens = PlwTokenizer.Tokenize(source);

        Assert.Equal(PlwTokenKind.Begin, tokens[0].Kind);
        Assert.Equal("BEGIN", tokens[0].Text);
        Assert.Equal(PlwTokenKind.Identifier, tokens[1].Kind);
        Assert.Equal("x", tokens[1].Text);
        Assert.Equal(PlwTokenKind.ColonEquals, tokens[2].Kind);
        Assert.Equal(PlwTokenKind.Number, tokens[3].Kind);
        Assert.Equal("1", tokens[3].Text);
        Assert.Equal(PlwTokenKind.Semicolon, tokens[4].Kind);
        Assert.Equal(PlwTokenKind.End, tokens[5].Kind);
        Assert.Equal("END", tokens[5].Text);
        Assert.Equal(PlwTokenKind.Semicolon, tokens[6].Kind);
        Assert.Equal(PlwTokenKind.Eof, tokens[^1].Kind);
    }

    [Fact]
    public void Tokenize_Keywords_AreCaseInsensitive()
    {
        var tokens = PlwTokenizer.Tokenize("If a > 0 Then b := 1; ElsE b := 0;");
        var kinds = tokens.Select(t => t.Kind).ToArray();

        Assert.Equal(new[]
        {
            PlwTokenKind.If, PlwTokenKind.Identifier, PlwTokenKind.GreaterThan,
            PlwTokenKind.Number, PlwTokenKind.Then, PlwTokenKind.Identifier,
            PlwTokenKind.ColonEquals, PlwTokenKind.Number, PlwTokenKind.Semicolon,
            PlwTokenKind.Else, PlwTokenKind.Identifier, PlwTokenKind.ColonEquals,
            PlwTokenKind.Number, PlwTokenKind.Semicolon, PlwTokenKind.Eof
        }, kinds);
    }

    [Fact]
    public void Tokenize_Comments_AreSkipped()
    {
        var source = """
            -- line comment
            /* block
               comment */
            x := 42;
            """;

        var tokens = PlwTokenizer.Tokenize(source);

        Assert.Equal(4, tokens.Count - 1); // ohne Eof
        Assert.Equal(PlwTokenKind.Identifier, tokens[0].Kind);
        Assert.Equal(PlwTokenKind.ColonEquals, tokens[1].Kind);
        Assert.Equal(PlwTokenKind.Number, tokens[2].Kind);
        Assert.Equal(PlwTokenKind.Semicolon, tokens[3].Kind);
    }

    [Fact]
    public void Tokenize_StringLiteral_HandlesEscapes()
    {
        var tokens = PlwTokenizer.Tokenize("'hello''world'");

        Assert.Equal(2, tokens.Count);
        Assert.Equal(PlwTokenKind.String, tokens[0].Kind);
        Assert.Equal("hello'world", tokens[0].Text);
        Assert.Equal(PlwTokenKind.Eof, tokens[1].Kind);
    }

    [Fact]
    public void Tokenize_DollarQuotedString_IsRecognized()
    {
        var tokens = PlwTokenizer.Tokenize("$x$ some body $x$");

        Assert.Equal(2, tokens.Count);
        Assert.Equal(PlwTokenKind.DollarString, tokens[0].Kind);
        Assert.Equal(" some body ", tokens[0].Text);
        Assert.Equal("x", tokens[0].DollarTag);
        Assert.Equal(PlwTokenKind.Eof, tokens[1].Kind);
    }

    [Fact]
    public void Tokenize_DollarQuotedString_EmptyTag_IsRecognized()
    {
        var tokens = PlwTokenizer.Tokenize("$$literal$$");

        Assert.Equal(PlwTokenKind.DollarString, tokens[0].Kind);
        Assert.Equal("literal", tokens[0].Text);
        Assert.Equal(string.Empty, tokens[0].DollarTag);
    }

    [Fact]
    public void Tokenize_Operators_TwoCharOperators()
    {
        var source = "a := b || c; d <= e; f >= g; h <> i; j != k;";
        var tokens = PlwTokenizer.Tokenize(source);
        var kinds = tokens.Select(t => t.Kind).Where(k => k != PlwTokenKind.Identifier && k != PlwTokenKind.Eof).ToArray();

        Assert.Equal(new[]
        {
            PlwTokenKind.ColonEquals, PlwTokenKind.Concat,
            PlwTokenKind.Semicolon, PlwTokenKind.LessEquals,
            PlwTokenKind.Semicolon, PlwTokenKind.GreaterEquals,
            PlwTokenKind.Semicolon, PlwTokenKind.NotEquals,
            PlwTokenKind.Semicolon, PlwTokenKind.NotEquals,
            PlwTokenKind.Semicolon
        }, kinds);
    }

    [Fact]
    public void Tokenize_Number_Decimal()
    {
        var tokens = PlwTokenizer.Tokenize("3.14");

        Assert.Equal(PlwTokenKind.Number, tokens[0].Kind);
        Assert.Equal("3.14", tokens[0].Text);
    }

    [Fact]
    public void Tokenize_ProcedureLikeBody_RecognizesStructure()
    {
        var source = """
            DECLARE
                x INT := 0;
                rec RECORD;
            BEGIN
                IF x > 0 THEN
                    RETURN;
                ELSIF x = 0 THEN
                    RETURN QUERY SELECT Id FROM T;
                ELSE
                    RAISE NOTICE 'value is negative';
                END IF;

                FOR rec IN SELECT Id FROM T LOOP
                    x := x + 1;
                END LOOP;
            END;
            """;

        var tokens = PlwTokenizer.Tokenize(source);
        var kinds = tokens.Select(t => t.Kind).ToArray();

        Assert.Contains(PlwTokenKind.Declare, kinds);
        Assert.Contains(PlwTokenKind.Begin, kinds);
        Assert.Contains(PlwTokenKind.End, kinds);
        Assert.Contains(PlwTokenKind.If, kinds);
        Assert.Contains(PlwTokenKind.Then, kinds);
        Assert.Contains(PlwTokenKind.Elsif, kinds);
        Assert.Contains(PlwTokenKind.Else, kinds);
        Assert.Contains(PlwTokenKind.Return, kinds);
        Assert.Contains(PlwTokenKind.Query, kinds);
        Assert.Contains(PlwTokenKind.Raise, kinds);
        Assert.Contains(PlwTokenKind.Notice, kinds);
        Assert.Contains(PlwTokenKind.For, kinds);
        Assert.Contains(PlwTokenKind.In, kinds);
        Assert.Contains(PlwTokenKind.Loop, kinds);
    }

    [Fact]
    public void Tokenize_Positions_AreTracked()
    {
        var source = "BEGIN\n  x := 1;\nEND";
        var tokens = PlwTokenizer.Tokenize(source);

        var begin = tokens[0];
        var x = tokens[1];
        var end = tokens[^2];

        Assert.Equal(1, begin.Line);
        Assert.Equal(1, begin.Column);
        Assert.Equal(2, x.Line);
        Assert.Equal(3, x.Column);
        Assert.Equal(3, end.Line);
        Assert.Equal(1, end.Column);
    }

    [Fact]
    public void Tokenize_Perform_Keyword()
    {
        var tokens = PlwTokenizer.Tokenize("PERFORM some_proc();");

        Assert.Equal(PlwTokenKind.Perform, tokens[0].Kind);
    }

    [Fact]
    public void Tokenize_ExecuteUsing_Keywords()
    {
        var tokens = PlwTokenizer.Tokenize("EXECUTE 'SELECT ...' USING a, b;");

        Assert.Equal(PlwTokenKind.Execute, tokens[0].Kind);
        Assert.Equal(PlwTokenKind.String, tokens[1].Kind);
        Assert.Equal(PlwTokenKind.Using, tokens[2].Kind);
    }
}
