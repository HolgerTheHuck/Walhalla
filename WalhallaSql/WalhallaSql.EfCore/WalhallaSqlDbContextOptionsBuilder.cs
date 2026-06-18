using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace WalhallaSql.EfCore;

public sealed class WalhallaSqlDbContextOptionsBuilder(DbContextOptionsBuilder optionsBuilder)
    : RelationalDbContextOptionsBuilder<WalhallaSqlDbContextOptionsBuilder, WalhallaSqlDbContextOptionsExtension>(optionsBuilder);
