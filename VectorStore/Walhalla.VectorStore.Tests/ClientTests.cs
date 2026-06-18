// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Walhalla.VectorStore.Client;
using Walhalla.VectorStore.Client.Models;

namespace Walhalla.VectorStore.Tests;

public class ClientTests
{
    [Fact]
    public void WalhallaClient_CanCreateAndDispose()
    {
        using var client = new WalhallaClient("http://localhost:5000");
        Assert.NotNull(client);
    }

    [Fact]
    public void WalhallaClient_WithChannel_DoesNotDisposeExternalChannel()
    {
        using var channel = global::Grpc.Net.Client.GrpcChannel.ForAddress("http://localhost:5000");
        var client = new WalhallaClient(channel);
        client.Dispose();

        // Externer Channel darf nicht disposed sein
        Assert.NotNull(channel);
    }

    [Fact]
    public void DistanceMetric_AllValuesHaveStringRepresentation()
    {
        Assert.Equal("Euclidean", DistanceMetric.Euclidean.ToString());
        Assert.Equal("Cosine", DistanceMetric.Cosine.ToString());
        Assert.Equal("DotProduct", DistanceMetric.DotProduct.ToString());
    }

    [Fact]
    public void FullTextQueryMode_AllValuesHaveStringRepresentation()
    {
        Assert.Equal("All", FullTextQueryMode.All.ToString());
        Assert.Equal("Any", FullTextQueryMode.Any.ToString());
    }
}
