using System;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Actor.Dsl;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Akka.Remote.gRPC.Tests;

public class GrpcAssociationSpecs : TestKit.Xunit2.TestKit
{
    public GrpcAssociationSpecs(ITestOutputHelper output) : base(GrpcHelpers.CreateConfig("localhost", 0), output:output){}
    
    [Fact]
    public async Task ShouldAssociateActorSystemsWithGrpcEnabled()
    {
        // arrange
        using var as2 = ActorSystem.Create("Sys2", GrpcHelpers.CreateConfig("localhost", 0));
        as2.EventStream.Subscribe(TestActor, typeof(RemotingListenEvent));
        InitializeLogger(as2);
        
        var actor1 = Sys.ActorOf(act =>
        {
            act.ReceiveAny((o, context) => context.Sender.Tell(o));
        }, "target");
        

        // act
        await Task.Delay(TimeSpan.FromMilliseconds(500)); // wait for binds
            
        // should have bound to a real port
        var as1Address = Sys.As<ExtendedActorSystem>().Provider.DefaultAddress;

        var actorRef = await as2.ActorSelection(new RootActorPath(as1Address) / "user" / "target")
            .Ask<string>("hit", TimeSpan.FromMinutes(10));
        actorRef.Should().Be("hit");
    }    
}