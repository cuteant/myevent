﻿using System;
using CuteAnt.Buffers;
using EventStore.Common.Utils;
using EventStore.Core.Data;
using EventStore.Core.Helpers;
using EventStore.Core.Messages;
using EventStore.Core.Services.Storage.ReaderIndex;
using EventStore.Core.Services.UserManagement;
using EventStore.Core.Settings;
using Microsoft.Extensions.Logging;

namespace EventStore.Projections.Core.Services.Processing
{
    public interface IEmittedStreamsTracker
    {
        void TrackEmittedStream(EmittedEvent[] emittedEvents);
        void Initialize();
    }

    public class EmittedStreamsTracker : IEmittedStreamsTracker
    {
        private static readonly ILogger Log = TraceLogger.GetLogger<EmittedStreamsTracker>();
        private readonly IODispatcher _ioDispatcher;
        private readonly ProjectionConfig _projectionConfig;
        private readonly ProjectionNamesBuilder _projectionNamesBuilder;
        private readonly BoundedCache<string, string> _streamIdCache = new BoundedCache<string, string>(int.MaxValue, ESConsts.CommitedEventsMemCacheLimit, x => 16 + 4 + IntPtr.Size + 2 * x.Length);
        private const int MaxRetryCount = 3;
        private readonly object _locker = new object();

        public EmittedStreamsTracker(IODispatcher ioDispatcher, ProjectionConfig projectionConfig, ProjectionNamesBuilder projectionNamesBuilder)
        {
            _ioDispatcher = ioDispatcher;
            _projectionConfig = projectionConfig;
            _projectionNamesBuilder = projectionNamesBuilder;
        }

        public void Initialize()
        {
            ReadEmittedStreamStreamIdsIntoCache(0); //start from the beginning
        }

        private void ReadEmittedStreamStreamIdsIntoCache(long position)
        {
            _ioDispatcher.ReadForward(_projectionNamesBuilder.GetEmittedStreamsName(), position, 1, false, SystemAccount.Principal, x =>
            {
                if ((uint)x.Events.Length > 0u)
                {
                    for (int i = 0; i < x.Events.Length; i++)
                    {
                        var streamId = Helper.UTF8NoBom.GetString(x.Events[i].Event.Data);
                        lock (_locker)
                        {
                            _streamIdCache.PutRecord(streamId, streamId, false);
                        }
                    }
                }
                if (!x.IsEndOfStream)
                {
                    ReadEmittedStreamStreamIdsIntoCache(x.NextEventNumber);
                }
            });
        }

        public void TrackEmittedStream(EmittedEvent[] emittedEvents)
        {
            if (!_projectionConfig.TrackEmittedStreams) return;
            foreach (var emittedEvent in emittedEvents)
            {
                if (!_streamIdCache.TryGetRecord(emittedEvent.StreamId, out string streamId))
                {
                    var trackEvent = new Event(Guid.NewGuid(), ProjectionEventTypes.StreamTracked, false, Helper.UTF8NoBom.GetBytes(emittedEvent.StreamId), null);
                    lock (_locker)
                    {
                        _streamIdCache.PutRecord(emittedEvent.StreamId, emittedEvent.StreamId, false);
                    }
                    WriteEvent(trackEvent, MaxRetryCount);
                }
            }
        }

        private void WriteEvent(Event evnt, int retryCount)
        {
            _ioDispatcher.WriteEvent(_projectionNamesBuilder.GetEmittedStreamsName(), ExpectedVersion.Any, evnt, SystemAccount.Principal, x => OnWriteComplete(x, evnt, Helper.UTF8NoBom.GetString(evnt.Data), retryCount));
        }

        private void OnWriteComplete(ClientMessage.WriteEventsCompleted completed, Event evnt, string streamId, int retryCount)
        {
            if (completed.Result != OperationResult.Success)
            {
                if (retryCount > 0)
                {
                    WriteEvent(evnt, retryCount - 1);
                }
                else
                {
                    Log.FailedToWriteATrackedStreamIdOfToTheStream(streamId, _projectionNamesBuilder.GetEmittedStreamsName(), MaxRetryCount, completed.Result);
                }
            }
        }
    }
}