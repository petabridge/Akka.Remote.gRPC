using System.Threading.Tasks;
using Akka.Remote.Transport.gRPC;
using Grpc.Core;

namespace Akka.Remote.gRPC;

/// <summary>
/// Inbound
/// </summary>
internal sealed class GrpcServerListener : AkkaRemote.AkkaRemoteBase
{
    public GrpcServerListener(GrpcConnectionManager connectionManager)
    {
        
    }

    /*
         * New server handle will need to be created each time here...
         */
    public override async Task MessageEndpoint(IAsyncStreamReader<Payload> requestStream,
        IServerStreamWriter<Payload> responseStream, ServerCallContext context)
    {
        var serverHandler = new Grp
    }
}