﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Configuration;
using Akka.Event;
using Akka.Remote.Transport;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Akka.Remote.gRPC
{
    internal sealed class GrpcAssociationHandle : AssociationHandle
    {
        private readonly GrpcHandler _handler;

        public GrpcAssociationHandle(Address localAddress, Address remoteAddress, GrpcHandler handler) : base(
            localAddress, remoteAddress)
        {
            _handler = handler;
        }

        public override bool Write(ByteString payload)
        {
            return _handler.Write(payload);
        }

        public override void Disassociate()
        {
            // begin closing the channel
#pragma warning disable CS4014
            _handler.CloseAsync();
#pragma warning restore CS4014
        }
    }

    /// <summary>
    /// INTERNAL API
    ///
    /// Responsible for managing open connections and spawning new <see cref="GrpcHandler"/>s
    /// for both inbound and outbound connections.
    /// </summary>
    internal sealed class GrpcConnectionManager
    {
        private readonly ConcurrentBag<GrpcHandler> _allConnections = new ConcurrentBag<GrpcHandler>();

        private readonly Task<IAssociationEventListener> _listenerTask;
        public GrpcTransport Transport { get; }

        public ActorSystem System => Transport.System;
        

        public GrpcConnectionManager(Task<IAssociationEventListener> listenerTask, GrpcTransport transport)
        {
            _listenerTask = listenerTask;
            Transport = transport;
        }

        public ValueTask<GrpcHandler> StartHandlerAsync(
            IAsyncStreamReader<Akka.Remote.Transport.gRPC.Payload> requestStream,
            IServerStreamWriter<Akka.Remote.Transport.gRPC.Payload> responseStream,
            CancellationToken grpcCancellationToken)
        {
            
        }
    }

    public sealed class GrpcTransport : Akka.Remote.Transport.Transport
    {
        public GrpcTransport(ActorSystem system, Config config)
        {
            System = system;
            Config = config;
            Settings = GrpcTransportSettings.Create(config);
            Log = Logging.GetLogger(System, GetType());
            _associationListenerPromise = new TaskCompletionSource<IAssociationEventListener>();
        }

        private readonly TaskCompletionSource<IAssociationEventListener> _associationListenerPromise;
        private WebApplication? _host;

        public ILoggingAdapter Log { get; }

        public ActorSystem System { get; }

        public GrpcTransportSettings Settings { get; }

        public override string SchemeIdentifier => "grpc";

        public override async Task<(Address, TaskCompletionSource<IAssociationEventListener>)> Listen()
        {
            EndPoint listenAddress;
            IPAddress ip;
            if (IPAddress.TryParse(Settings.Hostname, out ip))
                listenAddress = new IPEndPoint(ip, Settings.Port);
            else
                listenAddress = new DnsEndPoint(Settings.Hostname, Settings.Port);

            try
            {
                var builder = WebApplication.CreateBuilder();
                builder.WebHost.ConfigureKestrel(options =>
                    {
                        // bind to the specified address
                        options.Listen(listenAddress, listenOptions =>
                        {
                            // todo: want HTTP1 to support platforms like Azure App Service,
                            // but  need to lean on HTTP2 as the default for performance reasons.
                            listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
                        });

                        // todo - configure max frame size et al here
                        // see https://docs.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/options?view=aspnetcore-6.0
                        // options.Limits
                    })
                    .ConfigureServices(services =>
                    {
                        services.AddGrpc(options =>
                        {
                            options.IgnoreUnknownServices = true;

                            // TODO: this might be a better place to set frame size limits
                            options.MaxReceiveMessageSize = null;
                            options.MaxSendMessageSize = null;
                        });

                    })
                    .Configure(app =>
                    {
                        app.UseEndpoints(ep =>
                        {
                            ep.MapGrpcService<GrpcServerListener>();
                        });
                    });

                _host = builder.Build();
                
                // begin accepting incoming requests
                await _host.StartAsync();

                var address = MapGrpcConnectionToAddress(_host.Urls, SchemeIdentifier, System.Name,
                    Settings.PublicPort ?? Settings.Port);
            }
        }

        internal static Address MapGrpcConnectionToAddress(ICollection<string> hostUrls, 
            string schemeIdentifier, string systemName, int port, string publicHostname = null)
        {
            // TODO: probably need to do some parsing / filtering on hostUrls bindings here
            return hostUrls == null
                ? null
                : new Address(schemeIdentifier, systemName, publicHostname ?? hostUrls.First(), port);
        }

        public override bool IsResponsibleFor(Address remote)
        {
            throw new NotImplementedException();
        }

        public override async Task<AssociationHandle> Associate(Address remoteAddress)
        {
            throw new NotImplementedException();
        }

        public override async Task<bool> Shutdown()
        {
            if (_host != null)
                await _host.StopAsync();
        }
    }
}