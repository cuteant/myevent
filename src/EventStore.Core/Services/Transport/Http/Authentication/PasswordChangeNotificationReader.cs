﻿using System;
using Microsoft.Extensions.Logging;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Helpers;
using EventStore.Core.Messages;
using EventStore.Core.Services.UserManagement;
using Newtonsoft.Json;
using EventStore.Common.Utils;

namespace EventStore.Core.Services.Transport.Http.Authentication
{
    public class PasswordChangeNotificationReader : IHandle<SystemMessage.SystemStart>,
                                                    IHandle<SystemMessage.BecomeShutdown>
    {
        private readonly IPublisher _publisher;
        private readonly IODispatcher _ioDispatcher;
        private readonly ILogger _log;
        private bool _stopped;

        public PasswordChangeNotificationReader(IPublisher publisher, IODispatcher ioDispatcher)
        {
            _publisher = publisher;
            _ioDispatcher = ioDispatcher;
            _log = TraceLogger.GetLogger<UserManagementService>();
        }

        private void Start()
        {
            _stopped = false;
            _ioDispatcher.ReadBackward(
                UserManagementService.UserPasswordNotificationsStreamId, -1, 1, false, SystemAccount.Principal,
                completed =>
                    {
                        switch (completed.Result)
                        {
                            case ReadStreamResult.NoStream:
                                ReadNotificationsFrom(0);
                                break;
                            case ReadStreamResult.Success:
                                var completedEvts = completed.Events;
                                if (0u >= (uint)completedEvts.Length)
                                    ReadNotificationsFrom(0);
                                else
                                    ReadNotificationsFrom(completedEvts[0].Event.EventNumber + 1);
                                break;
                            default:
                                throw new Exception(
                                    "Failed to initialize password change notification reader. Cannot read "
                                    + UserManagementService.UserPasswordNotificationsStreamId + " Error: "
                                    + completed.Result);
                        }
                    });
        }

        private void ReadNotificationsFrom(long fromEventNumber)
        {
            if (_stopped) return;
            _ioDispatcher.ReadForward(
                UserManagementService.UserPasswordNotificationsStreamId, fromEventNumber, 100, false,
                SystemAccount.Principal, completed =>
                {
                    if (_stopped) return;
                    switch (completed.Result)
                    {
                        case ReadStreamResult.AccessDenied:
                        case ReadStreamResult.Error:
                        case ReadStreamResult.NotModified:
                            _log.FailedToReadUserPasswordNotificationsStream(completed.Result);
                            _ioDispatcher.Delay(
                                TimeSpan.FromSeconds(10), () => ReadNotificationsFrom(fromEventNumber));
                            break;
                        case ReadStreamResult.NoStream:
                        case ReadStreamResult.StreamDeleted:
                            _ioDispatcher.Delay(
                                TimeSpan.FromSeconds(1), () => ReadNotificationsFrom(0));
                            break;
                        case ReadStreamResult.Success:
                            foreach (var @event in completed.Events)
                                PublishPasswordChangeNotificationFrom(@event);
                            if (completed.IsEndOfStream)
                                _ioDispatcher.Delay(
                                    TimeSpan.FromSeconds(1), () => ReadNotificationsFrom(completed.NextEventNumber));
                            else
                                ReadNotificationsFrom(completed.NextEventNumber);
                            break;
                        default:
                            throw new NotSupportedException();
                    }
                },
                () => {
                    if (_log.IsWarningLevelEnabled()) { _log.Timeout_reading_stream(); }
                    _ioDispatcher.Delay(TimeSpan.FromSeconds(10), () => ReadNotificationsFrom(fromEventNumber));
                },
                Guid.NewGuid());
        }


        private class Notification
        {
#pragma warning disable 649
            public string LoginName;
#pragma warning restore 649
        }

        private void PublishPasswordChangeNotificationFrom(in ResolvedEvent @event)
        {
            var data = @event.Event.Data;
            try
            {
                var notification = data.ParseJson<Notification>();
                _publisher.Publish(
                    new InternalAuthenticationProviderMessages.ResetPasswordCache(notification.LoginName));
            }
            catch (JsonException ex)
            {
                _log.FailedToDeserializeEvent(@event.OriginalEventNumber, ex);
            }
        }

        public void Handle(SystemMessage.SystemStart message)
        {
            Start();
        }

        public void Handle(SystemMessage.BecomeShutdown message)
        {
            _stopped = true;
        }
    }
}
