﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using CuteAnt.AsyncEx;
using Microsoft.Extensions.Logging;

namespace EventStore.ClientAPI
{
    // The idea of DefaultHandlerCollection is from EasyNetQ
    // https://github.com/EasyNetQ/EasyNetQ/blob/master/Source/EasyNetQ/Consumer/HandlerCollection.cs
    internal sealed class DefaultHandlerCollection : IHandlerCollection
    {
        private static readonly Func<IResolvedEvent2, Task> s_emptyHandler = iEvent => TaskConstants.Completed;

        private readonly object _thisLock = new object();
        private readonly IDictionary<Type, Func<IResolvedEvent2, Task>> _handlers;
        private readonly HashSet<Type> _noMatching;

        private static readonly ILogger s_logger = TraceLogger.GetLogger<DefaultHandlerCollection>();
        private bool _throwOnNoMatchingHandler;

        public DefaultHandlerCollection(bool throwOnNoMatchingHandler = false)
        {
            _throwOnNoMatchingHandler = throwOnNoMatchingHandler;
            _handlers = new Dictionary<Type, Func<IResolvedEvent2, Task>>();
            _noMatching = new HashSet<Type>();
        }

        public IHandlerRegistration Add<TEvent>(Func<IResolvedEvent<TEvent>, Task> handler)
        {
            if (handler is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.handler); }

            if (_handlers.ContainsKey(typeof(TEvent)))
            {
                CoreThrowHelper.ThrowEventStoreHandlerException<TEvent>();
            }

            _handlers.Add(typeof(TEvent), (iEvent) => handler((IResolvedEvent<TEvent>)iEvent));
            return this;
        }

        public IHandlerRegistration Add<TEvent>(Action<IResolvedEvent<TEvent>> handler)
        {
            if (handler is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.handler); }

            Add<TEvent>(async iEvent =>
            {
                handler(iEvent);
                await TaskConstants.Completed;
            });
            return this;
        }

        public Func<IResolvedEvent<TEvent>, Task> GetHandler<TEvent>()
        {
            return GetHandler(typeof(TEvent));
        }

        public Func<IResolvedEvent2, Task> GetHandler(Type eventType)
        {
            if (_handlers.TryGetValue(eventType, out Func<IResolvedEvent2, Task> func)) { return func; }

            return GetHandlerInternal(eventType);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public Func<IResolvedEvent2, Task> GetHandlerInternal(Type eventType)
        {
            if (!_throwOnNoMatchingHandler)
            {
                lock (_thisLock)
                {
                    var hander = Match(eventType);
                    if (hander is object) { return hander; }
                    _handlers.Add(eventType, s_emptyHandler);
                    if (s_logger.IsWarningLevelEnabled()) { s_logger.NoHandlerFoundForEventTypeTheDefaultHanderHasBeenUsed(eventType); }
                    return s_emptyHandler;
                }
            }
            else
            {
                if (!_noMatching.Contains(eventType))
                {
                    lock (_thisLock)
                    {
                        var hander = Match(eventType);
                        if (hander is object) { return hander; }
                        _noMatching.Add(eventType);
                    }
                }
                s_logger.NoHandlerFoundForEventType(eventType);
                CoreThrowHelper.ThrowEventStoreHandlerException(eventType); return null;
            }
        }

        private Func<IResolvedEvent2, Task> Match(Type eventType)
        {
            if (_handlers.TryGetValue(eventType, out var func)) { return func; }

            // no exact handler match found, so let's see if we can find a handler that
            // handles a supertype of the consumed event.
            var handlerType = _handlers.Keys.FirstOrDefault(type => type.IsAssignableFrom(eventType));
            if (handlerType is object)
            {
                var hander = _handlers[handlerType];
                _handlers.Add(eventType, hander);
                return hander;
            }
            return null;
        }

        public bool ThrowOnNoMatchingHandler { get => _throwOnNoMatchingHandler; set => _throwOnNoMatchingHandler = value; }
    }
}