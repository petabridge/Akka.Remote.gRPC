using Akka.Actor;
using Akka.Actor.Setup;
using Akka.Configuration;

namespace Akka.Remote.gRPC.Tests;

public static class GrpcHelpers
{
    public static ActorSystemSetup CreateConfig(string host, int port)
    {
        var config = ConfigurationFactory.ParseString($@"akka.remote.grpc.hostname = ""{host}""
                akka.remote.grpc.port={port}").WithFallback(GrpcTransportSettings.DefaultConfig);
            
        var setup = ActorSystemSetup.Create().WithSetup(BootstrapSetup.Create()
            .WithConfig(config)
            .WithActorRefProvider(ProviderSelection.Remote.Instance));

        return setup;
    }
}