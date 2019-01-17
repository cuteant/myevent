﻿using System;
using System.Runtime.CompilerServices;

namespace EventStore.Projections.Core
{
    #region -- ExceptionArgument --

    /// <summary>The convention for this enum is using the argument name as the enum name</summary>
    internal enum ExceptionArgument
    {
        db,
        id,
        func,
        partition,
        query,
        name,
        data,
        publisher,
        ioDispatcher,
        envelope,
        state,
        causedBy,
        handlerType,
        projectionConfig,
        streams,
        causedByTag,
        queueMap,
        config,
        masterMasterWorkerId,
        querySource,
        fromPosition,
        replyEnvelope,
        persistedState_Query,
        persistedState_handlerType,
        output,
        events,
        getStateDispatcher,
        getResultDispatcher,
        maxEvents,
        tag,
        checkpointTag,
        eventTypes,
        writerConfiguration,
        @event,
        resultStream,
        upositionTaggerrl,
        sourceDefinition,
        fromCheckpointPosition,
        projectionState,
        subscriptionDispatcher,
        sequenceNumber,
        streamId,
        coreProjection,
        namingBuilder,
        stateCache,
        positionTagger,
        checkpointManager,
        emittedEventWriter,
        emittedStreamsTracker,
        stream,
        fromSequenceNumber,
        from,
        streamName,
        readyHandler,
        resultWriter,
        slaves,
        spoolProcessingResponseDispatcher,
        namesBuilder,
        readerStrategy,
        timeProvider,
        inputQueue,
        sources,
    }

    #endregion

    #region -- ExceptionResource --

    /// <summary>The convention for this enum is using the resource name as the enum name</summary>
    internal enum ExceptionResource
    {
        CannotDeleteAProjectionThatHasnotBeenStoppedOrFaulted,
        Set_Shared_StateCommandHandlerHasNotBeenRegistered,
        Set_StateCommandHandlerHasNotBeenRegistered,
    }

    #endregion

    partial class ThrowHelper
    {
        #region -- ArgumentException --

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ThrowArgumentException_NotEmptyGuid(ExceptionArgument argument)
        {
            throw GetException();
            ArgumentException GetException()
            {
                var argumentName = GetArgumentName(argument);
                return new ArgumentException($"{argumentName} should be non-empty GUID.", argumentName);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ThrowArgumentException_Equal(int expected, int actual, ExceptionArgument argument)
        {
            throw GetException();
            ArgumentException GetException()
            {
                var argumentName = GetArgumentName(argument);
                return new ArgumentException($"{argumentName} expected value: {expected}, actual value: {actual}");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ThrowArgumentException_Equal(long expected, long actual, ExceptionArgument argument)
        {
            throw GetException();
            ArgumentException GetException()
            {
                var argumentName = GetArgumentName(argument);
                return new ArgumentException($"{argumentName} expected value: {expected}, actual value: {actual}");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ThrowArgumentException_Equal(bool expected, bool actual, ExceptionArgument argument)
        {
            throw GetException();
            ArgumentException GetException()
            {
                var argumentName = GetArgumentName(argument);
                return new ArgumentException($"{argumentName} expected value: {expected}, actual value: {actual}");
            }
        }

        #endregion

        #region -- ArgumentOutOfRangeException --

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ThrowArgumentOutOfRangeException_Positive(ExceptionArgument argument)
        {
            throw GetException();
            ArgumentOutOfRangeException GetException()
            {
                var argumentName = GetArgumentName(argument);
                return new ArgumentOutOfRangeException(argumentName, $"{argumentName} should be positive.");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ThrowArgumentOutOfRangeException_Nonnegative(ExceptionArgument argument)
        {
            throw GetException();
            ArgumentOutOfRangeException GetException()
            {
                var argumentName = GetArgumentName(argument);
                return new ArgumentOutOfRangeException(argumentName, $"{argumentName} should be non negative.");
            }
        }

        #endregion
    }
}
