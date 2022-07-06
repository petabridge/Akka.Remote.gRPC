# Akka.Remote.gRPC

Prototype gRPC transport for [Akka.NET](https://getakka.net/) built on top of ASP.NET gRPC / .NET 6.0.

## Installation

To use this transport:

```
PS> Install-Package Akka.Remote.gRPC
```

### Configuration (HOCON)

Then configure the following in your HOCON for Akka.Remote:

```hocon
akka.remote{
    enabled-transports = ["akka.remote.grpc"]
    
    grpc{
          transport-class = "Akka.Remote.gRPC.GrpcTransport,Akka.Remote.gRPC"
    
          # The default remote server port clients should connect to.
          # Default is 2553 (AKKA), use 0 if you want a random available port
          # This port needs to be unique for each actor system on the same machine.
          port = 2553
    
          # Similar in spirit to "public-hostname" setting, this allows Akka.Remote users
          # to alias the port they're listening on. The socket will actually listen on the
          # "port" setting, but when connecting to other ActorSystems this node will advertise
          # itself as being connected to the "public-port". This is helpful when working with 
          # hosting environments that rely on address translation and port-forwarding, such as Docker.
          #
          # Leave this setting to "0" if you don't intend to use it.
          public-port = 0
    
          # The hostname or ip to bind the remoting to,
          # InetAddress.getLocalHost.getHostAddress is used if empty
          hostname = ""
    
          # If this value is set, this becomes the public address for the actor system on this
          # transport, which might be different than the physical ip address (hostname)
          # this is designed to make it easy to support private / public addressing schemes
          public-hostname = ""
    }
}
```

> **`akka.remote.grpc.hostname` and `akka.remote.grpc.port` should be populated with values specific to your application**, just like `akka.remote.dot-netty.tcp`.

From there you'll be able to address other remote `ActorSystem`s and `IActorRef`s using `akka.grpc://{ActorSystemName}@host:port/`.
