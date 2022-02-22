using System;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Configuration;
using FluentAssertions;
using Xunit;

namespace Akka.Remote.gRPC.Tests
{
    public class GrpcTransportBindSpecs
    {
        [Fact]
        public async Task ShouldStartActorSystemWithGrpcEnabled()
        {
            // arrange
            var config = BootstrapSetup.Create()
                .WithConfig(GrpcTransportSettings.DefaultConfig)
                .WithActorRefProvider(ProviderSelection.Remote.Instance);

            // act
            using var actorSystem = ActorSystem.Create("ActorSys", config);
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // assert
            var extended = (ExtendedActorSystem)actorSystem;
            extended.Provider.DefaultAddress.Should().NotBe(Address.AllSystems);
            
            // validate that we can shutdown
            await actorSystem.Terminate();
        }

        public static BootstrapSetup CreateConfig(string host, int port)
        {
            var config = ConfigurationFactory.ParseString($@"akka.remote.grpc.hostname = ""{host}""
                akka.remote.grpc.port={port}").WithFallback(GrpcTransportSettings.DefaultConfig);
            
            var setup = BootstrapSetup.Create()
                .WithConfig(config)
                .WithActorRefProvider(ProviderSelection.Remote.Instance);

            return setup;
        }

        [Fact]
        public async Task ShouldAssociateActorSystemsWithGrpcEnabled()
        {
            // arrange
            using var as1 = ActorSystem.Create("Sys1", CreateConfig("localhost", 2555));
            using var as2 = ActorSystem.Create("Sys2", CreateConfig("localhost", 0));

            // act
            await Task.Delay(TimeSpan.FromMilliseconds(500)); // wait for binds
            
            // should have bound to a real port
            as2.As<ExtendedActorSystem>().Provider.DefaultAddress.Port.Should().NotBe(0);
        }
    }
}
