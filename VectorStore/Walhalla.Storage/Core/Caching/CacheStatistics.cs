// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Walhalla.Storage.Core.Caching;

/// <summary>
/// A point-in-time snapshot of LRU page-cache metrics.
/// Returned as part of <see cref="Walhalla.Storage.Core.WalhallaDiagnostics.Cache"/>.
/// </summary>
/// <param name="HitCount">Total number of cache lookups that found an entry since the cache was created.</param>
/// <param name="MissCount">Total number of cache lookups that missed (required a disk read) since the cache was created.</param>
/// <param name="CurrentSizeBytes">Approximate memory occupied by all currently cached pages, in bytes.</param>
/// <param name="CapacityBytes">Maximum memory the cache is allowed to use before evicting the least-recently-used entries.</param>
public readonly record struct CacheStatistics(long HitCount, long MissCount, long CurrentSizeBytes, long CapacityBytes);
