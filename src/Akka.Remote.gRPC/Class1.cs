using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Configuration;
using Akka.Event;
using Akka.Remote.Transport;
using Akka.Remote.Transport.gRPC;
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
    /// Reusable abstraction shared between gRPC client and server connections.
    /// </summary>
    internal sealed class GrpcHandler : IDisposable, IEquatable<GrpcHandler>
    {
        private readonly IAsyncStreamReader<Payload> _requestStream;
        private readonly IServerStreamWriter<Payload> _responseStream;

        // driven by the gRPC connection
        private readonly CancellationToken _grpcCancellationToken;

        // managed by us
        private readonly CancellationTokenSource _internalCancellationToken;

        private IHandleEventListener _listener;
        private readonly TaskCompletionSource<Done> _readHandlerSet;
        private readonly TaskCompletionSource<Done> _shutdownTask;

        public GrpcHandler(IAsyncStreamReader<Payload> requestStream, IServerStreamWriter<Payload> responseStream,
            Address remoteAddress, Address localAddress, CancellationToken grpcCancellationToken)
        {
            _requestStream = requestStream;
            _responseStream = responseStream;
            _grpcCancellationToken = grpcCancellationToken;
            RemoteAddress = remoteAddress;
            LocalAddress = localAddress;
            _internalCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(grpcCancellationToken);
            _readHandlerSet = new TaskCompletionSource<Done>(TaskCreationOptions.RunContinuationsAsynchronously);
            _shutdownTask = new TaskCompletionSource<Done>(TaskCreationOptions.RunContinuationsAsynchronously);
            _internalCancellationToken.Token.Register(() => _shutdownTask.TrySetResult(Done.Instance));
        }

        public void RegisterListener(IHandleEventListener listener)
        {
            _listener = listener;
            _readHandlerSet.TrySetResult(Done.Instance);
#pragma warning disable CS4014
            DoRead();
#pragma warning restore CS4014
        }

        public async Task<Done> CloseAsync()
        {
            _internalCancellationToken.Cancel();
            return await _shutdownTask.Task.ConfigureAwait(false);
        }

        public bool Write(ByteString message)
        {
            if (!_internalCancellationToken.IsCancellationRequested)
            {
                // don't await
                _responseStream.WriteAsync(new Payload() { Message = message });
                return true;
            }

            return false;
        }

        public Task<Done> WhenTerminated => _shutdownTask.Task;

        public Task<Done> WhenReadOpen => _readHandlerSet.Task;
        
        public Address RemoteAddress { get; }
        
        public Address LocalAddress { get; }

        private async Task DoRead()
        {
            // need to wait for the read handler to be set first
            await WhenReadOpen;
            await foreach (var read in _requestStream.ReadAllAsync(_internalCancellationToken.Token))
            {
                _listener.Notify(new InboundPayload(read.Message));
            }
        }

        public void Dispose()
        {
            _internalCancellationToken?.Cancel();
            _internalCancellationToken?.Dispose();
        }
        
        public bool Equals(GrpcHandler other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return RemoteAddress.Equals(other.RemoteAddress) && LocalAddress.Equals(other.LocalAddress);
        }

        public override bool Equals(object obj)
        {
            return ReferenceEquals(this, obj) || obj is GrpcHandler other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(RemoteAddress, LocalAddress);
        }

        public static bool operator ==(GrpcHandler left, GrpcHandler right)
        {
            return Equals(left, right);
        }

        public static bool operator !=(GrpcHandler left, GrpcHandler right)
        {
            return !Equals(left, right);
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
        private readonly IWebHost _host;
        
        // all open connections
        private readonly ConcurrentBag<GrpcHandler> _handlers = new ConcurrentBag<GrpcHandler>();

        public ILoggingAdapter Log { get; }

        public ActorSystem System { get; }

        public GrpcTransportSettings Settings { get; }

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

                    });
            }
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
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Inbound
    /// </summary>
    internal sealed class GrpcServerListener : AkkaRemote.AkkaRemoteBase
    {
        public GrpcServerListener(Channel<ByteString> writeBytes, Channel<ByteString> readBytes)
        {
            _writeBytes = writeBytes;
            _readBytes = readBytes;
        }

        /*
         * New server handle will need to be created each time here...
         */
        public override async Task MessageEndpoint(IAsyncStreamReader<Payload> requestStream,
            IServerStreamWriter<Payload> responseStream, ServerCallContext context)
        {
            //context.GetHttpContext().Connection.
            async Task DoWrites()
            {
                await foreach (var write in _writeBytes.Reader.ReadAllAsync(context.CancellationToken))
                {
                    await responseStream.WriteAsync(new Payload() { Message = write });
                }
            }

            async Task DoReads()
            {
                await foreach (var read in requestStream.ReadAllAsync(context.CancellationToken))
                {
                    await _readBytes.Writer.WriteAsync(read.Message, context.CancellationToken);
                }
            }

            // let the writes run asynchronously
            var writeTask = DoWrites();
            var readTask = DoReads();
            await Task.WhenAll(writeTask, readTask);
        }
    }
}