using System;
using System.Threading.Tasks;
using Akka.Actor;
using FluentAssertions;
using Xunit;
using static Akka.Remote.gRPC.Tests.GrpcHelpers;

namespace Akka.Remote.gRPC.Tests;

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
        extended.Provider.DefaultAddress.Protocol.Should().Be("akka.grpc");
            
        // validate that we can shutdown
        await actorSystem.Terminate();
    }

    [Fact]
    public async Task ShouldBindToPortZero()
    {
       // arrange
       using var as2 = ActorSystem.Create("Sys2", CreateConfig("localhost", 0));
       
       // act
       await Task.Delay(TimeSpan.FromMilliseconds(500)); // wait for binds
            
       // assert
       // should have bound to a real port
       as2.As<ExtendedActorSystem>().Provider.DefaultAddress.Port.Should().NotBe(0);
    }
}