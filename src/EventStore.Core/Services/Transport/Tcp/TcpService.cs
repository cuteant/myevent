﻿using System;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using EventStore.Common.Utils;
using EventStore.Core.Authentication;
using EventStore.Core.Bus;
using EventStore.Core.Messages;
using EventStore.Transport.Tcp;
using Microsoft.Extensions.Logging;

namespace EventStore.Core.Services.Transport.Tcp
{
    public enum TcpServiceType
    {
        Internal,
        External
    }

    public enum TcpSecurityType
    {
        Normal,
        Secure
    }

    public class TcpService : DotNettyServerTransport,
        IHandle<SystemMessage.SystemInit>,
        IHandle<SystemMessage.SystemStart>,
        IHandle<SystemMessage.BecomeShuttingDown>
    {
        private static readonly ILogger Log = TraceLogger.GetLogger<TcpService>();

        private readonly IPublisher _publisher;
        private readonly IPEndPoint _serverEndPoint;
        private readonly IPublisher _networkSendQueue;
        private readonly TcpServiceType _serviceType;
        private readonly TcpSecurityType _securityType;
        private readonly Func<Guid, IPEndPoint, ITcpDispatcher> _dispatcherFactory;
        private readonly TimeSpan _heartbeatInterval;
        private readonly TimeSpan _heartbeatTimeout;
        private readonly IAuthenticationProvider _authProvider;
        private readonly X509Certificate2 _certificate;
        private readonly int _connectionPendingSendBytesThreshold;
        private readonly int _connectionQueueSizeThreshold;

        public TcpService(
            DotNettyTransportSettings transportSettings,
            IPublisher publisher,
            IPEndPoint serverEndPoint,
            IPublisher networkSendQueue,
            TcpServiceType serviceType,
            TcpSecurityType securityType,
            ITcpDispatcher dispatcher,
            TimeSpan heartbeatInterval,
            TimeSpan heartbeatTimeout,
            IAuthenticationProvider authProvider,
            X509Certificate2 certificate,
            int connectionPendingSendBytesThreshold,
            int connectionQueueSizeThreshold)
            : this(transportSettings, publisher, serverEndPoint, networkSendQueue, serviceType, securityType, (_, __) => dispatcher,
                   heartbeatInterval, heartbeatTimeout, authProvider, certificate, connectionPendingSendBytesThreshold, connectionQueueSizeThreshold)
        {
        }

        public TcpService(
            DotNettyTransportSettings transportSettings,
            IPublisher publisher,
            IPEndPoint serverEndPoint,
            IPublisher networkSendQueue,
            TcpServiceType serviceType,
            TcpSecurityType securityType,
            Func<Guid, IPEndPoint, ITcpDispatcher> dispatcherFactory,
            TimeSpan heartbeatInterval,
            TimeSpan heartbeatTimeout,
            IAuthenticationProvider authProvider,
            X509Certificate2 certificate,
            int connectionPendingSendBytesThreshold,
            int connectionQueueSizeThreshold)
            : base(transportSettings, certificate)
        {
            if (publisher is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.publisher); }
            if (serverEndPoint is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.serverEndPoint); }
            if (networkSendQueue is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.networkSendQueue); }
            if (dispatcherFactory is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.dispatcherFactory); }
            if (authProvider is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.authProvider); }
            if (securityType == TcpSecurityType.Secure && certificate is null)
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.certificate);

            _publisher = publisher;
            _serverEndPoint = serverEndPoint;
            _networkSendQueue = networkSendQueue;
            _serviceType = serviceType;
            _securityType = securityType;
            _dispatcherFactory = dispatcherFactory;
            _heartbeatInterval = heartbeatInterval;
            _heartbeatTimeout = heartbeatTimeout;
            _connectionPendingSendBytesThreshold = connectionPendingSendBytesThreshold;
            _connectionQueueSizeThreshold = connectionQueueSizeThreshold;
            _authProvider = authProvider;
            _certificate = certificate;
        }

        public void Handle(SystemMessage.SystemInit message)
        {
            try
            {
                if (Logger.IsInformationLevelEnabled()) Logger.StartingTcpListeningOnTcpEndpoint(_securityType, _serverEndPoint);

                ListenAsync(_serverEndPoint).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                Application.Exit(ExitCode.Error, e.Message);
            }
        }

        public void Handle(SystemMessage.SystemStart message)
        {
        }

        public void Handle(SystemMessage.BecomeShuttingDown message)
        {
            Shutdown().Ignore();
        }

        public override void Notify(in InboundConnection inboundConn)
        {
            var conn = inboundConn.Connection;
            if (Log.IsInformationLevelEnabled()) { Log.OnTcpConnectionAccepted(_serviceType, _securityType, conn); }

            var dispatcher = _dispatcherFactory(conn.ConnectionId, _serverEndPoint);
            var manager = new TcpConnectionManager(
                    string.Format("{0}-{1}", _serviceType.ToString().ToLowerInvariant(), _securityType.ToString().ToLowerInvariant()),
                    _serviceType,
                    dispatcher,
                    _publisher,
                    conn,
                    _networkSendQueue,
                    _authProvider,
                    _heartbeatInterval,
                    _heartbeatTimeout,
                    (m, e) => _publisher.Publish(new TcpMessage.ConnectionClosed(m, e)),
                    _connectionPendingSendBytesThreshold,
                    _connectionQueueSizeThreshold); // TODO AN: race condition
        }
    }
}
