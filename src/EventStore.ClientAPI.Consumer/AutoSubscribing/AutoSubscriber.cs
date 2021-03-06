﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CuteAnt;
using CuteAnt.Collections;
using CuteAnt.Pool;
using CuteAnt.Reflection;
using EventStore.ClientAPI.Consumers;
using EventStore.ClientAPI.Subscriptions;
using Grace.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EventStore.ClientAPI.AutoSubscribing
{
    /// <summary>Lets you scan assemblies for implementations of <see cref="IAutoSubscriberConsume{T}"/> so that
    /// these will get registrered as subscribers in the bus.</summary>
    public class AutoSubscriber : IDisposable
    {
        #region @@ Fields @@

        private static readonly ILogger s_logger = TraceLogger.GetLogger<AutoSubscriber>();

        private static readonly ISet<string> s_emptySubscribingTopics;
        private static readonly List<(Type interfaceType, string consumeMethodName, bool isGenericType)> s_consumerInterfaceTypeInfos;
        //private Dictionary<Type, List<AutoSubscriberConsumerInfo>> _consumerInfos = new Dictionary<Type, List<AutoSubscriberConsumerInfo>>();

        private List<IStreamConsumer> _streamConsumers = new List<IStreamConsumer>();
        //private readonly ConcurrentHashSet<Type> _registeredConsumerTypes = new ConcurrentHashSet<Type>();

        private readonly CachedReadConcurrentDictionary<Type, object> _concreteConsumers = new CachedReadConcurrentDictionary<Type, object>(DictionaryCacheConstants.SIZE_SMALL);
        private readonly CachedReadConcurrentDictionary<Type, IStreamConsumerGenerator> _streamConsumerGenerators = new CachedReadConcurrentDictionary<Type, IStreamConsumerGenerator>(DictionaryCacheConstants.SIZE_SMALL);

        private const int ON = 1;
        private const int OFF = 0;
        private int _subscribed;

        #endregion

        #region @@ Constructors @@

        static AutoSubscriber()
        {
            s_emptySubscribingTopics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            s_consumerInterfaceTypeInfos = new List<(Type interfaceType, string consumeMethodName, bool isGenericType)>(new[]
            {
                (typeof(IAutoSubscriberConsume<>), nameof(IAutoSubscriberConsume<string>.Consume) , true),
                (typeof(IAutoSubscriberConsumeAsync<>), nameof(IAutoSubscriberConsumeAsync<string>.ConsumeAsync), true),

                (typeof(IAutoSubscriberConsumerRegistration), nameof(IAutoSubscriberConsumerRegistration.RegisterConsumers), false),
                (typeof(IAutoSubscriberHandlerRegistration), nameof(IAutoSubscriberHandlerRegistration.RegisterHandlers), false),

                (typeof(IAutoSubscriberCatchUpConsume), nameof(IAutoSubscriberCatchUpConsume.Consume), false),
                (typeof(IAutoSubscriberCatchUpConsume<>), nameof(IAutoSubscriberCatchUpConsume<string>.Consume), true),
                (typeof(IAutoSubscriberCatchUpConsumeAsync), nameof(IAutoSubscriberCatchUpConsumeAsync.ConsumeAsync), false),
                (typeof(IAutoSubscriberCatchUpConsumeAsync<>), nameof(IAutoSubscriberCatchUpConsumeAsync<string>.ConsumeAsync), true),

                (typeof(IAutoSubscriberPersistentConsume), nameof(IAutoSubscriberPersistentConsume.Consume), false),
                (typeof(IAutoSubscriberPersistentConsume<>), nameof(IAutoSubscriberPersistentConsume<string>.Consume), true),
                (typeof(IAutoSubscriberPersistentConsumeAsync), nameof(IAutoSubscriberPersistentConsumeAsync.ConsumeAsync), false),
                (typeof(IAutoSubscriberPersistentConsumeAsync<>), nameof(IAutoSubscriberPersistentConsumeAsync<string>.ConsumeAsync), true),

                (typeof(IAutoSubscriberVolatileConsume), nameof(IAutoSubscriberVolatileConsume.Consume), false),
                (typeof(IAutoSubscriberVolatileConsume<>), nameof(IAutoSubscriberVolatileConsume<string>.Consume), true),
                (typeof(IAutoSubscriberVolatileConsumeAsync), nameof(IAutoSubscriberVolatileConsumeAsync.ConsumeAsync), false),
                (typeof(IAutoSubscriberVolatileConsumeAsync<>), nameof(IAutoSubscriberVolatileConsumeAsync<string>.ConsumeAsync), true),
            });
        }

        /// <summary>Responsible for setting subscription configuration for all auto subscribed consumers.</summary>
        public AutoSubscriber(IEventStoreBus bus, string subscriptionIdPrefix, bool subscribingAllTopics = true)
        {
            if (string.IsNullOrWhiteSpace(subscriptionIdPrefix)) { throw new ArgumentNullException("You need to specify a SubscriptionId prefix, which will be used as part of the checksum of all generated subscription ids."); }

            this.Bus = bus ?? throw new ArgumentNullException(nameof(bus));
            SubscriptionIdPrefix = subscriptionIdPrefix;
            SubscribingAllTopics = subscribingAllTopics;
            _onlySubscribingTopics = s_emptySubscribingTopics;

            GenerateSubscriptionId = DefaultSubscriptionIdGenerator;
        }

        #endregion

        #region @@ Properties @@

        protected readonly IEventStoreBus Bus;

        // 如果 SubscribingAllTopics 值 false，OnlySubscribingTopics 也没有设置任何 topics，也不做订阅，防止重复订阅
        private ISet<string> _onlySubscribingTopics;
        protected ISet<string> OnlySubscribingTopics => _onlySubscribingTopics;

        /// <summary>Used when generating the unique SubscriptionId checksum.</summary>
        public string SubscriptionIdPrefix { get; }

        /// <summary>SubscribingAllTopics，default: true</summary>
        public bool SubscribingAllTopics { get; }

        private IServiceProvider _services;
        /// <summary>Services</summary>
        public IServiceProvider Services { get => _services; set => Interlocked.CompareExchange(ref _services, value, null); }

        private IInjectionScope _injectionScope;
        /// <summary>InjectionScope</summary>
        public IInjectionScope InjectionScope { get => _injectionScope; set => Interlocked.CompareExchange(ref _injectionScope, value, null); }

        /// <summary>Responsible for generating SubscriptionIds, when you use
        /// <see cref="IAutoSubscriberConsume{T}"/>, since it does not let you specify specific SubscriptionIds.
        /// Message type and SubscriptionId is the key; which if two
        /// equal keys exists, you will get round robin consumption of messages.</summary>
        public Func<AutoSubscriberConsumerInfo, string> GenerateSubscriptionId { protected get; set; }

        #endregion

        #region -- IDisposable Members --

        public void Dispose()
        {
            var streamConsumers = Interlocked.Exchange(ref _streamConsumers, new List<IStreamConsumer>());
            foreach (var item in streamConsumers)
            {
                item?.Dispose();
            }
            streamConsumers.Clear();
        }

        #endregion

        #region -- SetOnlySubscribingTopics --

        /// <summary>SetOnlySubscribingTopics</summary>
        /// <param name="topics"></param>
        public void SetOnlySubscribingTopics(params string[] topics)
        {
            if (topics is null || !topics.Any()) { return; }
            _onlySubscribingTopics = new HashSet<string>(topics, StringComparer.OrdinalIgnoreCase);
        }

        #endregion

        #region -- RegisterAssemblies --

        /// <summary>Registers all consumers in passed assembly. The SubscriptionId per consumer
        /// method is determined by <seealso cref="GenerateSubscriptionId"/> or if the method
        /// is marked with <see cref="AutoSubscriberConsumerAttribute"/> with a custom SubscriptionId.</summary>
        /// <param name="assemblies">The assemblies to scan for consumers.</param>
        public void RegisterAssemblies(params Assembly[] assemblies)
        {
            if (assemblies is null || !assemblies.Any()) { throw new ArgumentException("No assemblies specified.", nameof(assemblies)); }

            RegisterConsumerTypes(assemblies.SelectMany(asm => TypeUtils.GetTypes(asm, t => true)).ToArray());
        }

        #endregion

        #region -- RegisterConsumerTypes --

        /// <summary>Registers all types as consumers. The SubscriptionId per consumer
        /// method is determined by <seealso cref="GenerateSubscriptionId"/> or if the method
        /// is marked with <see cref="AutoSubscriberConsumerAttribute"/> with a custom SubscriptionId.</summary>
        /// <param name="consumerTypes">the types to register as consumers.</param>
        public virtual void RegisterConsumerTypes(params Type[] consumerTypes)
        {
            if (consumerTypes is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.consumerTypes); }

            foreach (var (interfaceType, consumeMethodName, isGenericType) in s_consumerInterfaceTypeInfos)
            {
                var subscriptionInfos = GetSubscriptionInfos(consumerTypes, interfaceType, consumeMethodName, isGenericType);
                foreach (var kv in subscriptionInfos)
                {
                    foreach (var subscriptionInfo in kv.Value)
                    {
                        _streamConsumers.AddRange(CreateStreamConsumer(subscriptionInfo));
                    }
                }
            }
        }

        #endregion

        #region -- ConnectToSubscriptionsAsync --

        public async Task ConnectToSubscriptionsAsync()
        {
            if (Interlocked.CompareExchange(ref _subscribed, ON, OFF) == ON) { return; }

            foreach (var item in _streamConsumers)
            {
                await item.ConnectToSubscriptionAsync();
                await Task.Delay(100);
            }
        }

        #endregion

        #region ++ CreateStreamConsumer ++

        protected IEnumerable<IStreamConsumer> CreateStreamConsumer(AutoSubscriberConsumerInfo consumerInfo)
        {
            var consumeMethod = GetConsumeMethod(consumerInfo);
            if (consumeMethod is null)
            {
                // TODO logging
                return EmptyArray<IStreamConsumer>.Instance;
            }

            var topics = GetTopAttributeValues(consumerInfo, consumeMethod);
            if (topics.Count <= 0)
            {
                return new[] { CreateStreamConsumer(consumerInfo, consumeMethod) };
            }
            else
            {
                var list = new List<IStreamConsumer>();
                foreach (var topic in topics)
                {
                    // 如果 SubscribingAllTopics 值 false，OnlySubscribingTopics 也没有设置任何 topics，也不做订阅，防止重复订阅
                    if (!SubscribingAllTopics && !_onlySubscribingTopics.Contains(topic)) { continue; }
                    list.Add(CreateStreamConsumer(consumerInfo, consumeMethod, topic));
                }
                return list;
            }
        }

        private IStreamConsumer CreateStreamConsumer(AutoSubscriberConsumerInfo consumerInfo, MethodInfo consumeMethod, string topic = null)
        {
            var autoSubscriberConsumerAttr = GetCustomAttribute<AutoSubscriberConsumerAttribute>(consumerInfo, consumeMethod);
            if (autoSubscriberConsumerAttr is null)
            {
                autoSubscriberConsumerAttr = new AutoSubscriberConsumerAttribute { Subscription = SubscriptionType.Persistent };
            }

            var interfaceType = consumerInfo.InterfaceType;
            var concreteConsumer = GetConcreteConsumer(consumerInfo.ConcreteType);
            var topics = GetTopAttributeValues(consumerInfo, consumeMethod);
            var isGenericType = interfaceType.IsGenericType;

            #region IAutoSubscriberConsume<>

            if (isGenericType && interfaceType.GetGenericTypeDefinition() == typeof(IAutoSubscriberConsume<>))
            {
                var consumerGenerator = GetStreamConsumerGenerator(consumerInfo.MessageType);
                return consumerGenerator.CreateConsumer(autoSubscriberConsumerAttr.Subscription, consumerInfo, consumeMethod, concreteConsumer, topic);
            }

            #endregion

            #region IAutoSubscriberConsumeAsync<>

            else if (isGenericType && interfaceType.GetGenericTypeDefinition() == typeof(IAutoSubscriberConsumeAsync<>))
            {
                var consumerGenerator = GetStreamConsumerGenerator(consumerInfo.MessageType);
                return consumerGenerator.CreateAsyncConsumer(autoSubscriberConsumerAttr.Subscription, consumerInfo, consumeMethod, concreteConsumer, topic);
            }

            #endregion


            #region IAutoSubscriberConsumerRegistration

            else if (interfaceType == typeof(IAutoSubscriberConsumerRegistration))
            {
                switch (autoSubscriberConsumerAttr.Subscription)
                {
                    case SubscriptionType.Volatile:
                        var volatileConsumer = new VolatileConsumer();
                        volatileConsumer.Initialize(Bus, GetVolatileSubscription(consumerInfo, consumeMethod, topic),
                            ConsumerExpressionBuilder.CompileConsumerRegistration((IAutoSubscriberConsumerRegistration)concreteConsumer, consumeMethod));
                        return volatileConsumer;
                    case SubscriptionType.CatchUp:
                        var catchUpConsumer = new CatchUpConsumer();
                        catchUpConsumer.Initialize(Bus, GetCatchUpSubscription(consumerInfo, consumeMethod, topic),
                            ConsumerExpressionBuilder.CompileConsumerRegistration((IAutoSubscriberConsumerRegistration)concreteConsumer, consumeMethod));
                        return catchUpConsumer;
                    case SubscriptionType.Persistent:
                    default:
                        var persistentConsumer = new PersistentConsumer();
                        persistentConsumer.Initialize(Bus, GetPersistentSubscription(consumerInfo, consumeMethod, topic),
                            ConsumerExpressionBuilder.CompileConsumerRegistration((IAutoSubscriberConsumerRegistration)concreteConsumer, consumeMethod));
                        return persistentConsumer;
                }
            }

            #endregion

            #region IAutoSubscriberHandlerRegistration

            else if (interfaceType == typeof(IAutoSubscriberHandlerRegistration))
            {
                switch (autoSubscriberConsumerAttr.Subscription)
                {
                    case SubscriptionType.Volatile:
                        var volatileConsumer = new VolatileConsumer();
                        volatileConsumer.Initialize(Bus, GetVolatileSubscription(consumerInfo, consumeMethod, topic),
                            ConsumerExpressionBuilder.CompileHandlerRegistration((IAutoSubscriberHandlerRegistration)concreteConsumer, consumeMethod));
                        return volatileConsumer;
                    case SubscriptionType.CatchUp:
                        var catchUpConsumer = new CatchUpConsumer();
                        catchUpConsumer.Initialize(Bus, GetCatchUpSubscription(consumerInfo, consumeMethod, topic),
                            ConsumerExpressionBuilder.CompileHandlerRegistration((IAutoSubscriberHandlerRegistration)concreteConsumer, consumeMethod));
                        return catchUpConsumer;
                    case SubscriptionType.Persistent:
                    default:
                        var persistentConsumer = new PersistentConsumer();
                        persistentConsumer.Initialize(Bus, GetPersistentSubscription(consumerInfo, consumeMethod, topic),
                            ConsumerExpressionBuilder.CompileHandlerRegistration((IAutoSubscriberHandlerRegistration)concreteConsumer, consumeMethod));
                        return persistentConsumer;
                }
            }

            #endregion


            #region IAutoSubscriberCatchUpConsume

            else if (interfaceType == typeof(IAutoSubscriberCatchUpConsume))
            {
                var catchUpConsumer = new CatchUpConsumer();
                catchUpConsumer.Initialize(Bus, GetCatchUpSubscription(consumerInfo, consumeMethod, topic),
                    ConsumerExpressionBuilder.CompileCatchUpConsumer((IAutoSubscriberCatchUpConsume)concreteConsumer, consumeMethod));
                return catchUpConsumer;
            }

            #endregion

            #region IAutoSubscriberCatchUpConsumeAsync

            else if (interfaceType == typeof(IAutoSubscriberCatchUpConsumeAsync))
            {
                var catchUpConsumer = new CatchUpConsumer();
                catchUpConsumer.Initialize(Bus, GetCatchUpSubscription(consumerInfo, consumeMethod, topic),
                    ConsumerExpressionBuilder.CompileAsyncCatchUpConsumer((IAutoSubscriberCatchUpConsumeAsync)concreteConsumer, consumeMethod));
                return catchUpConsumer;
            }

            #endregion


            #region IAutoSubscriberCatchUpConsume<>

            else if (isGenericType && interfaceType.GetGenericTypeDefinition() == typeof(IAutoSubscriberCatchUpConsume<>))
            {
                var consumerGenerator = GetStreamConsumerGenerator(consumerInfo.MessageType);
                return consumerGenerator.CreateResolvedEventConsumer(SubscriptionType.CatchUp, consumerInfo, consumeMethod, concreteConsumer, topic);
            }

            #endregion

            #region IAutoSubscriberCatchUpConsumeAsync<>

            else if (isGenericType && interfaceType.GetGenericTypeDefinition() == typeof(IAutoSubscriberCatchUpConsumeAsync<>))
            {
                var consumerGenerator = GetStreamConsumerGenerator(consumerInfo.MessageType);
                return consumerGenerator.CreateAsyncResolvedEventConsumer(SubscriptionType.CatchUp, consumerInfo, consumeMethod, concreteConsumer, topic);
            }

            #endregion


            #region IAutoSubscriberPersistentConsume

            else if (interfaceType == typeof(IAutoSubscriberPersistentConsume))
            {
                var catchUpConsumer = new PersistentConsumer();
                catchUpConsumer.Initialize(Bus, GetPersistentSubscription(consumerInfo, consumeMethod, topic),
                    ConsumerExpressionBuilder.CompilePersistentConsumer((IAutoSubscriberPersistentConsume)concreteConsumer, consumeMethod));
                return catchUpConsumer;
            }

            #endregion

            #region IAutoSubscriberPersistentConsumeAsync

            else if (interfaceType == typeof(IAutoSubscriberPersistentConsumeAsync))
            {
                var catchUpConsumer = new PersistentConsumer();
                catchUpConsumer.Initialize(Bus, GetPersistentSubscription(consumerInfo, consumeMethod, topic),
                    ConsumerExpressionBuilder.CompileAsyncPersistentConsumer((IAutoSubscriberPersistentConsumeAsync)concreteConsumer, consumeMethod));
                return catchUpConsumer;
            }

            #endregion


            #region IAutoSubscriberPersistentConsume<>

            else if (isGenericType && interfaceType.GetGenericTypeDefinition() == typeof(IAutoSubscriberPersistentConsume<>))
            {
                var consumerGenerator = GetStreamConsumerGenerator(consumerInfo.MessageType);
                return consumerGenerator.CreateResolvedEventConsumer(SubscriptionType.Persistent, consumerInfo, consumeMethod, concreteConsumer, topic);
            }

            #endregion

            #region IAutoSubscriberPersistentConsumeAsync<>

            else if (isGenericType && interfaceType.GetGenericTypeDefinition() == typeof(IAutoSubscriberPersistentConsumeAsync<>))
            {
                var consumerGenerator = GetStreamConsumerGenerator(consumerInfo.MessageType);
                return consumerGenerator.CreateAsyncResolvedEventConsumer(SubscriptionType.Persistent, consumerInfo, consumeMethod, concreteConsumer, topic);
            }

            #endregion


            #region IAutoSubscriberVolatileConsume

            else if (interfaceType == typeof(IAutoSubscriberVolatileConsume))
            {
                var catchUpConsumer = new VolatileConsumer();
                catchUpConsumer.Initialize(Bus, GetVolatileSubscription(consumerInfo, consumeMethod, topic),
                    ConsumerExpressionBuilder.CompileVolatileConsumer((IAutoSubscriberVolatileConsume)concreteConsumer, consumeMethod));
                return catchUpConsumer;
            }

            #endregion

            #region IAutoSubscriberVolatileConsumeAsync

            else if (interfaceType == typeof(IAutoSubscriberVolatileConsumeAsync))
            {
                var catchUpConsumer = new VolatileConsumer();
                catchUpConsumer.Initialize(Bus, GetVolatileSubscription(consumerInfo, consumeMethod, topic),
                    ConsumerExpressionBuilder.CompileAsyncVolatileConsumer((IAutoSubscriberVolatileConsumeAsync)concreteConsumer, consumeMethod));
                return catchUpConsumer;
            }

            #endregion


            #region IAutoSubscriberVolatileConsume<>

            else if (isGenericType && interfaceType.GetGenericTypeDefinition() == typeof(IAutoSubscriberVolatileConsume<>))
            {
                var consumerGenerator = GetStreamConsumerGenerator(consumerInfo.MessageType);
                return consumerGenerator.CreateResolvedEventConsumer(SubscriptionType.Volatile, consumerInfo, consumeMethod, concreteConsumer, topic);
            }

            #endregion

            #region IAutoSubscriberVolatileConsumeAsync<>

            else if (isGenericType && interfaceType.GetGenericTypeDefinition() == typeof(IAutoSubscriberVolatileConsumeAsync<>))
            {
                var consumerGenerator = GetStreamConsumerGenerator(consumerInfo.MessageType);
                return consumerGenerator.CreateAsyncResolvedEventConsumer(SubscriptionType.Volatile, consumerInfo, consumeMethod, concreteConsumer, topic);
            }

            #endregion

            return null;
        }

        #endregion

        #region ++ GetStreamConsumerGenerator ++

        protected IStreamConsumerGenerator GetStreamConsumerGenerator(Type eventType)
        {
            return _streamConsumerGenerators.GetOrAdd(eventType, type =>
            {
                var generator = ActivatorUtils.FastCreateInstance<IStreamConsumerGenerator>(typeof(StreamConsumerGenerator<>).GetCachedGenericType(type));
                generator.Connection = this.Bus;
                generator.GenerateSubscriptionId = this.GenerateSubscriptionId;
                generator.CombineSubscriptionId = this.CombineSubscriptionId;
                return generator;
            });
        }

        #endregion

        #region ** GetVolatileSubscription **

        private static VolatileSubscription GetVolatileSubscription(AutoSubscriberConsumerInfo consumerInfo, MethodInfo consumeMethod, string topic = null)
        {
            var streamAttr = GetCustomAttribute<StreamAttribute>(consumerInfo, consumeMethod);
            if (streamAttr is null || string.IsNullOrEmpty(streamAttr.StreamId)) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.streamAttr_StreamId); }

            return new VolatileSubscription(streamAttr.StreamId)
            {
                Topic = topic,

                Settings = GetCustomAttribute<ConnectToVolatileSubscriptionConfigurationAttribute>(consumerInfo, consumeMethod).ToSettings(),
                StreamMeta = GetCustomAttribute<StreamMetadataAttribute>(consumerInfo, consumeMethod).ToStreamMetadata(),

                RetryPolicy = GetCustomAttribute<AutoSubscriberRetryPolicyAttribute>(consumerInfo, consumeMethod).ToRetryPolicy(),
                Credentials = GetCustomAttribute<AutoSubscriberUserCredentialAttribute>(consumerInfo, consumeMethod).ToCredentials()
            };
        }

        #endregion

        #region ** GetCatchUpSubscription **

        private static CatchUpSubscription GetCatchUpSubscription(AutoSubscriberConsumerInfo consumerInfo, MethodInfo consumeMethod, string topic = null)
        {
            var streamAttr = GetCustomAttribute<StreamAttribute>(consumerInfo, consumeMethod);
            if (streamAttr is null || string.IsNullOrEmpty(streamAttr.StreamId)) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.streamAttr_StreamId); }

            return new CatchUpSubscription(streamAttr.StreamId)
            {
                Topic = topic,

                Settings = GetCustomAttribute<ConnectToCatchUpSubscriptionConfigurationAttribute>(consumerInfo, consumeMethod).ToSettings(),
                LastCheckpoint = GetCustomAttribute<CatchUpSubscriptionConfigurationAttribute>(consumerInfo, consumeMethod)?.LastCheckpoint,
                StreamMeta = GetCustomAttribute<StreamMetadataAttribute>(consumerInfo, consumeMethod).ToStreamMetadata(),

                RetryPolicy = GetCustomAttribute<AutoSubscriberRetryPolicyAttribute>(consumerInfo, consumeMethod).ToRetryPolicy(),
                Credentials = GetCustomAttribute<AutoSubscriberUserCredentialAttribute>(consumerInfo, consumeMethod).ToCredentials()
            };
        }

        #endregion

        #region ** GetPersistentSubscription **

        private PersistentSubscription GetPersistentSubscription(AutoSubscriberConsumerInfo consumerInfo, MethodInfo consumeMethod, string topic = null)
        {
            var streamAttr = GetCustomAttribute<StreamAttribute>(consumerInfo, consumeMethod);
            if (streamAttr is null || string.IsNullOrEmpty(streamAttr.StreamId)) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.streamAttr_StreamId); }

            var autoSubscriberConsumerAttr = GetCustomAttribute<AutoSubscriberConsumerAttribute>(consumerInfo, consumeMethod);
            var subscriptionId = string.IsNullOrEmpty(autoSubscriberConsumerAttr?.SubscriptionId)
                               ? GenerateSubscriptionId(consumerInfo)
                               : autoSubscriberConsumerAttr?.SubscriptionId;
            return new PersistentSubscription(streamAttr.StreamId, CombineSubscriptionId(subscriptionId))
            {
                Topic = topic,

                Settings = GetCustomAttribute<ConnectToPersistentSubscriptionConfigurationAttribute>(consumerInfo, consumeMethod).ToSettings(),
                StreamMeta = GetCustomAttribute<StreamMetadataAttribute>(consumerInfo, consumeMethod).ToStreamMetadata(),
                PersistentSettings = GetCustomAttribute<PersistentSubscriptionConfigurationAttribute>(consumerInfo, consumeMethod).ToSettings(),

                RetryPolicy = GetCustomAttribute<AutoSubscriberRetryPolicyAttribute>(consumerInfo, consumeMethod).ToRetryPolicy(),
                Credentials = GetCustomAttribute<AutoSubscriberUserCredentialAttribute>(consumerInfo, consumeMethod).ToCredentials()
            };
        }

        #endregion

        #region ++ DefaultSubscriptionIdGenerator ++

        protected virtual string DefaultSubscriptionIdGenerator(AutoSubscriberConsumerInfo c)
        {
            var sb = StringBuilderManager.Allocate();
            var unique = string.Concat(SubscriptionIdPrefix, ":", c.ConcreteType.FullName, ":", c.MessageType?.FullName);

            using (var md5 = MD5.Create())
            {
                var buff = md5.ComputeHash(Encoding.UTF8.GetBytes(unique));
                foreach (var b in buff)
                {
                    sb.Append(b.ToString("x2"));
                }
            }

            //return string.Concat(SubscriptionIdPrefix, ":", StringBuilderManager.ReturnAndFree(sb));
            return StringBuilderManager.ReturnAndFree(sb);
        }

        #endregion

        #region ++ CombineSubscriptionId ++

        protected string CombineSubscriptionId(string subscriptionId)
        {
            const char _separator = ':';

            var sb = StringBuilderManager.Allocate();
            sb.Append(SubscriptionIdPrefix);
            sb.Append(_separator);
            sb.Append(subscriptionId);
            return StringBuilderManager.ReturnAndFree(sb);
        }

        #endregion

        #region ++ GetConcreteConsumer ++

        protected object GetConcreteConsumer(Type concreteType)
        {
            return _concreteConsumers.GetOrAdd(concreteType, type => CreateInstance(type));
        }

        #endregion

        #region ++ GetSubscriptionInfos ++

        protected virtual IEnumerable<KeyValuePair<Type, AutoSubscriberConsumerInfo[]>> GetSubscriptionInfos(IEnumerable<Type> types, Type interfaceType, string consumeMethodName, bool isGenericType)
        {
            foreach (var concreteType in types.Where(t => t.IsClass && !t.IsAbstract))
            {
                AutoSubscriberConsumerInfo[] subscriptionInfos;

                subscriptionInfos = isGenericType
                    ? concreteType.GetInterfaces()
                      .Where(i => i.IsConstructedGenericType && i.GetGenericTypeDefinition() == interfaceType && !i.GetGenericArguments()[0].IsGenericParameter)
                      .Select(i => new AutoSubscriberConsumerInfo(concreteType, i, consumeMethodName, i.GetGenericArguments()[0]))
                      .ToArray()
                    : concreteType.GetInterfaces()
                      .Where(i => !i.IsGenericType && i == interfaceType)
                      .Select(i => new AutoSubscriberConsumerInfo(concreteType, i, consumeMethodName))
                      .ToArray();

                if (subscriptionInfos.Any())
                {
                    yield return new KeyValuePair<Type, AutoSubscriberConsumerInfo[]>(concreteType, subscriptionInfos);
                }
            }
        }

        #endregion

        #region ++ CreateInstance ++

        protected virtual object CreateInstance(Type instanceType)
        {
            return _injectionScope is object
                ? ActivatorUtils.CreateInstance(_injectionScope, instanceType)
                : _services is object
                  ? ActivatorUtils.CreateInstance(_services, instanceType)
                  : ActivatorUtils.FastCreateInstance(instanceType);
        }

        protected T CreateInstance<T>()
        {
            return _injectionScope is object
                ? ActivatorUtils.CreateInstance<T>(_injectionScope)
                : _services is object
                  ? ActivatorUtils.CreateInstance<T>(_services)
                  : ActivatorUtils.FastCreateInstance<T>();
        }

        protected T CreateInstance<T>(Type instanceType) => (T)CreateInstance(instanceType);

        #endregion

        #region **& GetTopAttributeValues &**

        private static ISet<string> GetTopAttributeValues(AutoSubscriberConsumerInfo subscriptionInfo, MethodInfo consumeMethod)
        {
            var topicAttrs = consumeMethod.GetAllAttributes<ForTopicAttribute>();
            if (!topicAttrs.Any())
            {
                topicAttrs = subscriptionInfo.ConcreteType.GetAllAttributes<ForTopicAttribute>() ?? Enumerable.Empty<ForTopicAttribute>();
            }
            var topics = new HashSet<string>(topicAttrs.Select(_ => _.Topic), StringComparer.OrdinalIgnoreCase);
            var topicsAttr = consumeMethod.FirstAttribute<ForTopicsAttribute>();
            if (topicsAttr is null)
            {
                topicsAttr = subscriptionInfo.ConcreteType.FirstAttribute<ForTopicsAttribute>();
            }
            if (topicsAttr is object && topicsAttr.Topics is object && 0u < (uint)topicsAttr.Topics.Length)
            {
                topics.UnionWith(topicsAttr.Topics);
            }
            return topics;
        }

        #endregion

        #region **& GetCustomAttribute<TAttribute> &**

        private static TAttribute GetCustomAttribute<TAttribute>(AutoSubscriberConsumerInfo consumerInfo, MethodInfo consumeMethod)
          where TAttribute : Attribute
        {
            var subscriberAttr = consumeMethod.FirstAttribute<TAttribute>();
            if (subscriberAttr is null)
            {
                subscriberAttr = consumerInfo.ConcreteType.FirstAttribute<TAttribute>();
            }
            if (subscriberAttr is null)
            {
                subscriberAttr = consumerInfo.MessageType?.FirstAttribute<TAttribute>();
            }
            return subscriberAttr;
        }

        #endregion

        #region **& GetConsumeMethod &**

        private static MethodInfo GetConsumeMethod(AutoSubscriberConsumerInfo consumerInfo)
        {
            MethodInfo consumeMethod = null;
            var interfaceType = consumerInfo.InterfaceType;
            var isGenericType = interfaceType.IsGenericType;
            if (isGenericType && (interfaceType.GetGenericTypeDefinition() == typeof(IAutoSubscriberConsume<>) || interfaceType.GetGenericTypeDefinition() == typeof(IAutoSubscriberConsumeAsync<>)))
            {
                consumeMethod = consumerInfo.ConcreteType.GetMethod(consumerInfo.ConsumeMethodName, new[] { consumerInfo.MessageType });
            }
            else if (interfaceType == typeof(IAutoSubscriberConsumerRegistration))
            {
                consumeMethod = consumerInfo.ConcreteType.GetMethod(consumerInfo.ConsumeMethodName, new[] { typeof(IConsumerRegistration) });
            }
            else if (interfaceType == typeof(IAutoSubscriberHandlerRegistration))
            {
                consumeMethod = consumerInfo.ConcreteType.GetMethod(consumerInfo.ConsumeMethodName, new[] { typeof(IHandlerRegistration) });
            }
            else if (interfaceType == typeof(IAutoSubscriberCatchUpConsume) || interfaceType == typeof(IAutoSubscriberCatchUpConsumeAsync))
            {
                consumeMethod = consumerInfo.ConcreteType.GetMethod(consumerInfo.ConsumeMethodName, new[]
                {
                    typeof(EventStoreCatchUpSubscription),
                    typeof(ResolvedEvent<object>)
                });
            }
            else if (isGenericType && (interfaceType.GetGenericTypeDefinition() == typeof(IAutoSubscriberCatchUpConsume<>) || interfaceType.GetGenericTypeDefinition() == typeof(IAutoSubscriberCatchUpConsumeAsync<>)))
            {
                consumeMethod = consumerInfo.ConcreteType.GetMethod(consumerInfo.ConsumeMethodName, new[]
                {
                    typeof(EventStoreCatchUpSubscription<>).GetCachedGenericType(consumerInfo.MessageType),
                    typeof(ResolvedEvent<>).GetCachedGenericType(consumerInfo.MessageType)
                });
            }
            else if (interfaceType == typeof(IAutoSubscriberPersistentConsume) || interfaceType == typeof(IAutoSubscriberPersistentConsumeAsync))
            {
                consumeMethod = consumerInfo.ConcreteType.GetMethod(consumerInfo.ConsumeMethodName, new[]
                {
                    typeof(EventStorePersistentSubscription),
                    typeof(ResolvedEvent<object>),
                    typeof(int?)
                });
            }
            else if (isGenericType && (interfaceType.GetGenericTypeDefinition() == typeof(IAutoSubscriberPersistentConsume<>) || interfaceType.GetGenericTypeDefinition() == typeof(IAutoSubscriberPersistentConsumeAsync<>)))
            {
                consumeMethod = consumerInfo.ConcreteType.GetMethod(consumerInfo.ConsumeMethodName, new[]
                {
                    typeof(EventStorePersistentSubscription<>).GetCachedGenericType(consumerInfo.MessageType),
                    typeof(ResolvedEvent<>).GetCachedGenericType(consumerInfo.MessageType),
                    typeof(int?)
                });
            }
            else if (interfaceType == typeof(IAutoSubscriberVolatileConsume) || interfaceType == typeof(IAutoSubscriberVolatileConsumeAsync))
            {
                consumeMethod = consumerInfo.ConcreteType.GetMethod(consumerInfo.ConsumeMethodName, new[]
                {
                    typeof(EventStoreSubscription),
                    typeof(ResolvedEvent<object>)
                });
            }
            else if (isGenericType && (interfaceType.GetGenericTypeDefinition() == typeof(IAutoSubscriberVolatileConsume<>) || interfaceType.GetGenericTypeDefinition() == typeof(IAutoSubscriberVolatileConsumeAsync<>)))
            {
                consumeMethod = consumerInfo.ConcreteType.GetMethod(consumerInfo.ConsumeMethodName, new[]
                {
                    typeof(EventStoreSubscription),
                    typeof(ResolvedEvent<>).GetCachedGenericType(consumerInfo.MessageType)
                });
            }

            return consumeMethod;
        }

        #endregion

        #region ** class StreamConsumerGenerator<T> **

        internal class StreamConsumerGenerator<T> : IStreamConsumerGenerator
        {
            #region @ Properties @

            public IEventStoreBus Connection { get; set; }
            public Func<AutoSubscriberConsumerInfo, string> GenerateSubscriptionId { get; set; }
            public Func<string, string> CombineSubscriptionId { get; set; }

            #endregion

            #region - CreateConsumer -

            public IStreamConsumer CreateConsumer(SubscriptionType subscription, AutoSubscriberConsumerInfo consumerInfo,
              MethodInfo consumeMethod, object concreteConsumer, string topic = null)
            {
                switch (subscription)
                {
                    case SubscriptionType.Volatile:
                        var volatileConsumer = new VolatileConsumer<T>();
                        volatileConsumer.Initialize(Connection, GetVolatileSubscription(consumerInfo, consumeMethod, topic),
                            ConsumerExpressionBuilder.CompileConsumer((IAutoSubscriberConsume<T>)concreteConsumer, consumeMethod));
                        return volatileConsumer;
                    case SubscriptionType.CatchUp:
                        var catchUpConsumer = new CatchUpConsumer<T>();
                        catchUpConsumer.Initialize(Connection, GetCatchUpSubscription(consumerInfo, consumeMethod, topic),
                            ConsumerExpressionBuilder.CompileConsumer((IAutoSubscriberConsume<T>)concreteConsumer, consumeMethod));
                        return catchUpConsumer;
                    case SubscriptionType.Persistent:
                    default:
                        var persistentConsumer = new PersistentConsumer<T>();
                        persistentConsumer.Initialize(Connection, GetPersistentSubscription(consumerInfo, consumeMethod, topic),
                            ConsumerExpressionBuilder.CompileConsumer((IAutoSubscriberConsume<T>)concreteConsumer, consumeMethod));
                        return persistentConsumer;
                }
            }

            #endregion

            #region - CreateAsyncConsumer -

            public IStreamConsumer CreateAsyncConsumer(SubscriptionType subscription, AutoSubscriberConsumerInfo consumerInfo, MethodInfo consumeMethod, object concreteConsumer, string topic = null)
            {
                switch (subscription)
                {
                    case SubscriptionType.Volatile:
                        var volatileConsumer = new VolatileConsumer<T>();
                        volatileConsumer.Initialize(Connection, GetVolatileSubscription(consumerInfo, consumeMethod, topic),
                            ConsumerExpressionBuilder.CompileAsyncConsumer((IAutoSubscriberConsumeAsync<T>)concreteConsumer, consumeMethod));
                        return volatileConsumer;
                    case SubscriptionType.CatchUp:
                        var catchUpConsumer = new CatchUpConsumer<T>();
                        catchUpConsumer.Initialize(Connection, GetCatchUpSubscription(consumerInfo, consumeMethod, topic),
                            ConsumerExpressionBuilder.CompileAsyncConsumer((IAutoSubscriberConsumeAsync<T>)concreteConsumer, consumeMethod));
                        return catchUpConsumer;
                    case SubscriptionType.Persistent:
                    default:
                        var persistentConsumer = new PersistentConsumer<T>();
                        persistentConsumer.Initialize(Connection, GetPersistentSubscription(consumerInfo, consumeMethod, topic),
                            ConsumerExpressionBuilder.CompileAsyncConsumer((IAutoSubscriberConsumeAsync<T>)concreteConsumer, consumeMethod));
                        return persistentConsumer;
                }
            }

            #endregion

            #region - CreateResolvedEventConsumer -

            public IStreamConsumer CreateResolvedEventConsumer(SubscriptionType subscription, AutoSubscriberConsumerInfo consumerInfo, MethodInfo consumeMethod, object concreteConsumer, string topic = null)
            {
                switch (subscription)
                {
                    case SubscriptionType.Volatile:
                        var volatileConsumer = new VolatileConsumer<T>();
                        volatileConsumer.Initialize(Connection, GetVolatileSubscription(consumerInfo, consumeMethod, topic),
                            ConsumerExpressionBuilder.CompileVolatileConsumer((IAutoSubscriberVolatileConsume<T>)concreteConsumer, consumeMethod));
                        return volatileConsumer;
                    case SubscriptionType.CatchUp:
                        var catchUpConsumer = new CatchUpConsumer<T>();
                        catchUpConsumer.Initialize(Connection, GetCatchUpSubscription(consumerInfo, consumeMethod, topic),
                            ConsumerExpressionBuilder.CompileCatchUpConsumer((IAutoSubscriberCatchUpConsume<T>)concreteConsumer, consumeMethod));
                        return catchUpConsumer;
                    case SubscriptionType.Persistent:
                    default:
                        var persistentConsumer = new PersistentConsumer<T>();
                        persistentConsumer.Initialize(Connection, GetPersistentSubscription(consumerInfo, consumeMethod, topic),
                            ConsumerExpressionBuilder.CompilePersistentConsumer((IAutoSubscriberPersistentConsume<T>)concreteConsumer, consumeMethod));
                        return persistentConsumer;
                }
            }

            #endregion

            #region - CreateAsyncResolvedEventConsumer -

            public IStreamConsumer CreateAsyncResolvedEventConsumer(SubscriptionType subscription, AutoSubscriberConsumerInfo consumerInfo, MethodInfo consumeMethod, object concreteConsumer, string topic = null)
            {
                switch (subscription)
                {
                    case SubscriptionType.Volatile:
                        var volatileConsumer = new VolatileConsumer<T>();
                        volatileConsumer.Initialize(Connection, GetVolatileSubscription(consumerInfo, consumeMethod, topic),
                            ConsumerExpressionBuilder.CompileAsyncVolatileConsumer((IAutoSubscriberVolatileConsumeAsync<T>)concreteConsumer, consumeMethod));
                        return volatileConsumer;
                    case SubscriptionType.CatchUp:
                        var catchUpConsumer = new CatchUpConsumer<T>();
                        catchUpConsumer.Initialize(Connection, GetCatchUpSubscription(consumerInfo, consumeMethod, topic),
                            ConsumerExpressionBuilder.CompileAsyncCatchUpConsumer((IAutoSubscriberCatchUpConsumeAsync<T>)concreteConsumer, consumeMethod));
                        return catchUpConsumer;
                    case SubscriptionType.Persistent:
                    default:
                        var persistentConsumer = new PersistentConsumer<T>();
                        persistentConsumer.Initialize(Connection, GetPersistentSubscription(consumerInfo, consumeMethod, topic),
                            ConsumerExpressionBuilder.CompileAsyncPersistentConsumer((IAutoSubscriberPersistentConsumeAsync<T>)concreteConsumer, consumeMethod));
                        return persistentConsumer;
                }
            }

            #endregion

            #region * GetVolatileSubscription *

            private static VolatileSubscription<T> GetVolatileSubscription(AutoSubscriberConsumerInfo consumerInfo, MethodInfo consumeMethod, string topic = null)
            {
                return new VolatileSubscription<T>()
                {
                    Topic = topic,

                    Settings = GetCustomAttribute<ConnectToVolatileSubscriptionConfigurationAttribute>(consumerInfo, consumeMethod).ToSettings(),
                    StreamMeta = GetCustomAttribute<StreamMetadataAttribute>(consumerInfo, consumeMethod).ToStreamMetadata(),

                    RetryPolicy = GetCustomAttribute<AutoSubscriberRetryPolicyAttribute>(consumerInfo, consumeMethod).ToRetryPolicy(),
                    Credentials = GetCustomAttribute<AutoSubscriberUserCredentialAttribute>(consumerInfo, consumeMethod).ToCredentials()
                };
            }

            #endregion

            #region * GetCatchUpSubscription *

            private static CatchUpSubscription<T> GetCatchUpSubscription(AutoSubscriberConsumerInfo consumerInfo, MethodInfo consumeMethod, string topic = null)
            {
                return new CatchUpSubscription<T>()
                {
                    Topic = topic,

                    Settings = GetCustomAttribute<ConnectToCatchUpSubscriptionConfigurationAttribute>(consumerInfo, consumeMethod).ToSettings(),
                    LastCheckpoint = GetCustomAttribute<CatchUpSubscriptionConfigurationAttribute>(consumerInfo, consumeMethod)?.LastCheckpoint,
                    StreamMeta = GetCustomAttribute<StreamMetadataAttribute>(consumerInfo, consumeMethod).ToStreamMetadata(),

                    RetryPolicy = GetCustomAttribute<AutoSubscriberRetryPolicyAttribute>(consumerInfo, consumeMethod).ToRetryPolicy(),
                    Credentials = GetCustomAttribute<AutoSubscriberUserCredentialAttribute>(consumerInfo, consumeMethod).ToCredentials()
                };
            }

            #endregion

            #region * GetPersistentSubscription *

            private PersistentSubscription<T> GetPersistentSubscription(AutoSubscriberConsumerInfo consumerInfo, MethodInfo consumeMethod, string topic = null)
            {
                var autoSubscriberConsumerAttr = GetCustomAttribute<AutoSubscriberConsumerAttribute>(consumerInfo, consumeMethod);
                var subscriptionId = string.IsNullOrEmpty(autoSubscriberConsumerAttr?.SubscriptionId)
                                   ? GenerateSubscriptionId(consumerInfo)
                                   : autoSubscriberConsumerAttr?.SubscriptionId;
                return new PersistentSubscription<T>(CombineSubscriptionId(subscriptionId))
                {
                    Topic = topic,

                    Settings = GetCustomAttribute<ConnectToPersistentSubscriptionConfigurationAttribute>(consumerInfo, consumeMethod).ToSettings(),
                    StreamMeta = GetCustomAttribute<StreamMetadataAttribute>(consumerInfo, consumeMethod).ToStreamMetadata(),
                    PersistentSettings = GetCustomAttribute<PersistentSubscriptionConfigurationAttribute>(consumerInfo, consumeMethod).ToSettings(),

                    RetryPolicy = GetCustomAttribute<AutoSubscriberRetryPolicyAttribute>(consumerInfo, consumeMethod).ToRetryPolicy(),
                    Credentials = GetCustomAttribute<AutoSubscriberUserCredentialAttribute>(consumerInfo, consumeMethod).ToCredentials()
                };
            }

            #endregion
        }

        #endregion
    }
}