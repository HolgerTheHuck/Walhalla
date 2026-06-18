using Xunit;

namespace Microsoft.EntityFrameworkCore;

[CollectionDefinition(SharedTypeQueryCollectionName, DisableParallelization = true)]
public sealed class SharedTypeQuerySpecSerialCollection
{
    public const string SharedTypeQueryCollectionName = "EF8 SharedTypeQuery Serial";
}

[CollectionDefinition(ConcurrencyDetectorCollectionName, DisableParallelization = true)]
public sealed class ConcurrencyDetectorSpecSerialCollection
{
    public const string ConcurrencyDetectorCollectionName = "EF8 ConcurrencyDetector Serial";
}

[CollectionDefinition(DataAnnotationCollectionName, DisableParallelization = true)]
public sealed class DataAnnotationSpecSerialCollection
{
    public const string DataAnnotationCollectionName = "EF8 DataAnnotation Serial";
}
