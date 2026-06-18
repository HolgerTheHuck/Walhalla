using Xunit;

namespace WalhallaSql.EfCore.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LocalNuGetConsumerSerialCollection
{
    public const string Name = "LocalNuGetConsumerSerial";
}
