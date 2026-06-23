using System.Collections.Generic;

namespace WalhallaSql.Parsing.Plw;

/// <summary>
/// Abstrakter Basisknoten für den PLW-AST.
/// </summary>
internal abstract record PlwNode;

/// <summary>
/// Deklarationsblock + ausführbarer Body.
/// </summary>
internal sealed record PlwBlock(
    IReadOnlyList<PlwVariableDeclaration> Declarations,
    IReadOnlyList<PlwNode> Body) : PlwNode;

/// <summary>
/// Variablen-/Parameter-Deklaration.
/// </summary>
internal sealed record PlwVariableDeclaration(
    string Name,
    string TypeName,
    PlwExpression? DefaultValue = null,
    bool IsParameter = false) : PlwNode;

/// <summary>
/// Zuweisung: target := value
/// </summary>
internal sealed record PlwAssignment(
    PlwExpression Target,
    PlwExpression Value) : PlwNode;

/// <summary>
/// IF ... THEN ... [ELSIF ... THEN ...] [ELSE ...] END IF
/// </summary>
internal sealed record PlwIf(
    PlwExpression Condition,
    PlwNode ThenBranch,
    IReadOnlyList<PlwElsif> ElsifBranches,
    PlwNode? ElseBranch) : PlwNode;

internal sealed record PlwElsif(
    PlwExpression Condition,
    PlwNode ThenBranch);

/// <summary>
/// LOOP ... END LOOP
/// </summary>
internal sealed record PlwSimpleLoop(
    PlwNode Body) : PlwNode;

/// <summary>
/// WHILE condition LOOP ... END LOOP
/// </summary>
internal sealed record PlwWhileLoop(
    PlwExpression Condition,
    PlwNode Body) : PlwNode;

/// <summary>
/// FOR var IN [REVERSE] lower..upper LOOP ... END LOOP
/// </summary>
internal sealed record PlwForIntegerLoop(
    string VariableName,
    PlwExpression Lower,
    PlwExpression Upper,
    PlwNode Body,
    bool Reverse = false) : PlwNode;

/// <summary>
/// FOR rec IN query LOOP ... END LOOP
/// </summary>
internal sealed record PlwForQueryLoop(
    string VariableName,
    PlwSqlFragment Query,
    PlwNode Body) : PlwNode;

/// <summary>
/// EXIT [label] [WHEN condition]
/// </summary>
internal sealed record PlwExit(
    string? Label,
    PlwExpression? WhenCondition) : PlwNode;

/// <summary>
/// CONTINUE [label] [WHEN condition]
/// </summary>
internal sealed record PlwContinue(
    string? Label,
    PlwExpression? WhenCondition) : PlwNode;

/// <summary>
/// RETURN [expression]
/// </summary>
internal sealed record PlwReturn(
    PlwExpression? Value) : PlwNode;

/// <summary>
/// RETURN QUERY sql
/// </summary>
internal sealed record PlwReturnQuery(
    PlwSqlFragment Query) : PlwNode;

/// <summary>
/// PERFORM sql;
/// </summary>
internal sealed record PlwPerform(
    PlwSqlFragment Statement) : PlwNode;

/// <summary>
/// SELECT ... INTO var1, var2 ...
/// </summary>
internal sealed record PlwSelectInto(
    PlwSqlFragment SelectSql,
    IReadOnlyList<PlwExpression> Targets) : PlwNode;

/// <summary>
/// EXECUTE sql_string [USING expr1, expr2 ...]
/// </summary>
internal sealed record PlwExecute(
    PlwExpression SqlExpression,
    IReadOnlyList<PlwExpression> UsingArguments) : PlwNode;

/// <summary>
/// RAISE level format [, args];
/// Level ist ein String-Literal oder Identifier.
/// </summary>
internal sealed record PlwRaise(
    string Level,
    PlwExpression? Message,
    IReadOnlyList<PlwExpression> Arguments) : PlwNode;

/// <summary>
/// Beliebige eingebettete SQL-Anweisung, die nicht direkt verarbeitet wird.
/// Wird vom Interpreter später an den SQL-Parser delegiert.
/// </summary>
internal sealed record PlwSqlStatement(
    PlwSqlFragment Sql) : PlwNode;

/// <summary>
/// Ausdrucksknoten.
/// </summary>
internal abstract record PlwExpression : PlwNode;

internal sealed record PlwIdentifierExpression(string Name) : PlwExpression;
internal sealed record PlwNumberExpression(string Text) : PlwExpression;
internal sealed record PlwStringExpression(string Value) : PlwExpression;
internal sealed record PlwBooleanExpression(bool Value) : PlwExpression;
internal sealed record PlwNullExpression : PlwExpression;
internal sealed record PlwBinaryExpression(PlwExpression Left, PlwTokenKind Operator, PlwExpression Right) : PlwExpression;
internal sealed record PlwUnaryExpression(PlwTokenKind Operator, PlwExpression Operand) : PlwExpression;
internal sealed record PlwParameterReference(int Index) : PlwExpression;

/// <summary>
/// Roh-SQL-Fragment mit Ersetzungsplatzhaltern.
/// Der Text enthält die SQL-Anweisung, und Arguments markiert Stellen,
/// an denen PLW-Ausdrücke eingesetzt werden (z. B. {expr}).
/// </summary>
internal sealed record PlwSqlFragment(
    string Text,
    IReadOnlyList<PlwExpression> Arguments,
    bool IsDollarQuoted = false,
    string? DollarTag = null) : PlwExpression;

/// <summary>
/// Gesamtes geparstes PLW-Programm.
/// </summary>
internal sealed record PlwProgram(
    IReadOnlyList<PlwVariableDeclaration> Parameters,
    PlwBlock Body);
