﻿using System;
using System.Net;
using System.Security.Principal;
using System.Runtime.CompilerServices;
using System.Threading;
using EventStore.Common.Utils;
using EventStore.Core.Authentication;
using EventStore.Core.Bus;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.TimerService;
using EventStore.Core.Settings;
using EventStore.Transport.Tcp;
using EventStore.Transport.Tcp.Messages;
using Microsoft.Extensions.Logging;

namespace EventStore.Core.Services.Transport.Tcp
{
    /// <summary>Manager for individual TCP connection. It handles connection lifecycle,
    /// heartbeats, message framing and dispatch to the memory bus.</summary>
    public class TcpConnectionManager : IHandle<TcpMessage.Heartbeat>, IHandle<TcpMessage.HeartbeatTimeout>, ITcpPackageListener
    {
        public static readonly TimeSpan ConnectionTimeout = TimeSpan.FromMilliseconds(1000);

        private static readonly ILogger Log = TraceLogger.GetLogger<TcpConnectionManager>();

        public readonly Guid ConnectionId;
        public readonly string ConnectionName;
        public readonly IPEndPoint RemoteEndPoint;
        public IPEndPoint LocalEndPoint { get { return _connection.LocalEndPoint; } }
        public bool IsClosed { get { return _isClosed != 0; } }
        public int SendQueueSize { get { return _connection.SendQueueSize; } }
        public string ClientConnectionName { get { return _clientConnectionName; } }

        private readonly ITcpConnection _connection;
        private readonly IEnvelope _tcpEnvelope;
        private readonly IPublisher _publisher;
        private readonly ITcpDispatcher _dispatcher;
        private int _messageNumber;
        private int _isClosed;
        private string _clientConnectionName;

        private byte _version;

        private readonly Action<TcpConnectionManager, DisassociateInfo> _connectionClosed;
        private readonly Action<TcpConnectionManager> _connectionEstablished;

        private readonly SendToWeakThisEnvelope _weakThisEnvelope;
        private readonly TimeSpan _heartbeatInterval;
        private readonly TimeSpan _heartbeatTimeout;
        private readonly int _connectionPendingSendBytesThreshold;
        private readonly int _connectionQueueSizeThreshold;

        private readonly IAuthenticationProvider _authProvider;
        private UserCredentials _defaultUser;
        private TcpServiceType _serviceType;

        public TcpConnectionManager(string connectionName,
                                    TcpServiceType serviceType,
                                    ITcpDispatcher dispatcher,
                                    IPublisher publisher,
                                    ITcpConnection openedConnection,
                                    IPublisher networkSendQueue,
                                    IAuthenticationProvider authProvider,
                                    TimeSpan heartbeatInterval,
                                    TimeSpan heartbeatTimeout,
                                    Action<TcpConnectionManager, DisassociateInfo> onConnectionClosed,
                                    int connectionPendingSendBytesThreshold,
                                    int connectionQueueSizeThreshold)
        {
            if (dispatcher is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.dispatcher); }
            if (publisher is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.publisher); }
            if (openedConnection is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.openedConnection); }
            if (networkSendQueue is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.networkSendQueue); }
            if (authProvider is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.authProvider); }

            ConnectionId = openedConnection.ConnectionId;
            ConnectionName = connectionName;

            openedConnection.ReadHandlerSource.TrySetResult(this);

            _serviceType = serviceType;
            _tcpEnvelope = new SendOverTcpEnvelope(this, networkSendQueue);
            _publisher = publisher;
            _dispatcher = dispatcher;
            _authProvider = authProvider;

            _weakThisEnvelope = new SendToWeakThisEnvelope(this);
            _heartbeatInterval = heartbeatInterval;
            _heartbeatTimeout = heartbeatTimeout;
            _connectionPendingSendBytesThreshold = connectionPendingSendBytesThreshold;
            _connectionQueueSizeThreshold = connectionQueueSizeThreshold;

            _connectionClosed = onConnectionClosed;

            RemoteEndPoint = openedConnection.RemoteEndPoint;
            _connection = openedConnection;
            _connection.ConnectionClosed += OnConnectionClosed;
            if (_connection.IsClosed)
            {
                OnConnectionClosed(_connection, DisassociateInfo.Success);
                return;
            }

            ScheduleHeartbeat(0);
        }

        public TcpConnectionManager(string connectionName,
                                    ITcpDispatcher dispatcher,
                                    IPublisher publisher,
                                    ITcpConnection openedConnection,
                                    IPublisher networkSendQueue,
                                    IAuthenticationProvider authProvider,
                                    TimeSpan heartbeatInterval,
                                    TimeSpan heartbeatTimeout,
                                    Action<TcpConnectionManager> onConnectionEstablished,
                                    Action<TcpConnectionManager, DisassociateInfo> onConnectionClosed)
        {
            if (dispatcher is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.dispatcher); }
            if (publisher is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.publisher); }
            if (openedConnection is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.openedConnection); }
            if (authProvider is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.authProvider); }

            ConnectionId = openedConnection.ConnectionId;
            ConnectionName = connectionName;

            openedConnection.ReadHandlerSource.TrySetResult(this);

            _tcpEnvelope = new SendOverTcpEnvelope(this, networkSendQueue);
            _publisher = publisher;
            _dispatcher = dispatcher;
            _authProvider = authProvider;

            _weakThisEnvelope = new SendToWeakThisEnvelope(this);
            _heartbeatInterval = heartbeatInterval;
            _heartbeatTimeout = heartbeatTimeout;
            _connectionPendingSendBytesThreshold = ESConsts.UnrestrictedPendingSendBytes;
            _connectionQueueSizeThreshold = ESConsts.MaxConnectionQueueSize;

            _connectionEstablished = onConnectionEstablished;
            _connectionClosed = onConnectionClosed;

            RemoteEndPoint = openedConnection.RemoteEndPoint;
            _connection = openedConnection;
            _connection.ConnectionClosed += OnConnectionClosed;
            if (_connection.IsClosed) { OnConnectionClosed(_connection, DisassociateInfo.Success); }
        }

        public void OnConnectionEstablished()
        {
            if (Log.IsInformationLevelEnabled()) { Log.OnConnectionEstablished(ConnectionName, ConnectionId, RemoteEndPoint); }

            ScheduleHeartbeat(0);

            var handler = _connectionEstablished;
            handler?.Invoke(this);
        }

        private void OnConnectionClosed(ITcpConnection connection, DisassociateInfo disassociateInfo)
        {
            if (Interlocked.CompareExchange(ref _isClosed, 1, 0) != 0) return;
            if (Log.IsInformationLevelEnabled()) { Log.OnConnectionClosed(ConnectionName, ClientConnectionName, connection.RemoteEndPoint, ConnectionId, disassociateInfo); }
            var handler = _connectionClosed;
            handler?.Invoke(this, disassociateInfo);
        }

        public void Notify(TcpPackage package)
        {
            Interlocked.Increment(ref _messageNumber);

            try
            {
                ProcessPackage(package);
            }
            catch (Exception e)
            {
                SendBadRequestAndClose(package.CorrelationId, $"Error while processing package. Error: {e}");
            }
        }
        public void HandleBadRequest(in Disassociated disassociated)
        {
            SendPackage(new TcpPackage(TcpCommand.BadRequest, Guid.Empty, Helper.UTF8NoBom.GetBytes(disassociated.ToString())), checkQueueSize: false);
            Log.ClosingConnectionDueToError(this, disassociated);
            _connection.Close(disassociated);
        }

        public void ProcessPackage(TcpPackage package)
        {
            if (_serviceType == TcpServiceType.External && (package.Flags & TcpFlags.TrustedWrite) != 0)
            {
                SendBadRequestAndClose(package.CorrelationId, "Trusted writes aren't accepted over the external TCP interface");
                return;
            }
            switch (package.Command)
            {
                case TcpCommand.HeartbeatResponseCommand:
                    break;
                case TcpCommand.HeartbeatRequestCommand:
                    SendPackage(new TcpPackage(TcpCommand.HeartbeatResponseCommand, package.CorrelationId, null));
                    break;
                case TcpCommand.IdentifyClient:
                    {
                        try
                        {
                            var message = (ClientMessage.IdentifyClient)_dispatcher.UnwrapPackage(package, _tcpEnvelope, null, null, null, this, _version);
                            if (Log.IsInformationLevelEnabled()) { Log.ConnectionIdentifiedByClient(this, message); }
                            _version = (byte)message.Version;
                            _clientConnectionName = message.ConnectionName;
                            _connection.SetClientConnectionName(_clientConnectionName);
                            SendPackage(new TcpPackage(TcpCommand.ClientIdentified, package.CorrelationId, null));
                        }
                        catch (Exception ex)
                        {
                            Log.ErrorIdentifyingClient(ex);
                        }
                        break;
                    }
                case TcpCommand.BadRequest:
                    {
                        var reason = string.Empty;
                        try { reason = Helper.UTF8NoBom.GetString(package.Data); } catch { }
                        Log.BadRequestReceivedFromWillStopServer(this, package.CorrelationId, reason);
                        break;
                    }
                case TcpCommand.Authenticate:
                    {
                        if (0u >= (uint)(package.Flags & TcpFlags.Authenticated))
                        {
                            ReplyNotAuthenticated(package.CorrelationId, "No user credentials provided.");
                        }
                        else
                        {
                            var defaultUser = new UserCredentials(package.Login, package.Password, null);
                            Interlocked.Exchange(ref _defaultUser, defaultUser);
                            _authProvider.Authenticate(new TcpDefaultAuthRequest(this, package.CorrelationId, defaultUser));
                        }
                        break;
                    }
                default:
                    {
                        var defaultUser = _defaultUser;
                        if ((package.Flags & TcpFlags.TrustedWrite) != 0)
                        {
                            UnwrapAndPublishPackage(package, UserManagement.SystemAccount.Principal, null, null);
                        }
                        else if ((package.Flags & TcpFlags.Authenticated) != 0)
                        {
                            _authProvider.Authenticate(new TcpAuthRequest(this, package, package.Login, package.Password));
                        }
                        else if (defaultUser is object)
                        {
                            if (defaultUser.User is object)
                            {
                                UnwrapAndPublishPackage(package, defaultUser.User, defaultUser.Login, defaultUser.Password);
                            }
                            else
                            {
                                _authProvider.Authenticate(new TcpAuthRequest(this, package, defaultUser.Login, defaultUser.Password));
                            }
                        }
                        else
                        {
                            UnwrapAndPublishPackage(package, null, null, null);
                        }
                        break;
                    }
            }
        }


        private void UnwrapAndPublishPackage(TcpPackage package, IPrincipal user, string login, string password)
        {
            Message message = null;
            string error = "";
            try
            {
                message = _dispatcher.UnwrapPackage(package, _tcpEnvelope, user, login, password, this, _version);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            if (message is object)
            {
                _publisher.Publish(message);
            }
            else
            {
                SendBadRequest(package.CorrelationId, $"Could not unwrap network package for command {package.Command}.\n{error}");
            }
        }

        private void ReplyNotAuthenticated(Guid correlationId, string description)
        {
            _tcpEnvelope.ReplyWith(new TcpMessage.NotAuthenticated(correlationId, description));
        }

        private void ReplyNotReady(Guid correlationId, string description)
        {
            _tcpEnvelope.ReplyWith(new ClientMessage.NotHandled(correlationId, TcpClientMessageDto.NotHandled.NotHandledReason.NotReady, description));
        }

        private void ReplyAuthenticated(Guid correlationId, UserCredentials userCredentials, IPrincipal user)
        {
            var authCredentials = new UserCredentials(userCredentials.Login, userCredentials.Password, user);
            Interlocked.CompareExchange(ref _defaultUser, authCredentials, userCredentials);
            _tcpEnvelope.ReplyWith(new TcpMessage.Authenticated(correlationId));
        }

        public void SendBadRequestAndClose(Guid correlationId, string message)
        {
            if (message is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.message); }

            SendPackage(new TcpPackage(TcpCommand.BadRequest, correlationId, Helper.UTF8NoBom.GetBytes(message)), checkQueueSize: false);
            Log.ClosingConnectionDueToError(this, message);
            _connection.Close(DisassociateInfo.Unknown, message);
        }

        public void SendBadRequest(Guid correlationId, string message)
        {
            if (message is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.message); }

            SendPackage(new TcpPackage(TcpCommand.BadRequest, correlationId, Helper.UTF8NoBom.GetBytes(message)), checkQueueSize: false);
        }

        public void Stop(string reason = null)
        {
#if DEBUG
            if (Log.IsTraceLevelEnabled()) { LogClosingConnection(reason); }
#endif
            _connection.Close(DisassociateInfo.Success, reason);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void LogClosingConnection(string reason)
        {
            Log.LogTrace("Closing connection '{connectionName}{clientConnectionName}' [{remoteEndPoint}, L{localEndPoint}, {connectionId:B}] cleanly.{reason}",
                      ConnectionName, ClientConnectionName.IsEmptyString() ? string.Empty : ":" + ClientConnectionName, RemoteEndPoint, LocalEndPoint, ConnectionId,
                      reason.IsEmpty() ? string.Empty : " Reason: " + reason);
        }

        public void SendMessage(Message message)
        {
            var package = _dispatcher.WrapMessage(message, _version);
            if (package is object) { SendPackage(package); }
        }

        private void SendPackage(TcpPackage package, bool checkQueueSize = true)
        {
            if (IsClosed) return;

            int queueSize;
            int queueSendBytes;
            if (checkQueueSize)
            {
                if ((queueSize = _connection.SendQueueSize) > _connectionQueueSizeThreshold)
                {
                    SendBadRequestAndClose(Guid.Empty, $"Connection queue size is too large: {queueSize}.");
                    return;
                }
                if (_connectionPendingSendBytesThreshold > ESConsts.UnrestrictedPendingSendBytes && (queueSendBytes = _connection.PendingSendBytes) > _connectionPendingSendBytesThreshold)
                {
                    SendBadRequestAndClose(Guid.Empty, $"Connection pending send bytes is too large: {queueSendBytes}.");
                    return;
                }
            }

            _connection.EnqueueSend(package);
        }

        public void Handle(TcpMessage.Heartbeat message)
        {
            if (IsClosed) return;

            var msgNum = Volatile.Read(ref _messageNumber);
            if (message.MessageNumber != msgNum)
                ScheduleHeartbeat(msgNum);
            else
            {
                SendPackage(new TcpPackage(TcpCommand.HeartbeatRequestCommand, Guid.NewGuid(), null));

                _publisher.Publish(TimerMessage.Schedule.Create(_heartbeatTimeout, _weakThisEnvelope, new TcpMessage.HeartbeatTimeout(msgNum)));
            }
        }

        public void Handle(TcpMessage.HeartbeatTimeout message)
        {
            if (IsClosed) return;

            var msgNum = Volatile.Read(ref _messageNumber);
            if (message.MessageNumber != msgNum)
                ScheduleHeartbeat(msgNum);
            else
                Stop($"HEARTBEAT TIMEOUT at msgNum {msgNum}");
        }

        private void ScheduleHeartbeat(int msgNum)
        {
            _publisher.Publish(TimerMessage.Schedule.Create(_heartbeatInterval, _weakThisEnvelope, new TcpMessage.Heartbeat(msgNum)));
        }

        private class SendToWeakThisEnvelope : IEnvelope
        {
            private readonly WeakReference _receiver;

            public SendToWeakThisEnvelope(object receiver)
            {
                _receiver = new WeakReference(receiver);
            }

            public void ReplyWith<T>(T message) where T : Message
            {
                if (_receiver.Target is IHandle<T> x) { x.Handle(message); }
            }
        }

        private class TcpAuthRequest : AuthenticationRequest
        {
            private readonly TcpConnectionManager _manager;
            private readonly TcpPackage _package;

            public TcpAuthRequest(TcpConnectionManager manager, TcpPackage package, string login, string password)
              : base($"(TCP) {manager.RemoteEndPoint}", login, password)
            {
                _manager = manager;
                _package = package;
            }

            public override void Unauthorized()
            {
                _manager.ReplyNotAuthenticated(_package.CorrelationId, "Not Authenticated");
            }

            public override void Authenticated(IPrincipal principal)
            {
                _manager.UnwrapAndPublishPackage(_package, principal, Name, SuppliedPassword);
            }

            public override void Error()
            {
                _manager.ReplyNotAuthenticated(_package.CorrelationId, "Internal Server Error");
            }

            public override void NotReady()
            {
                _manager.ReplyNotReady(_package.CorrelationId, "Server not ready");
            }
        }


        private class TcpDefaultAuthRequest : AuthenticationRequest
        {
            private readonly TcpConnectionManager _manager;
            private readonly Guid _correlationId;
            private readonly UserCredentials _userCredentials;

            public TcpDefaultAuthRequest(TcpConnectionManager manager, Guid correlationId, UserCredentials userCredentials)
                : base($"(TCP) {manager.RemoteEndPoint}", userCredentials.Login, userCredentials.Password)
            {
                _manager = manager;
                _correlationId = correlationId;
                _userCredentials = userCredentials;
            }

            public override void Unauthorized()
            {
                _manager.ReplyNotAuthenticated(_correlationId, "Unauthorized");
            }

            public override void Authenticated(IPrincipal principal)
            {
                _manager.ReplyAuthenticated(_correlationId, _userCredentials, principal);
            }

            public override void Error()
            {
                _manager.ReplyNotAuthenticated(_correlationId, "Internal Server Error");
            }

            public override void NotReady()
            {
                _manager.ReplyNotAuthenticated(_correlationId, "Server not yet ready");
            }
        }

        private class UserCredentials
        {
            public readonly string Login;
            public readonly string Password;
            public readonly IPrincipal User;

            public UserCredentials(string login, string password, IPrincipal user)
            {
                Login = login;
                Password = password;
                User = user;
            }
        }
    }
}
