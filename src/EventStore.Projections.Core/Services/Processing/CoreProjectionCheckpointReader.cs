﻿using System;
using System.Linq;
using CuteAnt.Buffers;
using EventStore.Common.Utils;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Helpers;
using EventStore.Core.Messages;
using EventStore.Core.Services.UserManagement;
using Microsoft.Extensions.Logging;

namespace EventStore.Projections.Core.Services.Processing
{
    public class CoreProjectionCheckpointReader : ICoreProjectionCheckpointReader
    {

        private readonly IPublisher _publisher;
        private readonly Guid _projectionCorrelationId;
        private readonly IODispatcher _ioDispatcher;
        private readonly string _projectionCheckpointStreamId;
        private readonly bool _useCheckpoints;
        private readonly ILogger _logger = TraceLogger.GetLogger<CoreProjectionCheckpointReader>();

        private bool _stateRequested;

        private long _nextStateIndexToRequest;
        private ProjectionVersion _projectionVersion;
        private Guid _readRequestId;
        private long _lastWrittenCheckpointEventNumber;

        public CoreProjectionCheckpointReader(
            IPublisher publisher, Guid projectionCorrelationId, IODispatcher ioDispatcher, string projectionCheckpointStreamId, in ProjectionVersion projectionVersion, bool useCheckpoints)
        {
            _publisher = publisher;
            _projectionCorrelationId = projectionCorrelationId;
            _ioDispatcher = ioDispatcher;
            _projectionCheckpointStreamId = projectionCheckpointStreamId;
            _projectionVersion = projectionVersion;
            _useCheckpoints = useCheckpoints;
        }

        public void BeginLoadState()
        {
            if (_stateRequested) { throw new InvalidOperationException("State has been already requested"); }
            BeforeBeginLoadState();
            _stateRequested = true;
            if (_useCheckpoints)
            {
                RequestLoadState();
            }
            else
            {
                CheckpointLoaded(null, null);
            }
        }

        public void Initialize()
        {
            _ioDispatcher.BackwardReader.Cancel(_readRequestId);
            _readRequestId = Guid.Empty;
            _stateRequested = false;
        }

        protected void BeforeBeginLoadState()
        {
            _lastWrittenCheckpointEventNumber = ExpectedVersion.NoStream;
            _nextStateIndexToRequest = -1; // from the end
        }

        protected void RequestLoadState()
        {
            const int recordsToRequest = 10;
            _readRequestId = Guid.NewGuid();
            _ioDispatcher.ReadBackward(
                _projectionCheckpointStreamId, _nextStateIndexToRequest, recordsToRequest, false,
                SystemAccount.Principal, OnLoadStateReadRequestCompleted, 
                () => {
                    if (_logger.IsWarningLevelEnabled()) _logger.ReadForwardOfStreamTimedOut(_projectionCheckpointStreamId);
                    RequestLoadState();
                }, _readRequestId);
        }


        private void OnLoadStateReadRequestCompleted(ClientMessage.ReadStreamEventsBackwardCompleted message)
        {
            if (message.Events.Length > 0)
            {
                var checkpoint = message.Events.Where(v => v.Event.EventType == ProjectionEventTypes.ProjectionCheckpoint).Select(x => x.Event).FirstOrDefault();
                if (checkpoint is object)
                {
                    var parsed = checkpoint.Metadata.ParseCheckpointTagVersionExtraJson(_projectionVersion);
                    if (parsed.Version.ProjectionId != _projectionVersion.ProjectionId
                        || _projectionVersion.Epoch > parsed.Version.Version)
                    {
                        _lastWrittenCheckpointEventNumber = checkpoint.EventNumber;
                        CheckpointLoaded(null, null);
                    }
                    else
                    {
                        //TODO: check epoch and correctly set _lastWrittenCheckpointEventNumber
                        var checkpointData = Helper.UTF8NoBom.GetString(checkpoint.Data);
                        _lastWrittenCheckpointEventNumber = checkpoint.EventNumber;
                        var adjustedTag = parsed.Tag; // the same projection and epoch, handle upgrades internally
                        CheckpointLoaded(adjustedTag, checkpointData);
                    }
                    return;
                }
            }

            if (message.NextEventNumber != -1)
            {
                _nextStateIndexToRequest = message.NextEventNumber;
                RequestLoadState();
                return;
            }
            _lastWrittenCheckpointEventNumber = message.LastEventNumber;
            CheckpointLoaded(null, null);
        }


        protected void CheckpointLoaded(CheckpointTag checkpointTag, string checkpointData)
        {
            if (checkpointTag is null) // no checkpoint data found
            {
                checkpointData = null;
            }
            _publisher.Publish(
                new CoreProjectionProcessingMessage.CheckpointLoaded(
                    _projectionCorrelationId, checkpointTag, checkpointData, _lastWrittenCheckpointEventNumber));
        }


    }
}