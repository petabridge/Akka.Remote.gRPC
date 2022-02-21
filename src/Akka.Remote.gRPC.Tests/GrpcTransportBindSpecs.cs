using System;
using System.Threading.Tasks;
using Akka.Actor;
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

        }
    }
}
