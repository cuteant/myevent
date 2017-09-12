﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Helpers;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.AwakeReaderService;
using EventStore.Core.Services.TimerService;
using EventStore.Projections.Core.Messages;
using EventStore.Projections.Core.Messaging;
using EventStore.Core.Settings;

namespace EventStore.Projections.Core.Services.Processing
{
  public class MultiStreamEventReader : EventReader,
      IHandle<ClientMessage.ReadStreamEventsForwardCompleted>,
      IHandle<ProjectionManagementMessage.Internal.ReadTimeout>
  {
    private readonly HashSet<string> _streams;
    private CheckpointTag _fromPositions;
    private Dictionary<string, bool> _firstEventProcessed = new Dictionary<string, bool>(StringComparer.Ordinal);
    private readonly bool _resolveLinkTos;
    private readonly ITimeProvider _timeProvider;

    private readonly HashSet<string> _eventsRequested = new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<string, long?> _preparePositions = new Dictionary<string, long?>(StringComparer.Ordinal);

    // event, link, progress
    // null element in a queue means tream deleted 
    private readonly Dictionary<string, Queue<Tuple<EventStore.Core.Data.ResolvedEvent, float>>> _buffers =
        new Dictionary<string, Queue<Tuple<EventStore.Core.Data.ResolvedEvent, float>>>(StringComparer.Ordinal);

    private const int _maxReadCount = 111;
    private long? _safePositionToJoin;
    private readonly Dictionary<string, bool> _eofs;
    private int _deliveredEvents;
    private long _lastPosition;

    private readonly Dictionary<string, Guid> _pendingRequests;
    private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();

    public MultiStreamEventReader(
      IODispatcher ioDispatcher, IPublisher publisher, Guid eventReaderCorrelationId, IPrincipal readAs, int phase,
      string[] streams, Dictionary<string, long> fromPositions, bool resolveLinkTos, ITimeProvider timeProvider,
      bool stopOnEof = false, int? stopAfterNEvents = null)
      : base(publisher, eventReaderCorrelationId, readAs, stopOnEof)
    {
      if (streams == null) throw new ArgumentNullException(nameof(streams));
      if (timeProvider == null) throw new ArgumentNullException(nameof(timeProvider));
      if (streams.Length == 0) throw new ArgumentException(nameof(streams));

      _streams = new HashSet<string>(streams, StringComparer.Ordinal);
      _eofs = _streams.ToDictionary(v => v, v => false);
      var positions = CheckpointTag.FromStreamPositions(phase, fromPositions);
      ValidateTag(positions);
      _fromPositions = positions;
      _resolveLinkTos = resolveLinkTos;
      _timeProvider = timeProvider;
      _pendingRequests = new Dictionary<string, Guid>(StringComparer.Ordinal);
      foreach (var stream in streams)
      {
        _firstEventProcessed.Add(stream, false);
        _pendingRequests.Add(stream, Guid.Empty);
        _preparePositions.Add(stream, null);
      }
    }

    private void ValidateTag(CheckpointTag fromPositions)
    {
      if (_streams.Count != fromPositions.Streams.Count)
      {
        throw new ArgumentException("Number of streams does not match", "fromPositions");
      }

      foreach (var stream in _streams)
      {
        if (!fromPositions.Streams.ContainsKey(stream))
        {
          throw new ArgumentException($"The '{stream}' stream position has not been set", "fromPositions");
        }
      }
    }

    protected override void RequestEvents()
    {
      if (PauseRequested || Paused) { return; }

      if (_eofs.Any(v => v.Value))
      {
        _publisher.Publish(
            TimerMessage.Schedule.Create(
                TimeSpan.FromMilliseconds(250), new PublishEnvelope(_publisher, crossThread: true),
                new UnwrapEnvelopeMessage(ProcessBuffers2)));
      }

      foreach (var stream in _streams)
      {
        RequestEvents(stream, delay: _eofs[stream]);
      }
    }

    private void ProcessBuffers2()
    {
      ProcessBuffers();
      CheckIdle();
    }

    protected override bool AreEventsRequested()
    {
      return _eventsRequested.Count != 0;
    }

    public void Handle(ClientMessage.ReadStreamEventsForwardCompleted message)
    {
      if (_disposed) { return; }
      if (!_streams.Contains(message.EventStreamId))
      {
        throw new InvalidOperationException($"Invalid stream name: {message.EventStreamId}");
      }
      if (!_eventsRequested.Contains(message.EventStreamId))
      {
        throw new InvalidOperationException("Read events has not been requested");
      }
      if (Paused) { throw new InvalidOperationException("Paused"); }

      using (var token = _lock.CreateToken(true))
      {
        if (!_pendingRequests.Values.Any(x => x == message.CorrelationId)) { return; }
      }

      _lastPosition = message.TfLastCommitPosition;
      switch (message.Result)
      {
        case ReadStreamResult.StreamDeleted:
        case ReadStreamResult.NoStream:
          _eofs[message.EventStreamId] = true;
          UpdateSafePositionToJoin(message.EventStreamId, MessageToLastCommitPosition(message));
          if (message.Result == ReadStreamResult.StreamDeleted || (message.Result == ReadStreamResult.NoStream && message.LastEventNumber >= 0))
          {
            EnqueueItem(null, message.EventStreamId);
          }
          ProcessBuffers();
          _eventsRequested.Remove(message.EventStreamId);
          PauseOrContinueProcessing();
          CheckIdle();
          CheckEof();
          break;
        case ReadStreamResult.Success:
          if (message.Events.Length == 0)
          {
            // the end
            _eofs[message.EventStreamId] = true;
            UpdateSafePositionToJoin(message.EventStreamId, MessageToLastCommitPosition(message));
            CheckIdle();
            CheckEof();
          }
          else
          {
            _eofs[message.EventStreamId] = false;

            if(!_firstEventProcessed[message.EventStreamId])
            {
              _firstEventProcessed[message.EventStreamId] = true;
              //if events have been deleted from the beginning of a stream, update the stream position to the current event number
              if(message.Events[0].OriginalEventNumber > _fromPositions.Streams[message.EventStreamId])
              {
                  _fromPositions = _fromPositions.UpdateStreamPosition(message.EventStreamId, message.Events[0].OriginalEventNumber);
              }
            }

            for (int index = 0; index < message.Events.Length; index++)
            {
              var @event = message.Events[index].Event;
              var @link = message.Events[index].Link;
              EventRecord positionEvent = (link ?? @event);
              UpdateSafePositionToJoin(positionEvent.EventStreamId, EventPairToPosition(message.Events[index]));
              var itemToEnqueue = Tuple.Create(message.Events[index], 100.0f * (link ?? @event).EventNumber / message.LastEventNumber);
              EnqueueItem(itemToEnqueue, positionEvent.EventStreamId);
            }
          }

          ProcessBuffers();
          _eventsRequested.Remove(message.EventStreamId);
          PauseOrContinueProcessing();
          break;
        case ReadStreamResult.AccessDenied:
          SendNotAuthorized();
          return;
        default:
          throw new NotSupportedException($"ReadEvents result code was not recognized. Code: {message.Result}");
      }
    }

    public void Handle(ProjectionManagementMessage.Internal.ReadTimeout message)
    {
      if (_disposed) { return; }
      if (Paused) { return; }
      using (var token = _lock.CreateToken(true))
      {
        if (!_pendingRequests.Values.Any(x => x == message.CorrelationId)) { return; }
      }

      _eventsRequested.Remove(message.StreamId);
      PauseOrContinueProcessing();
    }

    private void EnqueueItem(Tuple<EventStore.Core.Data.ResolvedEvent, float> itemToEnqueue, string streamId)
    {
      Queue<Tuple<EventStore.Core.Data.ResolvedEvent, float>> queue;
      if (!_buffers.TryGetValue(streamId, out queue))
      {
        queue = new Queue<Tuple<EventStore.Core.Data.ResolvedEvent, float>>();
        _buffers.Add(streamId, queue);
      }
      //TODO: progress calculation below is incorrect.  sum(current)/sum(last_event) where sum by all streams
      queue.Enqueue(itemToEnqueue);
    }

    private void CheckEof()
    {
      if (_eofs.All(v => v.Value)) { SendEof(); }
    }

    private void CheckIdle()
    {
      if (_eofs.All(v => v.Value))
      {
        _publisher.Publish(new ReaderSubscriptionMessage.EventReaderIdle(EventReaderCorrelationId, _timeProvider.Now));
      }
    }

    private void ProcessBuffers()
    {
      if (_disposed) { return; }
      if (_safePositionToJoin == null) { return; }
      while (true)
      {
        var anyNonEmpty = false;

        var anyEvent = false;
        var minStreamId = "";
        var minPosition = GetMaxPosition();

        var anyDeletedStream = false;
        var deletedStreamId = "";

        foreach (var buffer in _buffers)
        {
          if (buffer.Value.Count == 0) { continue; }
          anyNonEmpty = true;
          var head = buffer.Value.Peek();

          var currentStreamId = buffer.Key;

          if (head != null)
          {
            var itemPosition = GetItemPosition(head);
            if (_safePositionToJoin != null
                && itemPosition.CompareTo(_safePositionToJoin.GetValueOrDefault()) <= 0
                && itemPosition.CompareTo(minPosition) < 0)
            {
              minPosition = itemPosition;
              minStreamId = currentStreamId;
              anyEvent = true;
            }
          }
          else
          {
            anyDeletedStream = true;
            deletedStreamId = currentStreamId;
          }
        }
        if (!anyEvent && !anyDeletedStream)
        {
          if (!anyNonEmpty) { DeliverSafePositionToJoin(); }
          break;
        }

        if (anyEvent)
        {
          var minHead = _buffers[minStreamId].Dequeue();
          DeliverEvent(minHead.Item1, minHead.Item2);

          if (_buffers[minStreamId].Count == 0) { PauseOrContinueProcessing(); }
        }

        if (anyDeletedStream)
        {
          _buffers[deletedStreamId].Dequeue();
          SendPartitionDeleted_WhenReadingDataStream(deletedStreamId, -1, null, null, null, null);
        }
      }
    }

    private void RequestEvents(string stream, bool delay)
    {
      if (_disposed) { throw new InvalidOperationException("Disposed"); }
      if (PauseRequested || Paused)
      {
        throw new InvalidOperationException("Paused or pause requested");
      }

      if (_eventsRequested.Contains(stream)) { return; }
      if (_buffers.TryGetValue(stream, out Queue<Tuple<EventStore.Core.Data.ResolvedEvent, float>> queue) && queue.Count > 0)
      {
        return;
      }
      _eventsRequested.Add(stream);

      var pendingRequestCorrelationId = Guid.NewGuid();
      using (var token = _lock.CreateToken())
      {
        _pendingRequests[stream] = pendingRequestCorrelationId;
      }
      var readEventsForward = new ClientMessage.ReadStreamEventsForward(
          Guid.NewGuid(), pendingRequestCorrelationId, new SendToThisEnvelope(this), stream, _fromPositions.Streams[stream],
          _maxReadCount, _resolveLinkTos, false, null, ReadAs);
      if (delay)
      {
        _publisher.Publish(
            new AwakeServiceMessage.SubscribeAwake(
                new PublishEnvelope(_publisher, crossThread: true), Guid.NewGuid(), null,
                new TFPos(_lastPosition, _lastPosition), CreateReadTimeoutMessage(pendingRequestCorrelationId, stream)));
        _publisher.Publish(
            new AwakeServiceMessage.SubscribeAwake(
                new PublishEnvelope(_publisher, crossThread: true), Guid.NewGuid(), null,
                new TFPos(_lastPosition, _lastPosition), readEventsForward));
      }
      else
      {
        _publisher.Publish(readEventsForward);
        ScheduleReadTimeoutMessage(pendingRequestCorrelationId, stream);
      }
    }

    private void ScheduleReadTimeoutMessage(Guid correlationId, string streamId)
    {
      _publisher.Publish(CreateReadTimeoutMessage(correlationId, streamId));
    }

    private Message CreateReadTimeoutMessage(Guid correlationId, string streamId)
    {
      return TimerMessage.Schedule.Create(
          TimeSpan.FromMilliseconds(ESConsts.ReadRequestTimeout),
          new SendToThisEnvelope(this),
          new ProjectionManagementMessage.Internal.ReadTimeout(correlationId, streamId));
    }

    private void DeliverSafePositionToJoin()
    {
      if (_stopOnEof || _safePositionToJoin == null) { return; }
      // deliver if already available
      _publisher.Publish(
          new ReaderSubscriptionMessage.CommittedEventDistributed(
              EventReaderCorrelationId, null, PositionToSafeJoinPosition(_safePositionToJoin), 100.0f, source: this.GetType()));
    }

    private void UpdateSafePositionToJoin(string streamId, long? preparePosition)
    {
      _preparePositions[streamId] = preparePosition;
      if (_preparePositions.All(v => v.Value != null))
      {
        _safePositionToJoin = _preparePositions.Min(v => v.Value.GetValueOrDefault());
      }
    }

    private void DeliverEvent(EventStore.Core.Data.ResolvedEvent pair, float progress)
    {
      _deliveredEvents++;
      var positionEvent = pair.OriginalEvent;
      string streamId = positionEvent.EventStreamId;
      long fromPosition = _fromPositions.Streams[streamId];
      if (positionEvent.EventNumber != fromPosition)
      {
        throw new InvalidOperationException(
            $"Event number {fromPosition} was expected in the stream {streamId}, but event number {positionEvent.EventNumber} was received");
      }

      _fromPositions = _fromPositions.UpdateStreamPosition(streamId, positionEvent.EventNumber + 1);
      _publisher.Publish(
          //TODO: publish both link and event data
          new ReaderSubscriptionMessage.CommittedEventDistributed(
              EventReaderCorrelationId, new ResolvedEvent(pair, null),
              _stopOnEof ? (long?)null : positionEvent.LogPosition, progress, source: this.GetType()));
    }

    private long? EventPairToPosition(EventStore.Core.Data.ResolvedEvent resolvedEvent)
    {
      return resolvedEvent.OriginalEvent.LogPosition;
    }

    private long? MessageToLastCommitPosition(ClientMessage.ReadStreamEventsForwardCompleted message)
    {
      return GetLastCommitPositionFrom(message);
    }

    private long GetItemPosition(Tuple<EventStore.Core.Data.ResolvedEvent, float> head)
    {
      return head.Item1.OriginalEvent.LogPosition;
    }

    private long GetMaxPosition()
    {
      return long.MaxValue;
    }

    private long? PositionToSafeJoinPosition(long? safePositionToJoin)
    {
      return safePositionToJoin;
    }
  }
}
