using Xunit;
using System.Reflection;

namespace Microsoft.EntityFrameworkCore;

public class DebugProjTest
{
    [Fact]
    public void Compute_projected_columns()
    {
        var sql = "SELECT p.Id, s.Id, s.ParentId FROM Parents AS p LEFT JOIN Singles AS s ON p.Id = s.ParentId";
        var method = typeof(WalhallaSql.AdoNet.WalhallaSqlDbCommand).GetMethod("ComputeProjectedColumns", BindingFlags.NonPublic | BindingFlags.Static);
        var result = method?.Invoke(null, new object[] { sql }) as System.Collections.Generic.IReadOnlyList<string>;
        if (result == null)
            System.Console.WriteLine("ComputeProjectedColumns returned null");
        else
            System.Console.WriteLine("Projected columns: " + string.Join(", ", result));
    }
}
