// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Walhalla.VectorStore.Microsoft.Extensions.VectorData;

/// <summary>
/// Microsoft.Extensions.VectorData-Connector für Walhalla.VectorStore.
/// </summary>
public sealed class WalhallaVectorStore : global::Microsoft.Extensions.VectorData.VectorStore
{
    private readonly EmbeddedVectorStore _store;
    private readonly WalhallaVectorStoreOptions _options;

    /// <summary>
    /// Öffnet oder erstellt einen Walhalla-Store im angegebenen Verzeichnis.
    /// </summary>
    public WalhallaVectorStore(string path)
        : this(new EmbeddedVectorStore(path))
    {
    }

    /// <summary>
    /// Nutzt eine bestehende EmbeddedVectorStore-Instanz.
    /// </summary>
    public WalhallaVectorStore(EmbeddedVectorStore store, WalhallaVectorStoreOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? new WalhallaVectorStoreOptions();
    }

    /// <inheritdoc />
    public override global::Microsoft.Extensions.VectorData.VectorStoreCollection<TKey, TRecord> GetCollection<TKey, TRecord>(
        string name,
        global::Microsoft.Extensions.VectorData.VectorStoreCollectionDefinition? definition)
    {
        return new WalhallaVectorStoreCollection<TKey, TRecord>(_store, name, definition, _options);
    }

    /// <inheritdoc />
    public override global::Microsoft.Extensions.VectorData.VectorStoreCollection<object, Dictionary<string, object?>> GetDynamicCollection(
        string name,
        global::Microsoft.Extensions.VectorData.VectorStoreCollectionDefinition? definition)
    {
        return new WalhallaVectorStoreCollection<object, Dictionary<string, object?>>(
            _store, name, definition, _options);
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<string> ListCollectionNamesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var collection in _store.GetCollections())
        {
            yield return collection.Name;
        }
    }

    /// <inheritdoc />
    public override Task<bool> CollectionExistsAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_store.GetCollection(name) is not null);
    }

    /// <inheritdoc />
    public override Task EnsureCollectionDeletedAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        _store.DeleteCollection(name);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override object GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(EmbeddedVectorStore)) return _store;
        throw new NotSupportedException($"Service {serviceType} is not supported by WalhallaVectorStore.");
    }
}
