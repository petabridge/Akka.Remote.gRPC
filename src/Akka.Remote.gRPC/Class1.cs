using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Akka.Remote.Transport;
using Akka.Remote.Transport.gRPC;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal;

namespace Akka.Remote.gRPC
{
    internal class GrpcHandler
    {
        
    }
    
    /// <summary>
    /// Outbound
    /// </summary>
    internal sealed class GrpcClient
    {
        private readonly IAsyncStreamReader<Payload> _requestStream;
        private readonly IServerStreamWriter<Payload> _responseStream;
        
        // driven by the gRPC connection
        private readonly CancellationToken _grpcCancellationToken;
        
        // managed by us
        private readonly CancellationTokenSource _internalCancellationToken;

        private IHandleEventListener _listener;
        private TaskCompletionSource<Done> _readHandlerSet;
        private TaskCompletionSource<Done> _shutdownTask;

        public GrpcClient(IAsyncStreamReader<Payload> requestStream, IServerStreamWriter<Payload> responseStream, CancellationToken grpcCancellationToken)
        {
            _requestStream = requestStream;
            _responseStream = responseStream;
            _grpcCancellationToken = grpcCancellationToken;
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
                _responseStream.WriteAsync(new Payload(){Message = message});
                return true;
            }

            return false;
        }

        public Task<Done> WhenTerminated => _shutdownTask.Task;

        public Task<Done> WhenReadOpen => _readHandlerSet.Task;

        private async Task DoRead()
        {
            // need to wait for the read handler to be set first
            await WhenReadOpen;
            await foreach (var read in _requestStream.ReadAllAsync(_internalCancellationToken.Token))
            {
                _listener.Notify(new InboundPayload(read.Message));
            }
        }
    }
    
    /// <summary>
    /// Inbound
    /// </summary>
    internal sealed class GrpcTransport : AkkaRemote.AkkaRemoteBase
    {
        private readonly Channel<ByteString> _writeBytes;
        private readonly Channel<ByteString> _readBytes;

        public GrpcTransport(Channel<ByteString> writeBytes, Channel<ByteString> readBytes)
        {
            _writeBytes = writeBytes;
            _readBytes = readBytes;
        }

        /*
         * New server handle will need to be created each time here...
         */
        public override async Task MessageEndpoint(IAsyncStreamReader<Payload> requestStream, IServerStreamWriter<Payload> responseStream, ServerCallContext context)
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
