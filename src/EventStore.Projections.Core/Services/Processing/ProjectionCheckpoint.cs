﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using EventStore.Core.Helpers;
using EventStore.Core.Messaging;
using EventStore.Projections.Core.Messages;
using EventStore.Core.Bus;
using Microsoft.Extensions.Logging;

namespace EventStore.Projections.Core.Services.Processing
{
  public class ProjectionCheckpoint : IDisposable, IEmittedStreamContainer, IEventWriter
  {
    private readonly int _maxWriteBatchLength;
    private readonly ILogger _logger;

    private readonly Dictionary<string, EmittedStream> _emittedStreams = new Dictionary<string, EmittedStream>();
    private readonly IPrincipal _runAs;
    private readonly CheckpointTag _from;
    private CheckpointTag _last;
    private readonly IProjectionCheckpointManager _readyHandler;
    private readonly PositionTagger _positionTagger;

    private bool _checkpointRequested = false;
    private int _requestedCheckpoints;
    private bool _started = false;

    private readonly IODispatcher _ioDispatcher;
    private readonly IPublisher _publisher;

    private readonly ProjectionVersion _projectionVersion;

    private List<IEnvelope> _awaitingStreams;

    private Guid[] _writeKeys;
    private int _maximumAllowedWritesInFlight;

    public ProjectionCheckpoint(
        IPublisher publisher,
        IODispatcher ioDispatcher,
        ProjectionVersion projectionVersion,
        IPrincipal runAs,
        IProjectionCheckpointManager readyHandler,
        CheckpointTag from,
        PositionTagger positionTagger,
        int maxWriteBatchLength,
        int maximumAllowedWritesInFlight,
        ILogger logger = null)
    {
      if (publisher == null) throw new ArgumentNullException(nameof(publisher));
      if (ioDispatcher == null) throw new ArgumentNullException(nameof(ioDispatcher));
      if (readyHandler == null) throw new ArgumentNullException(nameof(readyHandler));
      if (positionTagger == null) throw new ArgumentNullException(nameof(positionTagger));
      if (from.CommitPosition < from.PreparePosition) throw new ArgumentException(nameof(from));
      //NOTE: fromCommit can be equal fromPrepare on 0 position.  Is it possible anytime later? Ignoring for now.
      _maximumAllowedWritesInFlight = maximumAllowedWritesInFlight;
      _publisher = publisher;
      _ioDispatcher = ioDispatcher;
      _projectionVersion = projectionVersion;
      _runAs = runAs;
      _readyHandler = readyHandler;
      _positionTagger = positionTagger;
      _from = _last = from;
      _maxWriteBatchLength = maxWriteBatchLength;
      _logger = logger;
      _writeKeys = Enumerable.Range(0, _maximumAllowedWritesInFlight).Select(x => Guid.NewGuid()).ToArray();
    }

    public void Start()
    {
      if (_started) { throw new InvalidOperationException("Projection has been already started"); }
      _started = true;
      foreach (var stream in _emittedStreams.Values)
      {
        stream.Start();
      }
    }

    public void ValidateOrderAndEmitEvents(EmittedEventEnvelope[] events)
    {
      UpdateLastPosition(events);
      EnsureCheckpointNotRequested();

      var groupedEvents = events.GroupBy(v => v.Event.StreamId);
      foreach (var eventGroup in groupedEvents)
      {
        EmitEventsToStream(eventGroup.Key, eventGroup.ToArray());
      }
    }

    private void UpdateLastPosition(EmittedEventEnvelope[] events)
    {
      foreach (var emittedEvent in events)
      {
        if (emittedEvent.Event.CausedByTag > _last)
          _last = emittedEvent.Event.CausedByTag;
      }
    }

    private void ValidateCheckpointPosition(CheckpointTag position)
    {
      if (position <= _from)
      {
        throw new InvalidOperationException(
            $"Checkpoint position before or equal to the checkpoint start position. Requested: '{position}' Started: '{_from}'");
      }

      if (position < _last)
      {
        throw new InvalidOperationException(
            $"Checkpoint position before last handled position. Requested: '{position}' Last: '{_last}'");
      }
    }

    public void Prepare(CheckpointTag position)
    {
      if (!_started) { throw new InvalidOperationException("Projection has not been started"); }
      ValidateCheckpointPosition(position);
      _checkpointRequested = true;
      _requestedCheckpoints = 1; // avoid multiple checkpoint ready messages if already ready
      foreach (var emittedStream in _emittedStreams.Values)
      {
        _requestedCheckpoints++;
        emittedStream.Checkpoint();
      }
      _requestedCheckpoints--;
      OnCheckpointCompleted();
    }

    private void EnsureCheckpointNotRequested()
    {
      if (_checkpointRequested)
        throw new InvalidOperationException("Checkpoint requested");
    }

    private void EmitEventsToStream(string streamId, EmittedEventEnvelope[] emittedEvents)
    {
      if (string.IsNullOrEmpty(streamId)) { throw new ArgumentNullException(nameof(streamId)); }
      EmittedStream stream;
      if (!_emittedStreams.TryGetValue(streamId, out stream))
      {
        var streamMetadata = emittedEvents.Length > 0 ? emittedEvents[0].StreamMetadata : null;

        var writerConfiguration = new EmittedStream.WriterConfiguration(
            streamMetadata, _runAs, maxWriteBatchLength: _maxWriteBatchLength, logger: _logger);

        stream = new EmittedStream(
            _writeKeys[_emittedStreams.Count % _maximumAllowedWritesInFlight], streamId, writerConfiguration, _projectionVersion, _positionTagger, _from, _publisher, _ioDispatcher, this);

        if (_started)
          stream.Start();
        _emittedStreams.Add(streamId, stream);
      }
      stream.EmitEvents(emittedEvents.Select(v => v.Event).ToArray());
    }

    public void Handle(CoreProjectionProcessingMessage.ReadyForCheckpoint message)
    {
      _requestedCheckpoints--;
      OnCheckpointCompleted();
    }

    private void OnCheckpointCompleted()
    {
      if (_requestedCheckpoints == 0)
      {
        _readyHandler.Handle(new CoreProjectionProcessingMessage.ReadyForCheckpoint(this));
      }
    }

    public int GetWritePendingEvents()
    {
      return _emittedStreams.Values.Sum(v => v.GetWritePendingEvents());
    }

    public int GetWritesInProgress()
    {
      return _emittedStreams.Values.Sum(v => v.GetWritesInProgress());
    }

    public int GetReadsInProgress()
    {
      return _emittedStreams.Values.Sum(v => v.GetReadsInProgress());
    }

    public void Handle(CoreProjectionProcessingMessage.RestartRequested message)
    {
      _readyHandler.Handle(message);
    }

    public void Handle(CoreProjectionProcessingMessage.Failed message)
    {
      _readyHandler.Handle(message);
    }

    public void Dispose()
    {
      if (_emittedStreams != null)
        foreach (var stream in _emittedStreams.Values)
          stream.Dispose();
    }

    public void Handle(CoreProjectionProcessingMessage.EmittedStreamAwaiting message)
    {
      if (_awaitingStreams == null)
        _awaitingStreams = new List<IEnvelope>();
      _awaitingStreams.Add(message.Envelope);
    }

    public void Handle(CoreProjectionProcessingMessage.EmittedStreamWriteCompleted message)
    {
      var awaitingStreams = _awaitingStreams;
      _awaitingStreams = null; // still awaiting will re-register
      if (awaitingStreams != null)
        foreach (var stream in awaitingStreams)
          stream.ReplyWith(message);
    }
  }
}
