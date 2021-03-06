﻿using System;
using System.Threading.Tasks;
using EventStore.ClientAPI.Subscriptions;
using Microsoft.Extensions.Logging;
using Polly;

namespace EventStore.ClientAPI.Resilience
{
    /// <summary>Defines the policy for dealing with dropped subscriptions.</summary>
    internal static class DroppedSubscriptionPolicy
    {
        private static readonly ILogger log = TraceLogger.GetLogger(typeof(DroppedSubscriptionPolicy));

        /// <summary>Handles a dropped subscription according to a specified policy.</summary>
        /// <param name="subscription">The stream to which the subscription was dropped.</param>
        /// <param name="compensatingAction">The action to take in order to handle the subscription being dropped.</param>
        /// <param name="retryPolicy"></param>
        /// <returns>An asynchronous Task.</returns>
        public static async Task Handle(DroppedSubscription subscription, Func<Task> compensatingAction, RetryPolicy retryPolicy)
        {
            var error = subscription.Error;
            var message = error?.Message;
            try
            {
                switch (subscription.DropReason)
                {
                    case SubscriptionDropReason.UserInitiated:
                        //the client called Close() on the subscription
                        if (log.IsInformationLevelEnabled())
                        {
                            log.SubscriptionWasClosedByTheClient(subscription.StreamId, message);
                        }
                        break;
                    case SubscriptionDropReason.NotAuthenticated:
                        //the client is not authenticated -> check ACL
                        log.LogError($@"Subscription to {subscription.StreamId} was dropped because the client could not be authenticated. {Environment.NewLine}Check the Access Control List. {message}");
                        break;
                    case SubscriptionDropReason.AccessDenied:
                        //access to the stream was denied -> check ACL
                        log.LogError($@"Subscription to {subscription.StreamId} was dropped because the client was denied access. {Environment.NewLine}Check the Access Control List. {message}");
                        break;
                    case SubscriptionDropReason.SubscribingError:
                        //something went wrong while subscribing - retry
                        log.LogError($@"Subscription to {subscription.StreamId} failed.{Environment.NewLine}{message}");
                        await RetrySubscriptionAsync(compensatingAction, retryPolicy).ConfigureAwait(false);
                        break;
                    case SubscriptionDropReason.ServerError:
                        //error on the server
                        log.LogError($@"A server error occurred which dropped the subscription to {subscription.StreamId} {Environment.NewLine}{message}");
                        break;
                    case SubscriptionDropReason.ConnectionClosed:
                        //the connection was closed - retry
                        log.LogError($@"Subscription to {subscription.StreamId} was dropped due to the connection being closed. {Environment.NewLine}{message}");
                        if (!(error is ObjectDisposedException))
                        {
                            await RetrySubscriptionAsync(compensatingAction, retryPolicy).ConfigureAwait(false);
                        }
                        break;
                    case SubscriptionDropReason.CatchUpError:
                        //an error occurred during the catch-up phase - retry
                        log.LogError($@"Subscription to {subscription.StreamId} was dropped during the catch-up phase. {Environment.NewLine}{message}");
                        if (!(error is ObjectDisposedException))
                        {
                            await RetrySubscriptionAsync(compensatingAction, retryPolicy).ConfigureAwait(false);
                        }
                        break;
                    case SubscriptionDropReason.ProcessingQueueOverflow:
                        //occurs when the number of events on the push buffer exceed the specified maximum - retry
                        log.LogWarning($@"Subscription to {subscription.StreamId} was dropped due to a processing buffer overflow. {Environment.NewLine}{message}");
                        await RetrySubscriptionAsync(compensatingAction, retryPolicy).ConfigureAwait(false);
                        break;
                    case SubscriptionDropReason.EventHandlerException:
                        //Subscription dropped because an exception was thrown by one of our handlers.
                        log.LogError($@"Subscription to {subscription.StreamId} was dropped in response to a handler exception. {Environment.NewLine}{message}");
                        if (!(error is ObjectDisposedException))
                        {
                            await RetrySubscriptionAsync(compensatingAction, retryPolicy).ConfigureAwait(false);
                        }
                        break;
                    case SubscriptionDropReason.MaxSubscribersReached:
                        //The maximum number of subscribers for the persistent subscription has been reached
                        log.LogError($@"Subscription to {subscription.StreamId} was dropped because the maximum no. of subscribers was reached. {Environment.NewLine}{message}");
                        break;
                    case SubscriptionDropReason.PersistentSubscriptionDeleted:
                        //The persistent subscription has been deleted
                        log.LogError($@"The persistent subscription to {subscription.StreamId} was dropped because it was deleted. {Environment.NewLine}{message}");
                        break;
                    case SubscriptionDropReason.Unknown:
                        //Scoobied
                        log.LogError($@"Subscription to {subscription.StreamId} was dropped for an unspecified reason. {Environment.NewLine}{message}");
                        break;
                    case SubscriptionDropReason.NotFound:
                        //Target of persistent subscription was not found. Needs to be created first
                        log.LogError($@"The persistent subscription to {subscription.StreamId} could not be found. {Environment.NewLine}{message}");
                        break;
                    default:
                        ConsumerThrowHelper.ThrowArgumentOutOfRangeException(subscription.DropReason); break;
                }
            }
            catch (Exception exc)
            {
                log.LogError(exc.ToString());
            }
        }

        private static Task RetrySubscriptionAsync(Func<Task> compensatingAction, RetryPolicy retryPolicy)
        {
            if (retryPolicy.MaxNoOfRetries == RetryPolicy.Unbounded)
            {
                switch (retryPolicy.ProviderType)
                {
                    case SleepDurationProviderType.FixedDuration:
                    case SleepDurationProviderType.ExponentialDuration:
                        return Policy
                            .Handle<Exception>(ex => !(ex is ObjectDisposedException)) // EcConnnection has been closed
                            .WaitAndRetryForeverAsync(retryPolicy.SleepDurationProvider)
                            .ExecuteAsync(compensatingAction);
                    case SleepDurationProviderType.Immediately:
                    default:
                        return Policy
                            .Handle<Exception>(ex => !(ex is ObjectDisposedException)) // EcConnnection has been closed
                            .RetryForeverAsync()
                            .ExecuteAsync(compensatingAction);
                }
            }
            else
            {
                switch (retryPolicy.ProviderType)
                {
                    case SleepDurationProviderType.FixedDuration:
                    case SleepDurationProviderType.ExponentialDuration:
                        return Policy
                            .Handle<Exception>(ex => !(ex is ObjectDisposedException)) // EcConnnection has been closed
                            .WaitAndRetryAsync(retryPolicy.MaxNoOfRetries, retryPolicy.SleepDurationProvider)
                            .ExecuteAsync(compensatingAction);
                    case SleepDurationProviderType.Immediately:
                    default:
                        return Policy
                            .Handle<Exception>(ex => !(ex is ObjectDisposedException)) // EcConnnection has been closed
                            .RetryAsync(retryPolicy.MaxNoOfRetries)
                            .ExecuteAsync(compensatingAction);
                }
            }
        }
    }
}
