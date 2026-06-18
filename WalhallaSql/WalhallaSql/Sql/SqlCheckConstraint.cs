using System;

namespace WalhallaSql.Sql;

/// <summary>
/// A named CHECK constraint attached to a table. The <see cref="Expression"/> holds the raw
/// boolean predicate text (e.g. <c>age &gt;= 0</c>) which is parsed and evaluated with
/// SQL three-valued logic: a row violates the constraint only when the predicate evaluates to
/// <c>FALSE</c>; <c>TRUE</c> and <c>UNKNOWN</c> (NULL) both satisfy it.
/// </summary>
public sealed record SqlCheckConstraint(string Name, string Expression);
