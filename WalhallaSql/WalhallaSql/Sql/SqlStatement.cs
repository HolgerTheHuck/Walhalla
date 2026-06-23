using System.Collections.Generic;
using WalhallaSql.Catalog;

namespace WalhallaSql.Sql;

public abstract record SqlStatement;

public enum SqlJoinKind { Inner, Left, Right, Cross }

public sealed record SqlOrderByColumn(string ColumnName, bool Descending = false, string? Collation = null);

public sealed record SqlJoinClause(
    SqlJoinKind Kind,
    string TableName,
    string? Alias,
    SqlWhereExpression? OnPredicate,
    SqlSelectStatement? DerivedTable = null);

public enum SqlAggregateFunction { Count, Sum, Avg, Min, Max }

public sealed record SqlSelectStatement(
    string TableName,
    string? TableAlias,
    IReadOnlyList<SqlSelectColumn> Columns,
    SqlWhereExpression? Where,
    IReadOnlyList<string>? Parameters = null,
    IReadOnlyList<SqlJoinClause>? Joins = null,
    IReadOnlyList<string>? GroupByColumns = null,
    SqlWhereExpression? Having = null,
    IReadOnlyList<SqlOrderByColumn>? OrderBy = null,
    int? Limit = null,
    int? Offset = null,
    bool IsDistinct = false,
    SqlSelectStatement? DerivedTable = null) : SqlStatement;

public sealed record SqlSelectColumn(
    string Expression,
    string? Alias,
    SqlAggregateCall? Aggregate = null,
    SqlWindowCall? WindowFunction = null);

public enum SqlWindowFunctionType { RowNumber, Rank, DenseRank, NTile, PercentRank, CumeDist, Aggregate, Lag, Lead, FirstValue, LastValue, NthValue }

public enum SqlWindowFrameMode { Rows, Range, Groups }

public enum SqlWindowFrameBoundType
{
    UnboundedPreceding,
    Preceding,
    CurrentRow,
    Following,
    UnboundedFollowing
}

public sealed record SqlWindowFrameBound(
    SqlWindowFrameBoundType BoundType,
    int? Offset = null);

public sealed record SqlWindowFrame(
    SqlWindowFrameMode Mode,
    SqlWindowFrameBound Start,
    SqlWindowFrameBound End);

public sealed record SqlWindowCall(
    SqlWindowFunctionType Function,
    IReadOnlyList<string>? PartitionBy,
    IReadOnlyList<SqlOrderByColumn>? OrderBy,
    SqlWindowFrame? Frame = null,
    int? NTileBuckets = null,
    SqlAggregateFunction? AggregateFunction = null,
    string? AggregateArgument = null,
    string? OffsetColumn = null,
    int? OffsetAmount = null,
    string? OffsetDefault = null,
    bool IgnoreNulls = false);

public sealed record SqlAggregateCall(
    SqlAggregateFunction Function,
    string? Argument);   // null for COUNT(*)

public sealed record SqlInsertStatement(
    string TableName,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> ValueRows,
    SqlOnConflictClause? OnConflict = null) : SqlStatement;

public sealed record SqlCteDefinition(string Name, SqlStatement Body);

public sealed record SqlWithStatement(
    IReadOnlyList<SqlCteDefinition> Ctes,
    SqlStatement MainStatement,
    bool IsRecursive = false) : SqlStatement;

public sealed record SqlInsertSelectStatement(
    string TableName,
    IReadOnlyList<string> Columns,
    SqlSelectStatement SelectStatement,
    SqlOnConflictClause? OnConflict = null) : SqlStatement;

public sealed record SqlCreateTableStatement(
    SqlTableDefinition Definition) : SqlStatement;

public sealed record SqlDropTableStatement(
    string TableName) : SqlStatement;

public sealed record SqlUpdateStatement(
    string TableName,
    IReadOnlyDictionary<string, string> Assignments,
    SqlWhereExpression? Where,
    IReadOnlyList<string>? Parameters = null) : SqlStatement;

public sealed record SqlDeleteStatement(
    string TableName,
    SqlWhereExpression? Where,
    IReadOnlyList<string>? Parameters = null) : SqlStatement;

public enum SqlSetOperator { Union, UnionAll, Except, Intersect }

public sealed record SqlCompoundSelectStatement(
    SqlSelectStatement Left,
    SqlSetOperator Operator,
    SqlSelectStatement Right) : SqlStatement;

public sealed record SqlCreateIndexStatement(
    string IndexName,
    string TableName,
    IReadOnlyList<string> ColumnNames,
    bool IsUnique,
    SqlIndexType IndexType = SqlIndexType.BTree) : SqlStatement;

public sealed record SqlDropIndexStatement(
    string IndexName,
    string TableName) : SqlStatement;

public enum SqlAlterActionType { AddColumn, DropColumn, AlterColumn, RenameColumn, RenameTable, AddConstraint, DropConstraint }

public sealed record SqlAlterTableStatement(
    string TableName,
    SqlAlterActionType Action,
    string? ColumnName = null,
    SqlScalarType? NewType = null,
    object? DefaultValue = null,
    bool? NotNull = null,
    string? NewColumnName = null,
    string? NewTableName = null,
    string? ConstraintName = null,
    string? CheckExpression = null,
    string? Collation = null,
    SqlForeignKeyDefinition? ForeignKey = null) : SqlStatement;

public sealed record SqlCreateViewStatement(
    string ViewName,
    SqlSelectStatement SelectStatement) : SqlStatement;

public sealed record SqlDropViewStatement(
    string ViewName) : SqlStatement;

// ── Stored Procedures & Triggers ──────────────────────────────────────────────

public enum SqlTriggerEvent { Insert, Update, Delete }

public enum SqlTriggerTiming { Before, After, InsteadOf }

public enum SqlParameterDirection { In, Out, InOut }

public sealed record SqlProcedureParameter(
    string Name,
    SqlScalarType Type,
    bool IsOutput = false,
    bool IsNullable = true,
    object? DefaultValue = null)
{
    public SqlParameterDirection Direction { get; init; } = IsOutput ? SqlParameterDirection.Out : SqlParameterDirection.In;
}

public sealed record SqlStoredProcedureDefinition(
    string Name,
    IReadOnlyList<SqlProcedureParameter> Parameters,
    string Body,
    string Language = "sql",
    string? Description = null);

public sealed record SqlTriggerDefinition(
    string Name,
    string TableName,
    SqlTriggerEvent Event,
    SqlTriggerTiming Timing = SqlTriggerTiming.After,
    string Body = "",
    string? Description = null);

public sealed record SqlCreateProcedureStatement(
    string ProcedureName,
    IReadOnlyList<SqlProcedureParameter> Parameters,
    string Body,
    bool OrReplace = false,
    string Language = "sql") : SqlStatement;

public sealed record SqlDropProcedureStatement(
    string ProcedureName,
    bool IfExists = false) : SqlStatement;

public sealed record SqlExecStatement(
    string ProcedureName,
    IReadOnlyList<SqlExecArgument> Arguments) : SqlStatement;

public sealed record SqlExecArgument(
    string? ParameterName,
    string ValueExpression);

public sealed record SqlCreateTriggerStatement(
    string TriggerName,
    string TableName,
    SqlTriggerEvent Event,
    SqlTriggerTiming Timing = SqlTriggerTiming.After,
    string Body = "",
    bool OrReplace = false) : SqlStatement;

public sealed record SqlDropTriggerStatement(
    string TriggerName,
    bool IfExists = false) : SqlStatement;

public sealed record SqlExplainStatement(string SelectSql) : SqlStatement;

public sealed record SqlSavepointStatement(string Name) : SqlStatement;

public sealed record SqlRollbackToStatement(string Name) : SqlStatement;

public sealed record SqlReleaseSavepointStatement(string Name) : SqlStatement;

public sealed record SqlBeginTransactionStatement : SqlStatement;

public sealed record SqlCommitStatement : SqlStatement;

public sealed record SqlRollbackStatement : SqlStatement;

public sealed record SqlTruncateTableStatement(string TableName) : SqlStatement;

public sealed record SqlVacuumStatement(string? TableName) : SqlStatement;

public sealed record SqlAnalyzeStatement(string? TableName) : SqlStatement;

public sealed record SqlDescribeStatement(string TableName) : SqlStatement;

// ── Roles & Grants ───────────────────────────────────────────────────────────────

public sealed record SqlCreateRoleStatement(
    string RoleName,
    string Password,
    bool CanLogin,
    bool IsSuperuser) : SqlStatement;

public sealed record SqlAlterRoleStatement(
    string RoleName,
    string? NewPassword = null,
    bool? IsSuperuser = null) : SqlStatement;

public sealed record SqlDropRoleStatement(
    string RoleName,
    bool IfExists = false) : SqlStatement;

public sealed record SqlGrantStatement(
    IReadOnlyList<GrantPrivilege> Privileges,
    GrantObjectType ObjectType,
    string ObjectName,
    string Grantee) : SqlStatement;

public sealed record SqlRevokeStatement(
    IReadOnlyList<GrantPrivilege> Privileges,
    GrantObjectType ObjectType,
    string ObjectName,
    string Grantee) : SqlStatement;

public sealed record SqlSetTransactionStatement(string IsolationLevelName) : SqlStatement;

public sealed record SqlSetTransactionModeStatement(string ModeName) : SqlStatement;

public sealed record SqlMergeStatement(
    string TargetTable,
    string SourceTable,
    string SourceAlias,
    SqlWhereExpression OnPredicate,
    IReadOnlyDictionary<string, string>? UpdateAssignments,
    IReadOnlyList<string>? InsertColumns) : SqlStatement;

public sealed record SqlConflictTarget(
    IReadOnlyList<string>? ColumnNames,
    string? ConstraintName);

public enum SqlConflictAction { DoNothing, DoUpdate }

public sealed record SqlOnConflictClause(
    SqlConflictTarget? Target,
    SqlConflictAction Action,
    IReadOnlyDictionary<string, string>? UpdateAssignments,
    SqlWhereExpression? Where);

public enum SqlCopyDirection { FromStdin, ToStdout }
public enum SqlCopyFormat { Text, Csv, Binary }

public sealed record SqlCopyOptions(
    SqlCopyFormat Format = SqlCopyFormat.Text,
    string? Delimiter = null,
    string? NullMarker = null,
    bool Header = false,
    string? Quote = null,
    string? Escape = null);

public sealed record SqlCopyStatement(
    string TableName,
    SqlCopyDirection Direction,
    SqlCopyOptions Options,
    IReadOnlyList<string>? ColumnNames = null) : SqlStatement;

// ── Cursors ────────────────────────────────────────────────────────────────────────

public sealed record SqlDeclareCursorStatement(
    string CursorName,
    string SelectSql) : SqlStatement;

public sealed record SqlOpenCursorStatement(string CursorName) : SqlStatement;

public sealed record SqlFetchCursorStatement(string CursorName) : SqlStatement;

public sealed record SqlCloseCursorStatement(string CursorName) : SqlStatement;

public sealed record SqlDeallocateCursorStatement(string CursorName) : SqlStatement;
