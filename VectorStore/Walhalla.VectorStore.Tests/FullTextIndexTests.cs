// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Linq;
using Walhalla.Indexes.FullText;
using Xunit;

namespace Walhalla.VectorStore.Tests;

public class FullTextIndexTests
{
    [Fact]
    public void IndexDocument_And_Search_SingleTerm()
    {
        var index = new FullTextIndex();
        index.IndexDocument(1, "Hello World");

        var result = index.Search("hello");
        Assert.NotNull(result);
        Assert.True(result!.Get(1));
        Assert.False(result.Get(2));
    }

    [Fact]
    public void Search_MultiTerm_AND()
    {
        var index = new FullTextIndex();
        index.IndexDocument(1, "Hello World");
        index.IndexDocument(2, "Hello Universe");
        index.IndexDocument(3, "Goodbye World");

        var result = index.Search("hello world");
        Assert.NotNull(result);
        Assert.True(result!.Get(1));
        Assert.False(result.Get(2));
        Assert.False(result.Get(3));
    }

    [Fact]
    public void SearchAny_MultiTerm_OR()
    {
        var index = new FullTextIndex();
        index.IndexDocument(1, "Hello World");
        index.IndexDocument(2, "Hello Universe");
        index.IndexDocument(3, "Goodbye World");

        var result = index.SearchAny("hello goodbye");
        Assert.NotNull(result);
        Assert.True(result!.Get(1));
        Assert.True(result.Get(2));
        Assert.True(result.Get(3));
    }

    [Fact]
    public void Search_TermNotFound_ReturnsNull()
    {
        var index = new FullTextIndex();
        index.IndexDocument(1, "Hello World");

        var result = index.Search("missing");
        Assert.Null(result);
    }

    [Fact]
    public void RemoveDocument_RemovesFromPostings()
    {
        var index = new FullTextIndex();
        index.IndexDocument(1, "Hello World");
        index.IndexDocument(2, "Hello Universe");

        index.RemoveDocument(1);

        var result = index.Search("hello");
        Assert.NotNull(result);
        Assert.False(result!.Get(1));
        Assert.True(result.Get(2));
    }

    [Fact]
    public void ReindexDocument_UpdatesTerms()
    {
        var index = new FullTextIndex();
        index.IndexDocument(1, "Hello World");
        index.IndexDocument(1, "Goodbye Universe");

        var oldResult = index.Search("hello");
        Assert.Null(oldResult);

        var newResult = index.Search("goodbye");
        Assert.NotNull(newResult);
        Assert.True(newResult!.Get(1));
    }

    [Fact]
    public void CaseInsensitive_Search()
    {
        var index = new FullTextIndex();
        index.IndexDocument(1, "HELLO World");

        var result = index.Search("hello");
        Assert.NotNull(result);
        Assert.True(result!.Get(1));
    }

    [Fact]
    public void EmptyQuery_ReturnsNull()
    {
        var index = new FullTextIndex();
        index.IndexDocument(1, "Hello World");

        Assert.Null(index.Search(""));
        Assert.Null(index.Search("   "));
        Assert.Null(index.SearchAny(""));
    }

    [Fact]
    public void Punctuation_IsSplit()
    {
        var index = new FullTextIndex();
        index.IndexDocument(1, "Hello, world! How are you?");

        var result = index.Search("hello");
        Assert.NotNull(result);
        Assert.True(result!.Get(1));

        var result2 = index.Search("world");
        Assert.NotNull(result2);
        Assert.True(result2!.Get(1));
    }

    [Fact]
    public void Search_PhraseQuery_MatchesOrderedTokensOnly()
    {
        var index = new FullTextIndex();
        index.IndexDocument(1, "agent memory handbook");
        index.IndexDocument(2, "memory agent handbook");

        var result = index.Search("\"agent memory\"");

        Assert.NotNull(result);
        Assert.True(result!.Get(1));
        Assert.False(result.Get(2));
    }

    [Fact]
    public void SearchAny_NotQuery_ExcludesNegativeMatches()
    {
        var index = new FullTextIndex();
        index.IndexDocument(1, "agent memory handbook");
        index.IndexDocument(2, "agent private handbook");

        var result = index.SearchAny("agent", "private");

        Assert.NotNull(result);
        Assert.True(result!.Get(1));
        Assert.False(result.Get(2));
    }

    [Fact]
    public void DocumentCount_TracksCorrectly()
    {
        var index = new FullTextIndex();
        Assert.Equal(0, index.DocumentCount);

        index.IndexDocument(1, "Hello");
        Assert.Equal(1, index.DocumentCount);

        index.IndexDocument(2, "World");
        Assert.Equal(2, index.DocumentCount);

        index.RemoveDocument(1);
        Assert.Equal(1, index.DocumentCount);
    }

    [Fact]
    public void TermCount_TracksCorrectly()
    {
        var index = new FullTextIndex();
        Assert.Equal(0, index.TermCount);

        index.IndexDocument(1, "Hello World");
        Assert.Equal(2, index.TermCount);

        index.IndexDocument(2, "Hello Universe");
        Assert.Equal(3, index.TermCount);

        index.RemoveDocument(1);
        // "world" sollte entfernt werden, "hello" und "universe" bleiben
        Assert.Equal(2, index.TermCount);
    }
}
