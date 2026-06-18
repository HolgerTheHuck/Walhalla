using System;

namespace WalhallaSql.AdoNet;

/// <summary>
/// Describes the physical storage of a WalhallaSql database.
/// </summary>
public sealed record WalhallaSqlDatabaseInfo(
    string Provider,
    string DatabaseName,
    string Location,
    bool IsDirectory,
    long Size,
    DateTime? CreatedAt);
