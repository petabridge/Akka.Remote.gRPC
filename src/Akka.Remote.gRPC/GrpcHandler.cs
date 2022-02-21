using System;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Remote.Transport;
using Akka.Remote.Transport.gRPC;
using Google.Protobuf;
using Grpc.Core;

namespace Akka.Remote.gRPC;

/// <summary>
/// Reusable abstraction shared between gRPC client and server connections.
/// </summary>
internal sealed class GrpcHandler : IDisposable, IEquatable<GrpcHandler>
{
    private readonly GrpcConnectionManager _connectionManager;
    private readonly IAsyncStreamReader<Payload> _requestStream;
    private readonly IAsyncStreamWriter<Payload> _responseStream;

    // driven by the gRPC connection
    private readonly CancellationToken _grpcCancellationToken;

    // managed by us
    private readonly CancellationTokenSource _internalCancellationToken;

    private IHandleEventListener _listener;
    private readonly TaskCompletionSource<Done> _readHandlerSet;
    private readonly TaskCompletionSource<Done> _shutdownTask;

    public GrpcHandler(GrpcConnectionManager connectionManager, IAsyncStreamReader<Payload> requestStream, IAsyncStreamWriter<Payload> responseStream,
        Address localAddress, Address remoteAddress, CancellationToken grpcCancellationToken)
    {
        _connectionManager = connectionManager;
        _connectionManager.ConnectionGroup.TryAdd(this);
        _requestStream = requestStream;
        _responseStream = responseStream;
        _grpcCancellationToken = grpcCancellationToken;
        RemoteAddress = remoteAddress;
        LocalAddress = localAddress;
        _internalCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(grpcCancellationToken);
        _readHandlerSet = new TaskCompletionSource<Done>(TaskCreationOptions.RunContinuationsAsynchronously);
        _shutdownTask = new TaskCompletionSource<Done>(TaskCreationOptions.RunContinuationsAsynchronously);
        _internalCancellationToken.Token.Register(() =>
        {
            _connectionManager.ConnectionGroup.TryRemove(this);
            _shutdownTask.TrySetResult(Done.Instance);
        });
    }

    public void RegisterListener(IHandleEventListener listener)
    {
        _listener = listener;
        _readHandlerSet.TrySetResult(Done.Instance);
#pragma warning disable CS4014
        DoRead();
#pragma warning restore CS4014
    }

    public async Task<Done> CloseAsync()
    {
        _internalCancellationToken.Cancel();
        return await _shutdownTask.Task.ConfigureAwait(false);
    }

    public bool Write(ByteString message)
    {
        if (!_internalCancellationToken.IsCancellationRequested)
        {
            // don't await
            _responseStream.WriteAsync(new Payload() { Message = message }).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    public Task<Done> WhenTerminated => _shutdownTask.Task;

    public Task<Done> WhenReadOpen => _readHandlerSet.Task;
        
    public Address RemoteAddress { get; }
        
    public Address LocalAddress { get; }

    private async Task DoRead()
    {
        // need to wait for the read handler to be set first
        await WhenReadOpen.ConfigureAwait(false);
        await foreach (var read in _requestStream.ReadAllAsync(_internalCancellationToken.Token).ConfigureAwait(false))
        {
            _listener.Notify(new InboundPayload(read.Message));
        }
    }

    public void Dispose()
    {
        _internalCancellationToken?.Cancel();
        _internalCancellationToken?.Dispose();
    }
        
    public bool Equals(GrpcHandler other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;
        return RemoteAddress.Equals(other.RemoteAddress) && LocalAddress.Equals(other.LocalAddress);
    }

    public override bool Equals(object obj)
    {
        return ReferenceEquals(this, obj) || obj is GrpcHandler other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(RemoteAddress, LocalAddress);
    }

    public static bool operator ==(GrpcHandler left, GrpcHandler right)
    {
        return Equals(left, right);
    }

    public static bool operator !=(GrpcHandler left, GrpcHandler right)
    {
        return !Equals(left, right);
    }
}