using System;
using System.Linq;
using System.Reflection;

var assembly = Assembly.Load("Microsoft.Extensions.VectorData.Abstractions");

var defType = assembly.GetTypes().First(t => t.Name == "FilteredRecordRetrievalOptions`1");
var orderByProp = defType.GetProperty("OrderBy")!;

Console.WriteLine($"OrderBy PropertyType: {orderByProp.PropertyType}");
Console.WriteLine($"OrderBy CanRead: {orderByProp.CanRead}");
Console.WriteLine($"OrderBy CanWrite: {orderByProp.CanWrite}");

// Check set method parameters
var setMethod = orderByProp.SetMethod;
if (setMethod != null)
{
    foreach (var p in setMethod.GetParameters())
    {
        Console.WriteLine($"  Set param: {p.ParameterType.FullName} {p.Name}");
    }
}
