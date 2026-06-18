using System;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;

var path = @"C:\Users\hhuck\.nuget\packages\microsoft.entityframeworkcore.specification.tests\8.0.11\lib\net8.0\Microsoft.EntityFrameworkCore.Specification.Tests.dll";
var settings = new DecompilerSettings();
var decompiler = new CSharpDecompiler(path, settings);

var typeName = new FullTypeName("Microsoft.EntityFrameworkCore.SaveChangesInterceptionTestBase");
Console.WriteLine(decompiler.DecompileTypeAsString(typeName));
