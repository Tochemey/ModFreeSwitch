/*
    Copyright [2016] [Arsene Tochemey GANDOTE]

    Licensed under the Apache License, Version 2.0 (the "License");
    you may not use this file except in compliance with the License.
    You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

    Unless required by applicable law or agreed to in writing, software
    distributed under the License is distributed on an "AS IS" BASIS,
    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
    See the License for the specific language governing permissions and
    limitations under the License.
*/

using System;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Core.Commands;
using Core.Common;
using Core.Events;
using Core.Messages;
using DotNetty.Codecs;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using NLog;

namespace Core.Handlers.outbound
{
    public class OutboundSession : IOutboundListener
    {
        private readonly Bootstrap _bootstrap;

        private readonly MultithreadEventLoopGroup _eventLoopGroup;

        private readonly Subject<EventStream> _eventReceived = new Subject<EventStream>();
        private readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private IChannel _channel;

        public OutboundSession(string address,
            int port,
            string password,
            TimeSpan connectionTimeout)
        {
            Address = address;
            Port = port;
            Password = password;
            ConnectionTimeout = connectionTimeout;
            _eventLoopGroup = new MultithreadEventLoopGroup();
            _bootstrap = new Bootstrap();

            Initialize();
        }

        public OutboundSession(string address,
            int port,
            string password)
        {
            Address = address;
            Port = port;
            Password = password;
            ConnectionTimeout = new TimeSpan(0,
                0,
                0,
                0,
                1000);
            _eventLoopGroup = new MultithreadEventLoopGroup();
            _bootstrap = new Bootstrap();

            Initialize();
        }

        public OutboundSession() : this("localhost",
            8021,
            "ClueCon")
        {
        }

        public OutboundSession(TimeSpan timeout) : this("localhost",
            8021,
            "ClueCon",
            timeout)
        {
        }

        public string Address { get; }
        public bool Authenticated { get; private set; }
        public TimeSpan ConnectionTimeout { get; }
        public string Password { get; }
        public int Port { get; }
        public IObservable<EventStream> EventReceived => _eventReceived.AsObservable();

        public async Task OnAuthentication()
        {
            await AuthenticateAsync();
        }

        public async Task OnDisconnectNotice(FsMessage fsMessage,
            EndPoint channelEndPoint)
        {
            _logger.Debug("received disconnection message : {0}",
                fsMessage);
            _logger.Warn("channel {0} disconnected",
                channelEndPoint);
            await CleanUpAsync();
        }

        public async Task OnError(Exception exception)
        {
            switch (exception)
            {
                // disconnect when we have encountered system related errors
                case DecoderException _:
                    _logger.Warn($"Encountered an issue during encoding: {exception}. shutting down...");
                    await DisconnectAsync();
                    return;
                case SocketException _:
                    _logger.Warn($"Encountered an issue on the channel: {exception}. shutting down...");
                    await DisconnectAsync();
                    return;
                default:
                    _logger.Error($"Encountered an issue : {exception}");
                    break;
            }
        }

        public void OnEventReceived(FsMessage fsMessage)
        {
            try
            {
                var eslEvent = new FsEvent(fsMessage);
                var eventType = EnumExtensions.Parse<EventType>(eslEvent.EventName);
                _eventReceived.OnNext(new EventStream(eslEvent,
                    eventType));
            }
            catch (Exception exception)
            {
                _logger.Warn($"Encountered an issue on the channel: {exception}.");
                _eventReceived.OnError(exception);
            }
        }

        public async Task OnRudeRejection()
        {
            _logger.Warn("channel {0} received rude/rejection",
                _channel.RemoteAddress);
            await CleanUpAsync();
        }

        public bool IsSessionReady()
        {
            return Authenticated && IsActive();
        }

        public async Task CleanUpAsync()
        {
            if (_eventLoopGroup != null) await _eventLoopGroup.ShutdownGracefullyAsync();
        }

        public async Task ConnectAsync()
        {
            _logger.Info("connecting to freeSwitch mod_event_socket...");
            _channel = await _bootstrap.ConnectAsync(Address,
                Port);
            _logger.Info("successfully connected to freeSwitch mod_event_socket.");
        }

        public async Task DisconnectAsync()
        {
            if (_channel != null) await _channel.CloseAsync();
            if (_eventLoopGroup != null) await _eventLoopGroup.ShutdownGracefullyAsync();
        }

        public bool IsActive()
        {
            return _channel != null && _channel.Active;
        }

        public async Task<ApiResponse> SendApiAsync(ApiCommand apiCommand)
        {
            if (!IsSessionReady()) return null;
            var handler = (OutboundSessionHandler) _channel.Pipeline.Last();
            var response = await handler.SendApiAsync(apiCommand,
                _channel);
            return response;
        }

        public async Task<Guid> SendBgApiAsync(BgApiCommand bgApiCommand)
        {
            if (!IsSessionReady()) return Guid.Empty;
            var handler = (OutboundSessionHandler) _channel.Pipeline.Last();
            return await handler.SendBgApiAsync(bgApiCommand,
                _channel);
        }

        public async Task<CommandReply> SendCommandAsync(BaseCommand command)
        {
            if (!IsSessionReady()) return null;
            var handler = (OutboundSessionHandler) _channel.Pipeline.Last();
            var reply = await handler.SendCommandAsync(command,
                _channel);
            return reply;
        }

        public async Task<bool> SubscribeAsync(string events)
        {
            if (!IsSessionReady()) return false;
            var handler = (OutboundSessionHandler) _channel.Pipeline.Last();
            var command = new EventCommand(events);
            var reply = await handler.SendCommandAsync(command,
                _channel);
            return reply.IsOk;
        }

        protected async Task AuthenticateAsync()
        {
            var command = new AuthCommand(Password);
            var handler = (OutboundSessionHandler) _channel.Pipeline.Last();
            var reply = await handler.SendCommandAsync(command,
                _channel);
            Authenticated = reply.IsOk;
        }

        protected void Initialize()
        {
            _bootstrap.Group(_eventLoopGroup);
            _bootstrap.Channel<TcpSocketChannel>();
            _bootstrap.Option(ChannelOption.SoLinger,
                1);
            _bootstrap.Option(ChannelOption.TcpNodelay,
                true);
            _bootstrap.Option(ChannelOption.SoKeepalive,
                true);
            _bootstrap.Option(ChannelOption.SoReuseaddr,
                true);
            _bootstrap.Option(ChannelOption.ConnectTimeout,
                ConnectionTimeout);
            _bootstrap.Handler(new OutboundSessionInitializer(this));
        }
    }
}