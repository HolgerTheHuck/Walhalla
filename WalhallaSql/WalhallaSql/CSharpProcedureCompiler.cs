using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using WalhallaSql.Sql;

namespace WalhallaSql;

internal static class CSharpProcedureCompiler
{
    public static Func<SqlNativeProcedureContext, WalhallaResultSet> Compile(
        SqlStoredProcedureDefinition def)
    {
        var source = GenerateSource(def);

        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var references = CollectReferences();

        var compilation = CSharpCompilation.Create(
            assemblyName: $"__CsProc_{def.Name}_{Guid.NewGuid():N}",
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: false));

        using var ms = new MemoryStream();
        var emitResult = compilation.Emit(ms);

        if (!emitResult.Success)
        {
            var errors = string.Join(Environment.NewLine, emitResult.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString()));
            throw new InvalidOperationException(
                $"C# stored procedure '{def.Name}': compilation failed.{Environment.NewLine}{errors}{Environment.NewLine}Generated source:{Environment.NewLine}{source}");
        }

        ms.Seek(0, SeekOrigin.Begin);
        var assembly = Assembly.Load(ms.ToArray());
        var type = assembly.GetType("__CsProcedureHost")
            ?? throw new InvalidOperationException(
                $"C# stored procedure '{def.Name}': generated type '__CsProcedureHost' not found.");
        var method = type.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                $"C# stored procedure '{def.Name}': method 'Execute' not found.");

        return ctx => (WalhallaResultSet)method.Invoke(null, [ctx])!;
    }

    private static string GenerateSource(SqlStoredProcedureDefinition def)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine("using WalhallaSql;");
        sb.AppendLine("using WalhallaSql.Sql;");
        sb.AppendLine();
        sb.AppendLine("public static class __CsProcedureHost");
        sb.AppendLine("{");
        sb.AppendLine("    public static WalhallaSql.WalhallaResultSet Execute(WalhallaSql.SqlNativeProcedureContext ctx)");
        sb.AppendLine("    {");

        foreach (var p in def.Parameters)
        {
            var csType = MapSqlTypeToCSharp(p.Type, p.IsNullable);
            var varName = p.Name.TrimStart('@');
            sb.AppendLine($"        var {varName} = ctx.Get<{csType}>(\"{p.Name}\");");
        }

        if (def.Parameters.Count > 0)
            sb.AppendLine();

        sb.AppendLine("        // --- begin user code ---");
        sb.AppendLine(def.Body);
        sb.AppendLine("        // --- end user code ---");
        sb.AppendLine("#pragma warning disable CS0162");
        sb.AppendLine("        return WalhallaSql.WalhallaResultSet.Affected(0);");
        sb.AppendLine("#pragma warning restore CS0162");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string MapSqlTypeToCSharp(SqlScalarType type, bool nullable) => type switch
    {
        SqlScalarType.Int32    => nullable ? "int?"     : "int",
        SqlScalarType.Int64    => nullable ? "long?"    : "long",
        SqlScalarType.Double   => nullable ? "double?"  : "double",
        SqlScalarType.Decimal  => nullable ? "decimal?" : "decimal",
        SqlScalarType.Boolean  => nullable ? "bool?"    : "bool",
        SqlScalarType.Guid     => "System.Guid?",
        SqlScalarType.DateTime => nullable ? "System.DateTime?" : "System.DateTime",
        SqlScalarType.Binary   => "byte[]?",
        _                      => "string?"
    };

    private static IEnumerable<MetadataReference> CollectReferences()
    {
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;
        var refs = tpa
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(File.Exists)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

        var walhallaPath = typeof(SqlNativeProcedureContext).Assembly.Location;
        if (!string.IsNullOrEmpty(walhallaPath) && File.Exists(walhallaPath)
            && !refs.Any(r => ((PortableExecutableReference)r).FilePath
                               ?.Equals(walhallaPath, StringComparison.OrdinalIgnoreCase) == true))
        {
            refs.Add(MetadataReference.CreateFromFile(walhallaPath));
        }

        return refs;
    }
}
