using System;
using System.Data.Common;
using System.Collections.Generic;
using System.Text;
using WalhallaSql.EfCore.Migrations;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace WalhallaSql.EfCore;

public sealed class WalhallaSqlDbContextOptionsExtension : RelationalOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    public WalhallaSqlDbContextOptionsExtension()
    {
    }

    private WalhallaSqlDbContextOptionsExtension(WalhallaSqlDbContextOptionsExtension copyFrom)
        : base(copyFrom)
    {
        LayeredOptions = copyFrom.LayeredOptions;
    }

    public WalhallaSqlEfCoreOptions? LayeredOptions { get; private set; }

    public override DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    protected override RelationalOptionsExtension Clone()
    {
        return new WalhallaSqlDbContextOptionsExtension(this);
    }

    public WalhallaSqlDbContextOptionsExtension WithLayeredOptions(WalhallaSqlEfCoreOptions layeredOptions)
    {
        var clone = (WalhallaSqlDbContextOptionsExtension)Clone();
        clone.LayeredOptions = layeredOptions ?? throw new ArgumentNullException(nameof(layeredOptions));

        if (!string.IsNullOrWhiteSpace(layeredOptions.ConnectionString))
            clone = (WalhallaSqlDbContextOptionsExtension)clone.WithConnectionString(layeredOptions.ConnectionString);

        return clone;
    }

    public WalhallaSqlDbContextOptionsExtension WithExistingConnection(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return (WalhallaSqlDbContextOptionsExtension)WithConnection(connection, owned: false);
    }

    public override void ApplyServices(IServiceCollection services)
    {
        services.AddEntityFrameworkWalhallaSql();
    }

    public override void Validate(IDbContextOptions options)
    {
        base.Validate(options);

        if (LayeredOptions == null)
            throw new InvalidOperationException("WalhallaSql options are missing. Call UseWalhallaSql(...) when configuring DbContextOptions.");
    }

    private sealed class ExtensionInfo : RelationalExtensionInfo
    {
        private string? _logFragment;

        public ExtensionInfo(IDbContextOptionsExtension extension)
            : base(extension)
        {
        }

        private new WalhallaSqlDbContextOptionsExtension Extension => (WalhallaSqlDbContextOptionsExtension)base.Extension;

        public override string LogFragment
        {
            get
            {
                if (_logFragment == null)
                {
                    var builder = new StringBuilder();

                    builder.Append(base.LogFragment);

                    if (Extension.LayeredOptions != null)
                    {
                        builder.Append("using WalhallaSql ");
                    }

                    _logFragment = builder.ToString();
                }

                return _logFragment;
            }
        }

        public override int GetServiceProviderHashCode()
        {
            return base.GetServiceProviderHashCode();
        }

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
            debugInfo["WalhallaSql"] = Extension.LayeredOptions == null ? "0" : "1";
        }

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
        {
            return other is ExtensionInfo && base.ShouldUseSameServiceProvider(other);
        }
    }
}
