using System;
using System.Linq;
using System.Reflection;

var dbContextType = typeof(Microsoft.EntityFrameworkCore.DbContext);

var saveChangesMethod = dbContextType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
    .First(m => m.Name == "SaveChanges" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(bool));

Console.WriteLine($"Method: {saveChangesMethod}");

var body = saveChangesMethod.GetMethodBody();
if (body != null)
{
    var il = body.GetILAsByteArray();
    Console.WriteLine($"IL length: {il.Length}");
    // Print first 200 bytes as hex
    Console.WriteLine(BitConverter.ToString(il.Take(Math.Min(200, il.Length)).ToArray()));
}

// Also look for StateManager.SaveChanges
var stateManagerType = Assembly.LoadFrom(@"E:\Develop\WalhallaProject\WalhallaSql\CountDebug\bin\Debug\net8.0\Microsoft.EntityFrameworkCore.dll")
    .GetTypes()
    .FirstOrDefault(t => t.Name == "StateManager");

if (stateManagerType != null)
{
    var smSaveChanges = stateManagerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        .FirstOrDefault(m => m.Name == "SaveChanges" && m.GetParameters().Length == 1);
    Console.WriteLine($"StateManager.SaveChanges: {smSaveChanges}");
    if (smSaveChanges != null)
    {
        var smBody = smSaveChanges.GetMethodBody();
        if (smBody != null)
        {
            var smIl = smBody.GetILAsByteArray();
            Console.WriteLine($"StateManager IL length: {smIl.Length}");
            Console.WriteLine(BitConverter.ToString(smIl.Take(Math.Min(300, smIl.Length)).ToArray()));
        }
    }
}
