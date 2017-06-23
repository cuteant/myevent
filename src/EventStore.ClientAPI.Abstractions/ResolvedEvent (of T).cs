﻿using System;

namespace EventStore.ClientAPI
{
  /// <summary>A structure representing a single event or an resolved link event.</summary>
  public struct ResolvedEvent<T> : IResolvedEvent<T> where T : class
  {
    /// <summary>The event, or the resolved link event if this <see cref="ResolvedEvent"/> is a link event.</summary>
    public readonly RecordedEvent<T> Event;

    /// <summary>The link event if this <see cref="ResolvedEvent"/> is a link event.</summary>
    public readonly RecordedEvent<T> Link;

    /// <summary>Returns the event that was read or which triggered the subscription.
    ///
    /// If this <see cref="ResolvedEvent"/> represents a link event, the Link
    /// will be the <see cref="OriginalEvent"/>, otherwise it will be the Event.</summary>
    public RecordedEvent<T> OriginalEvent => Link ?? Event;

    /// <summary>Indicates whether this <see cref="ResolvedEvent"/> is a resolved link event.</summary>
    public bool IsResolved => Link != null && Event != null;

    /// <summary>The logical position of the <see cref="OriginalEvent"/>.</summary>
    public readonly Position? OriginalPosition;

    /// <summary>The stream name of the <see cref="OriginalEvent" />.</summary>
    public string OriginalStreamId => OriginalEvent.EventStreamId;

    /// <summary>The event number in the stream of the <see cref="OriginalEvent"/>.</summary>
    public long OriginalEventNumber => OriginalEvent.EventNumber;

    Position? IResolvedEvent.OriginalPosition => OriginalPosition;
    string IResolvedEvent.OriginalEventType => OriginalEvent.EventType;
    Guid IResolvedEvent.OriginalEventId => OriginalEvent.EventId;
    IRecordedEvent<T> IResolvedEvent<T>.OriginalEvent => OriginalEvent;
    IRecordedEvent IResolvedEvent2.GetOriginalEvent() => OriginalEvent;
    IEventDescriptor IResolvedEvent2.GetDescriptor() => OriginalEvent.FullEvent.Descriptor;
    object IResolvedEvent2.GetBody() => OriginalEvent.FullEvent.Value;

    internal ResolvedEvent(RecordedEvent<T> evnt, RecordedEvent<T> link, Position? position)
    {
      Event = evnt;
      Link = link;
      OriginalPosition = position;
    }

    public static readonly ResolvedEvent<T> Null = new ResolvedEvent<T>();
  }
}