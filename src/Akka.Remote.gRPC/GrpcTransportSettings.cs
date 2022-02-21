using System;
using System.Net;
using Akka.Configuration;

namespace Akka.Remote.gRPC;

public sealed record GrpcTransportSettings
{
    /// <summary>
    /// Default GRPC transport configuration
    /// </summary>
    public static readonly Config DefaultConfig = ConfigurationFactory.FromResource<GrpcTransportSettings>("Akka.Remote.gRPC.grpc.conf");
    
    public static GrpcTransportSettings Create(Config config)
    {
        if (config.IsNullOrEmpty())
            throw ConfigurationException.NullOrEmptyConfig<GrpcTransportSettings>();

        var host = config.GetString("hostname", null);
        if (string.IsNullOrEmpty(host)) host = IPAddress.Any.ToString();
        var publicHost = config.GetString("public-hostname", null);
        var publicPort = config.GetInt("public-port", 0);

        var connectTimeout = config.GetTimeSpan("connection-timeout", TimeSpan.FromSeconds(15));

        return new GrpcTransportSettings()
        {
            ConnectTimeout = connectTimeout,
            Hostname = host,
            PublicHostname = !string.IsNullOrEmpty(publicHost) ? publicHost : host,
            Port = config.GetInt("port", 2553),
            PublicPort = publicPort > 0 ? publicPort : null
        };
    }

    /// <summary>
    /// Sets a connection timeout for all outbound connections
    /// i.e. how long a connect may take until it is timed out.
    /// </summary>
    public TimeSpan ConnectTimeout { get; init; }

    /// <summary>
    /// The hostname or IP to bind the remoting to.
    /// </summary>
    public string Hostname { get; init; }

    /// <summary>
    /// If this value is set, this becomes the public address for the actor system on this
    /// transport, which might be different than the physical ip address (hostname)
    /// this is designed to make it easy to support private / public addressing schemes
    /// </summary>
    public string PublicHostname { get; init; }

    /// <summary>
    /// The default remote server port clients should connect to.
    /// Default is 2552 (AKKA), use 0 if you want a random available port
    /// This port needs to be unique for each actor system on the same machine.
    /// </summary>
    public int Port { get; init; }

    /// <summary>
    /// If this value is set, this becomes the public port for the actor system on this
    /// transport, which might be different than the physical port
    /// this is designed to make it easy to support private / public addressing schemes
    /// </summary>
    public int? PublicPort { get; init; }
}