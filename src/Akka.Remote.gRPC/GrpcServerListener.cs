using System.Threading.Tasks;
using Akka.Actor;
using Akka.Remote.Transport.gRPC;
using Grpc.Core;
using Grpc.Net.Client;

namespace Akka.Remote.gRPC;

/// <summary>
/// Inbound
/// </summary>
internal sealed class GrpcServerListener : AkkaRemote.AkkaRemoteBase
{
    private readonly GrpcConnectionManager _connectionManager;
    
    public GrpcServerListener(GrpcConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
    }

    public GrpcTransport Transport => _connectionManager.Transport;

    public Address LocalAddress => Transport.LocalAddress;

    public ActorSystem System => _connectionManager.System;

    /*
     * New server handle will need to be created each time here...
     */
    public override async Task MessageEndpoint(IAsyncStreamReader<Payload> requestStream,
        IServerStreamWriter<Payload> responseStream, ServerCallContext context)
    {
        // have to parse this here https://github.com/grpc/grpc/blob/master/doc/naming.md
        // currently showing up as IPV4 addresses
        var remoteAddress =GrpcTransport.MapGrpcConnectionToAddress(context.Peer, Transport.SchemeIdentifier, System.Name, 0);
        var localAddress = _connectionManager.Transport.LocalAddress;

        var grpc = await _connectionManager.StartHandlerAsync(requestStream, responseStream, localAddress,
            remoteAddress, context.CancellationToken);

        // force the handler to be reported as an inbound association to Akka.Remote
        var handle = await _connectionManager.Transport.Init(grpc, grpc.LocalAddress, grpc.RemoteAddress).ConfigureAwait(false);

        // read / write until signaled for termination.
        await grpc.WhenTerminated.ConfigureAwait(false);
    }
}