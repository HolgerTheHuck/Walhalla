using System;
using System.Linq;
using System.Reflection;

class Program {
    static void Main() {
        Assembly.LoadFrom(@"C:\Users\hhuck\.nuget\packages\xunit.extensibility.core\2.2.0\lib\netstandard1.1\xunit.core.dll");
        var asm = Assembly.LoadFrom(@"C:\Users\hhuck\.nuget\packages\microsoft.entityframeworkcore.specification.tests\8.0.11\lib\net8.0\Microsoft.EntityFrameworkCore.Specification.Tests.dll");
        var type = asm.GetTypes().FirstOrDefault(t => t.Name == "SaveChangesInterceptionTestBase");
        if (type == null) {
            Console.WriteLine("Type not found");
            return;
        }
        Console.WriteLine("Found: " + type.FullName);
        
        // Find AssertNormalOutcome
        var baseType = type;
        while (baseType != null && baseType != typeof(object)) {
            var method = baseType.GetMethod("AssertNormalOutcome", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method != null) {
                Console.WriteLine("Found AssertNormalOutcome on: " + baseType.Name);
                break;
            }
            baseType = baseType.BaseType;
        }
        
        // List methods on SaveChangesInterceptionTestBase
        foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {
            if (m.Name.Contains("Intercept_SaveChanges") || m.Name.Contains("Assert")) {
                Console.WriteLine("Method: " + m.Name + " returns " + m.ReturnType.Name);
            }
        }
    }
}
