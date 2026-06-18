using System.Data.Common;

namespace WalhallaSql.AdoNet;

/// <summary>
/// Registers the WalhallaSql ADO.NET provider with <see cref="System.Data.Common.DbProviderFactories">.
/// </summary>
public static class WalhallaSqlProviderRegistration
{
    /// <summary>The invariant name used to register the WalhallaSql ADO.NET provider.</summary>
    public const string InvariantName = "WalhallaSql.AdoNet";

    /// <summary>Registers the WalhallaSql provider factory if it has not already been registered.</summary>
    public static void Register()
    {
        DbProviderFactories.RegisterFactory(InvariantName, WalhallaSqlDbProviderFactory.Instance);
    }

    /// <summary>Returns the WalhallaSql provider factory, registering it first if necessary.</summary>
    public static DbProviderFactory GetFactory()
    {
        Register();
        return DbProviderFactories.GetFactory(InvariantName);
    }
}
