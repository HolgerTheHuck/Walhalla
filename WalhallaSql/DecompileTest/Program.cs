using System;
using System.Linq;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;

var assemblyPath = @"E:\Develop\WalhallaProject\WalhallaSql\WalhallaSql.EfCore.Tests\bin\Debug\net8.0\Microsoft.EntityFrameworkCore.Specification.Tests.dll";
var decompiler = new CSharpDecompiler(assemblyPath, new DecompilerSettings { ThrowOnAssemblyResolveErrors = false });
var name = new ICSharpCode.Decompiler.TypeSystem.FullTypeName("Microsoft.EntityFrameworkCore.SaveChangesInterceptionTestBase+SuppressingSaveChangesInterceptor");
var type = decompiler.TypeSystem.FindType(name).GetDefinition();
foreach (var method in type.Methods.Where(m => m.IsVirtual || m.Name.Contains("SavingChanges")))
{
    Console.WriteLine($"Method: {method.Name}");
    try { Console.WriteLine(decompiler.DecompileAsString(method.MetadataToken)); } catch { Console.WriteLine("  (could not decompile)"); }
}
