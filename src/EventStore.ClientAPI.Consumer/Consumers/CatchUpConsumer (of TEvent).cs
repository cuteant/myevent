﻿using System;
using System.Threading;
using System.Threading.Tasks;
using CuteAnt.AsyncEx;
using EventStore.ClientAPI.Subscriptions;
using Microsoft.Extensions.Logging;

namespace EventStore.ClientAPI.Consumers
{
  /// <summary>Represents the consumer of a catch-up subscription to EventStore: http://docs.geteventstore.com/introduction/4.0.0/subscriptions/ </summary>
  public class CatchUpConsumer<TEvent> : StreamConsumer<CatchUpSubscription<TEvent>, CatchUpSubscriptionSettings, TEvent>
    where TEvent : class
  {
    private static readonly ILogger s_logger = TraceLogger.GetLogger<VolatileConsumer>();

    private bool processingResolvedEvent;
    private Func<EventStoreCatchUpSubscription<TEvent>, ResolvedEvent<TEvent>, Task> _resolvedEventAppearedAsync;
    private Action<EventStoreCatchUpSubscription<TEvent>, ResolvedEvent<TEvent>> _resolvedEventAppeared;
    private Func<EventStoreCatchUpSubscription<TEvent>, ResolvedEvent<TEvent>, Task> _eventAppearedAsync;
    private Action<EventStoreCatchUpSubscription<TEvent>, ResolvedEvent<TEvent>> _eventAppeared;

    private EventStoreCatchUpSubscription<TEvent> esSubscription;

    protected override void OnDispose(bool disposing)
    {
      base.OnDispose(disposing);
      var subscription = Interlocked.Exchange(ref esSubscription, null);
      subscription?.Stop(TimeSpan.FromMinutes(1));
    }

    public void Initialize(IEventStoreConnectionBase2 connection, CatchUpSubscription<TEvent> subscription,
      Func<EventStoreCatchUpSubscription<TEvent>, ResolvedEvent<TEvent>, Task> resolvedEventAppearedAsync)
    {
      Initialize(connection, subscription);
      _resolvedEventAppearedAsync = resolvedEventAppearedAsync ?? throw new ArgumentNullException(nameof(resolvedEventAppearedAsync));
      processingResolvedEvent = true;
    }

    public void Initialize(IEventStoreConnectionBase2 connection, CatchUpSubscription<TEvent> subscription,
      Action<EventStoreCatchUpSubscription<TEvent>, ResolvedEvent<TEvent>> resolvedEventAppeared)
    {
      Initialize(connection, subscription);
      _resolvedEventAppeared = resolvedEventAppeared ?? throw new ArgumentNullException(nameof(resolvedEventAppeared));
      processingResolvedEvent = true;
    }

    public void Initialize(IEventStoreConnectionBase2 connection, CatchUpSubscription<TEvent> subscription, Func<TEvent, Task> eventAppearedAsync)
    {
      if (null == eventAppearedAsync) { throw new ArgumentNullException(nameof(eventAppearedAsync)); }
      Initialize(connection, subscription);
      _eventAppearedAsync = (sub, resolvedEvent) => eventAppearedAsync(resolvedEvent.Body);
      processingResolvedEvent = false;
    }

    public void Initialize(IEventStoreConnectionBase2 connection, CatchUpSubscription<TEvent> subscription, Action<TEvent> eventAppeared)
    {
      if (null == eventAppeared) { throw new ArgumentNullException(nameof(eventAppeared)); }
      Initialize(connection, subscription);
      _eventAppeared = (sub, resolvedEvent) => eventAppeared(resolvedEvent.Body);
      processingResolvedEvent = false;
    }

    public override Task ConnectToSubscriptionAsync() => ConnectToSubscriptionAsync(null);
    public override async Task ConnectToSubscriptionAsync(long? lastCheckpoint)
    {
      if (Interlocked.CompareExchange(ref _subscribed, ON, OFF) == ON) { return; }

      if (lastCheckpoint == null)
      {
        lastCheckpoint = StreamPosition.Start;
        var readResult = await Connection.ReadLastEventAsync(Subscription.StreamId, Subscription.Settings.ResolveLinkTos, Subscription.Credentials);
        if (EventReadStatus.Success == readResult.Status)
        {
          lastCheckpoint = readResult.EventNumber;
        }
      }

      try
      {
        if (processingResolvedEvent)
        {
          if (_resolvedEventAppearedAsync != null)
          {
            esSubscription = Connection.CatchUpSubscribe<TEvent>(Subscription.Topic, lastCheckpoint, Subscription.Settings, _resolvedEventAppearedAsync,
                    _ => s_logger.LogInformation($"Caught up on {_.StreamId} at {DateTime.Now}"),
                    async (sub, reason, exception) => await SubscriptionDroppedAsync(sub, reason, exception).ConfigureAwait(false),
                    Subscription.Credentials);
          }
          else
          {
            esSubscription = Connection.CatchUpSubscribe<TEvent>(Subscription.Topic, lastCheckpoint, Subscription.Settings, _resolvedEventAppeared,
                    _ => s_logger.LogInformation($"Caught up on {_.StreamId} at {DateTime.Now}"),
                    async (sub, reason, exception) => await SubscriptionDroppedAsync(sub, reason, exception).ConfigureAwait(false),
                    Subscription.Credentials);
          }
        }
        else
        {
          if (_eventAppearedAsync != null)
          {
            esSubscription = Connection.CatchUpSubscribe<TEvent>(Subscription.Topic, lastCheckpoint, Subscription.Settings, _eventAppearedAsync,
                    _ => s_logger.LogInformation($"Caught up on {_.StreamId} at {DateTime.Now}"),
                    async (sub, reason, exception) => await SubscriptionDroppedAsync(sub, reason, exception).ConfigureAwait(false),
                    Subscription.Credentials);
          }
          else
          {
            esSubscription = Connection.CatchUpSubscribe<TEvent>(Subscription.Topic, lastCheckpoint, Subscription.Settings, _eventAppeared,
                    _ => s_logger.LogInformation($"Caught up on {_.StreamId} at {DateTime.Now}"),
                    async (sub, reason, exception) => await SubscriptionDroppedAsync(sub, reason, exception).ConfigureAwait(false),
                    Subscription.Credentials);
          }
        }
      }
      catch (Exception exc)
      {
        await TaskConstants.Completed;
        s_logger.LogError(exc.ToString());
      }
    }

    private async Task SubscriptionDroppedAsync(EventStoreCatchUpSubscription<TEvent> subscription, SubscriptionDropReason dropReason, Exception exception)
    {
      if (await CanRetryAsync(subscription.ProcessingEventNumber, dropReason).ConfigureAwait(false))
      {
        var subscriptionDropped = new DroppedSubscription(Subscription, exception.Message, dropReason);
        await HandleDroppedSubscriptionAsync(subscriptionDropped).ConfigureAwait(false);
      }
    }
  }
}