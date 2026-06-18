using System.Text;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;

namespace WalhallaSql.EfCore;

internal sealed class WalhallaSqlUpdateSqlGenerator : UpdateAndSelectSqlGenerator
{
    public WalhallaSqlUpdateSqlGenerator(UpdateSqlGeneratorDependencies dependencies)
        : base(dependencies)
    {
    }

    protected override void AppendRowsAffectedWhereCondition(StringBuilder commandStringBuilder, int expectedRowsAffected)
    {
        commandStringBuilder.Append("changes() = ").Append(expectedRowsAffected);
    }

    protected override void AppendIdentityWhereCondition(StringBuilder commandStringBuilder, IColumnModification columnModification)
    {
        SqlGenerationHelper.DelimitIdentifier(commandStringBuilder, columnModification.ColumnName);
        commandStringBuilder.Append(" = last_insert_rowid()");
    }

    protected override ResultSetMapping AppendSelectAffectedCountCommand(
        StringBuilder commandStringBuilder,
        string name,
        string? schema,
        int commandPosition)
    {
        commandStringBuilder
            .Append("SELECT changes()")
            .AppendLine(SqlGenerationHelper.StatementTerminator)
            .AppendLine();

        return ResultSetMapping.LastInResultSet;
    }
}
