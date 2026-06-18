// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Linq;
using Walhalla.Indexes.FullText;
using Walhalla.Storage.Core.Configuration;
using Walhalla.Storage.Core.Runtime;
using Xunit;

namespace Walhalla.VectorStore.Tests;

public class PersistentFullTextIndexTests
{
    [Fact]
    public void IndexDocument_Reopen_SearchFindsPersistedPostings()
    {
        string path = CreateTempPath();
        try
        {
            using (var store = CreateStore(path))
            {
                var index = new PersistentFullTextIndex(store, "test:title");
                index.IndexDocument(1, "Hello World");
                index.IndexDocument(2, "Hello Universe");
            }

            using (var store = CreateStore(path))
            {
                var index = new PersistentFullTextIndex(store, "test:title");

                var result = index.Search("hello world");

                Assert.NotNull(result);
                Assert.True(result!.Get(1));
                Assert.False(result.Get(2));
                Assert.Equal(2, index.DocumentCount);
                Assert.Equal(3, index.TermCount);
            }
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    [Fact]
    public void IndexDocument_Reindex_RemovesOldPersistedTerms()
    {
        string path = CreateTempPath();
        try
        {
            using (var store = CreateStore(path))
            {
                var index = new PersistentFullTextIndex(store, "test:description");
                index.IndexDocument(1, "Hello World");
                index.IndexDocument(1, "Goodbye Universe");
            }

            using (var store = CreateStore(path))
            {
                var index = new PersistentFullTextIndex(store, "test:description");

                Assert.Null(index.Search("hello"));
                var result = index.Search("goodbye");
                Assert.NotNull(result);
                Assert.True(result!.Get(1));
                Assert.Equal(1, index.DocumentCount);
                Assert.Equal(2, index.TermCount);
            }
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    [Fact]
    public void RemoveDocument_Reopen_RemovesPersistedPostings()
    {
        string path = CreateTempPath();
        try
        {
            using (var store = CreateStore(path))
            {
                var index = new PersistentFullTextIndex(store, "test:body");
                index.IndexDocument(1, "Hello World");
                index.IndexDocument(2, "Hello Universe");
                index.RemoveDocument(1);
            }

            using (var store = CreateStore(path))
            {
                var index = new PersistentFullTextIndex(store, "test:body");

                var result = index.Search("hello");
                Assert.NotNull(result);
                Assert.False(result!.Get(1));
                Assert.True(result.Get(2));
                Assert.Equal(1, index.DocumentCount);
                Assert.Equal(2, index.TermCount);
            }
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    [Fact]
    public void SearchScored_Reopen_RanksByTermFrequency()
    {
        string path = CreateTempPath();
        try
        {
            using (var store = CreateStore(path))
            {
                var index = new PersistentFullTextIndex(store, "test:rank");
                index.IndexDocument(1, "agent memory agent memory");
                index.IndexDocument(2, "agent memory");
                index.IndexDocument(3, "agent only");
            }

            using (var store = CreateStore(path))
            {
                var index = new PersistentFullTextIndex(store, "test:rank");

                var results = index.SearchScored("agent memory", topK: 10).ToArray();

                Assert.Equal(2, results.Length);
                Assert.Equal(1L, results[0].Id);
                Assert.Equal(2L, results[1].Id);
                Assert.True(results[0].Score > results[1].Score);
            }
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    [Fact]
    public void SearchAnyScored_Reopen_ReturnsUnionRanked()
    {
        string path = CreateTempPath();
        try
        {
            using (var store = CreateStore(path))
            {
                var index = new PersistentFullTextIndex(store, "test:any");
                index.IndexDocument(1, "hello world");
                index.IndexDocument(2, "goodbye world");
                index.IndexDocument(3, "hello goodbye world");
            }

            using (var store = CreateStore(path))
            {
                var index = new PersistentFullTextIndex(store, "test:any");

                var results = index.SearchAnyScored("hello goodbye", topK: 10).ToArray();

                Assert.Equal(3, results.Length);
                Assert.Equal(3L, results[0].Id);
                Assert.Contains(results, result => result.Id == 1);
                Assert.Contains(results, result => result.Id == 2);
                Assert.True(results[0].Score > results[1].Score);
            }
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    [Fact]
    public void SearchScored_Reopen_SupportsPhraseAndNotQueries()
    {
        string path = CreateTempPath();
        try
        {
            using (var store = CreateStore(path))
            {
                var index = new PersistentFullTextIndex(store, "test:phrase");
                index.IndexDocument(1, "shared agent memory handbook");
                index.IndexDocument(2, "shared memory agent handbook");
                index.IndexDocument(3, "shared agent memory private notes");
            }

            using (var store = CreateStore(path))
            {
                var index = new PersistentFullTextIndex(store, "test:phrase");

                var results = index.SearchScored("shared \"agent memory\"", 10, FullTextQueryMode.Any, "private").ToArray();

                Assert.Equal(2, results.Length);
                Assert.Equal(1L, results[0].Id);
                Assert.Equal(2L, results[1].Id);
            }
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    private static WalhallaStore CreateStore(string path)
    {
        return new WalhallaStore(new WalhallaOptions(path)
        {
            WalSyncMode = WalSyncMode.None,
            AutoCheckpointWalThresholdBytes = 0
        });
    }

    private static string CreateTempPath()
    {
        return Path.Combine(Path.GetTempPath(), "walhalla-ft-" + Guid.NewGuid().ToString("N"));
    }

    private static void DeleteTempPath(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}