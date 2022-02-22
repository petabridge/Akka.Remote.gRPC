using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Configuration;
using Akka.Event;
using Akka.Remote.Transport;
using Akka.Remote.Transport.gRPC;
using Akka.Util;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
        public ConcurrentSet<GrpcHandler> ConnectionGroup { get; } = new ConcurrentSet<GrpcHandler>();

        private readonly Task<IAssociationEventListener> _listenerTask;
        public GrpcTransport Transport { get; }

        public ActorSystem System => Transport.System;
        

        public GrpcConnectionManager(Task<IAssociationEventListener> listenerTask, GrpcTransport transport)
        {
            _listenerTask = listenerTask;
            Transport = transport;
        }

        public async ValueTask<(GrpcHandler grpc, AssociationHandle handle)> StartHandlerAsync(
            IAsyncStreamReader<Akka.Remote.Transport.gRPC.Payload> requestStream,
            IAsyncStreamWriter<Akka.Remote.Transport.gRPC.Payload> responseStream,
            Address localAddress,
            Address remoteAddress,
            CancellationToken grpcCancellationToken)
        {
            var associationEventListener = await _listenerTask.ConfigureAwait(false);

            var handler = new GrpcHandler(this, requestStream, responseStream, localAddress, remoteAddress,
                grpcCancellationToken);
    
            var associationHandle = new GrpcAssociationHandle(localAddress, remoteAddress, handler);
            associationEventListener.Notify(new InboundAssociation(associationHandle));

            // prepare the event listener
            async Task RegisterEventListener()
            {
                var listener = await associationHandle.ReadHandlerSource.Task.ConfigureAwait(false);
                handler.RegisterListener(listener);
            }

            // don't want to await here
#pragma warning disable CS4014
            RegisterEventListener();
#pragma warning restore CS4014
            return (handler, associationHandle);
        }

        public async Task TerminateAsync()
        {
            try
            {
                var tasks = new List<Task>();
                // we take a snapshot of the list here to avoid "collection modified" errors
                foreach (var channel in ConnectionGroup.ToList())
                {
                    tasks.Add(channel.CloseAsync());
                }

                var all = Task.WhenAll(tasks);
                await all.ConfigureAwait(false);
            }
            finally
            {
                ConnectionGroup.Clear();
            }
        }
    }

    public sealed class GrpcTransport : Akka.Remote.Transport.Transport
    {
        private readonly GrpcConnectionManager _connectionManager;
        
        public GrpcTransport(ActorSystem system, Config config)
        {
            System = system;
            Config = config;
            Settings = GrpcTransportSettings.Create(config);
            Log = Logging.GetLogger(System, GetType());
            _associationListenerPromise = new TaskCompletionSource<IAssociationEventListener>();
            _connectionManager = new GrpcConnectionManager(_associationListenerPromise.Task, this);
        }

        private readonly TaskCompletionSource<IAssociationEventListener> _associationListenerPromise;
        private WebApplication? _host;

        public ILoggingAdapter Log { get; }

        public GrpcTransportSettings Settings { get; }

        public override string SchemeIdentifier => "grpc";
        
        public Address LocalAddress { get; private set; }
        
        private static async Task<IPEndPoint> ResolveNameAsync(DnsEndPoint address, AddressFamily addressFamily = AddressFamily.InterNetwork)
        {
            var resolved = await Dns.GetHostEntryAsync(address.Host).ConfigureAwait(false);
            var found = resolved.AddressList.LastOrDefault(a => a.AddressFamily == addressFamily);
            if (found == null)
            {
                throw new KeyNotFoundException($"Couldn't resolve IP endpoint from provided DNS name '{address}' with address family of '{addressFamily}'");
            }

            return new IPEndPoint(found, address.Port);
        }

        public override async Task<(Address, TaskCompletionSource<IAssociationEventListener>)> Listen()
        {
            EndPoint listenAddress;
            IPAddress ip;
            if (IPAddress.TryParse(Settings.Hostname, out ip))
                listenAddress = new IPEndPoint(ip, Settings.Port);
            else
            {
                var temp = new DnsEndPoint(Settings.Hostname, Settings.Port);
                
                // todo: need to add IPV6 handling
                listenAddress = await ResolveNameAsync(temp).ConfigureAwait(false);
            }
            
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
                            listenOptions.Protocols = HttpProtocols.Http2;
                        });

                        // todo - configure max frame size et al here
                        // see https://docs.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/options?view=aspnetcore-6.0
                        // options.Limits
                    })
                    .ConfigureServices(services =>
                    {
                        // add a single connection manager
                        services.AddSingleton<GrpcConnectionManager>(_connectionManager);
                        services.AddGrpc(options =>
                        {
                            //options.IgnoreUnknownServices = true;

                            // TODO: this might be a better place to set frame size limits
                            options.MaxReceiveMessageSize = null;
                            options.MaxSendMessageSize = null;
                        });

                    });

                _host = builder.Build();
                
                _host.UseRouting()
                    .UseEndpoints(ep =>
                {
                    ep.MapGrpcService<GrpcServerListener>();
                });
                
                // begin accepting incoming requests
                await _host.StartAsync();

                LocalAddress = MapGrpcConnectionToAddress(_host.Urls, SchemeIdentifier, System.Name,
                    Settings.PublicPort, Settings.PublicHostname ?? Settings.Hostname);

                return (LocalAddress, _associationListenerPromise);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to bind to {0}; shutting down gRPC transport.", listenAddress);
                try
                {
                    await Shutdown().ConfigureAwait(false);
                }
                catch
                {
                    // ignore errors occurring during shutdown
                }
                throw;
            }
        }

        internal static Address MapGrpcConnectionToAddress(ICollection<string> hostUrls, 
            string schemeIdentifier, string systemName, int? port = null, string publicHostname = null)
        {
            // TODO: probably need to do some parsing / filtering on hostUrls bindings here
            return MapGrpcConnectionToAddress(hostUrls.First(), schemeIdentifier, systemName, port, publicHostname);
        }
        
        internal static Address MapGrpcConnectionToAddress(string hostUrl, 
            string schemeIdentifier, string systemName, int? port = null, string publicHostname = null)
        {
            // TODO: probably need to do some parsing / filtering on hostUrls bindings here
            if (Uri.TryCreate(hostUrl, UriKind.Absolute, out var fakeAddr))
            {
                return new Address(schemeIdentifier, systemName, publicHostname ?? fakeAddr.Host, port ?? fakeAddr.Port);
            }
            return new Address(schemeIdentifier, systemName, publicHostname ?? hostUrl, port);
        }

        internal static async Task<string> MapAddressToGrpcEndpoint(Address address)
        {
            // todo: HTTPS support
            if (address.Port == null) throw new ArgumentException($"address port must not be null: {address}");
            if (address.Host == null) throw new ArgumentException($"address host must not be null: {address}");

            IPEndPoint listenAddress;
            if (IPAddress.TryParse(address.Host, out var ip))
                listenAddress = new IPEndPoint(ip, address.Port.Value);
            else
            {
                var temp = new DnsEndPoint(address.Host, address.Port.Value);
                
                // todo: need to add IPV6 handling
                listenAddress = await ResolveNameAsync(temp).ConfigureAwait(false);
            }
            
            return $"http://{listenAddress.Address}:{listenAddress.Port}";
        }

        public override bool IsResponsibleFor(Address remote)
        {
            return remote.Protocol.EndsWith(SchemeIdentifier);
        }

        public override async Task<AssociationHandle> Associate(Address remoteAddress)
        {
            /*
             * Establish outbound gRPC connection
             */
            var remoteChannel = GrpcChannel.ForAddress(await MapAddressToGrpcEndpoint(remoteAddress).ConfigureAwait(false));
            var client = new AkkaRemote.AkkaRemoteClient(remoteChannel);
            var options = new CallOptions() { };
            var ep = client.MessageEndpoint(options);
            var (grpc, associationHandle) = await _connectionManager.StartHandlerAsync(ep.ResponseStream, ep.RequestStream, LocalAddress, remoteAddress,
                options.CancellationToken).ConfigureAwait(false);
            
            // dispose client asynchronously
            async Task DisposeUponClose()
            {
                // this task will only terminate once the channel has been terminated
                await grpc.WhenTerminated.ConfigureAwait(false);
                try
                {
                    await remoteChannel.ShutdownAsync().ConfigureAwait(false);
                    ep.Dispose();
                }
                catch
                {
                    // don't really care about errors here
                }
            }

            // don't want to await
#pragma warning disable CS4014
            DisposeUponClose();
#pragma warning restore CS4014

            return associationHandle;
        }

        public override async Task<bool> Shutdown()
        {
            // terminate all channels prior to shutting down the server
            await _connectionManager.TerminateAsync().ConfigureAwait(false);
            
            // shut the server down
            if (_host != null)
            {
                await _host.StopAsync().ConfigureAwait(false);
            }

            return true;
        }
    }
}