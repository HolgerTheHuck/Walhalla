// API-Key Authentifizierung fuer gRPC-Calls via Server-Side Interceptor

namespace Walhalla.VectorStore.Api;

using GrpcCore = global::Grpc.Core;

public class ApiKeyGrpcInterceptor : global::Grpc.Core.Interceptors.Interceptor
{
    private readonly string _expectedKey;

    public ApiKeyGrpcInterceptor(IConfiguration config)
    {
        _expectedKey = config["ApiKey"] ?? "walhalla-dev-key";
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        GrpcCore.ServerCallContext context,
        global::Grpc.Core.UnaryServerMethod<TRequest, TResponse> continuation)
    {
        var providedKey = context.RequestHeaders.GetValue("x-api-key");
        if (providedKey != _expectedKey)
        {
            throw new GrpcCore.RpcException(
                new GrpcCore.Status(GrpcCore.StatusCode.Unauthenticated, "Invalid or missing API key"));
        }

        return await continuation(request, context);
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        global::Grpc.Core.IAsyncStreamReader<TRequest> requestStream,
        GrpcCore.ServerCallContext context,
        global::Grpc.Core.ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        var providedKey = context.RequestHeaders.GetValue("x-api-key");
        if (providedKey != _expectedKey)
        {
            throw new GrpcCore.RpcException(
                new GrpcCore.Status(GrpcCore.StatusCode.Unauthenticated, "Invalid or missing API key"));
        }

        return await continuation(requestStream, context);
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        GrpcCore.IServerStreamWriter<TResponse> responseStream,
        GrpcCore.ServerCallContext context,
        global::Grpc.Core.ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        var providedKey = context.RequestHeaders.GetValue("x-api-key");
        if (providedKey != _expectedKey)
        {
            throw new GrpcCore.RpcException(
                new GrpcCore.Status(GrpcCore.StatusCode.Unauthenticated, "Invalid or missing API key"));
        }

        await continuation(request, responseStream, context);
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        global::Grpc.Core.IAsyncStreamReader<TRequest> requestStream,
        GrpcCore.IServerStreamWriter<TResponse> responseStream,
        GrpcCore.ServerCallContext context,
        global::Grpc.Core.DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        var providedKey = context.RequestHeaders.GetValue("x-api-key");
        if (providedKey != _expectedKey)
        {
            throw new GrpcCore.RpcException(
                new GrpcCore.Status(GrpcCore.StatusCode.Unauthenticated, "Invalid or missing API key"));
        }

        await continuation(requestStream, responseStream, context);
    }
}
