using System;
using System.Collections.Generic;

namespace WalhallaSql.AdoNet.SqlClient;

public sealed record SqlClientCommand(
	string Sql,
	bool HasExternalTransaction = false,
	IReadOnlyList<SqlClientParameter>? Parameters = null,
	bool PreferTransportPrepare = false)
{
	public IReadOnlyList<SqlClientParameter> Parameters { get; init; } = Parameters ?? Array.Empty<SqlClientParameter>();
}
