using System;
using System.Linq;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;

var _ = typeof(RelationalQueryableMethodTranslatingExpressionVisitor);

var types = AppDomain.CurrentDomain.GetAssemblies()
    .Where(a => a.FullName?.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) == true)
    .SelectMany(a => a.GetTypes())
    .Where(t => t.Name.Contains("ConventionSetBuilder", StringComparison.Ordinal))
    .OrderBy(t => t.FullName);
foreach (var t in types)
{
    Console.WriteLine($"{t.FullName}");
    foreach (var iface in t.GetInterfaces())
    {
        Console.WriteLine($"  implements {iface.FullName}");
    }
}
