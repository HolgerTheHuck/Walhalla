using System;
using System.Linq;
using System.Reflection;
using System.IO;
class Program {
    static void Main() {
        var asm = Assembly.LoadFrom("E:/Develop/WalhallaProject/WalhallaSql/WalhallaSql.EfCore.Tests/bin/Debug/net8.0/Microsoft.EntityFrameworkCore.dll");
        var dbType = asm.GetTypes().FirstOrDefault(t => t.Name == "StateManager");
        if (dbType == null) {
            Console.WriteLine("StateManager not found");
            return;
        }
        var method = dbType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "SaveChanges" && m.GetParameters().Length == 1);
        if (method == null) {
            Console.WriteLine("SaveChanges not found");
            return;
        }
        var body = method.GetMethodBody();
        if (body == null) {
            Console.WriteLine("No method body");
            return;
        }
        var il = body.GetILAsByteArray();
        File.WriteAllBytes("/tmp/statemanager_il.bin", il);
        Console.WriteLine("IL bytes: " + il.Length);
        // Try to find references to Database or SaveChanges
        foreach (var fi in body.LocalVariables) {
            Console.WriteLine("Local: " + fi.LocalType?.Name);
        }
    }
}
