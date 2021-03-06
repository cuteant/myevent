﻿using System;
using System.Collections.Generic;
using EventStore.Common.Utils;

namespace EventStore.Core.Services.PersistentSubscription
{
    public class PersistentSubscriptionConfig
    {
        public string Version;
        public DateTime Updated;
        public string UpdatedBy;
        public List<PersistentSubscriptionEntry> Entries = new List<PersistentSubscriptionEntry>();

        public byte[] GetSerializedForm()
        {
            return this.ToJsonBytes();
        }

        public static PersistentSubscriptionConfig FromSerializedForm(byte[] data)
        {
            try
            {
                var ret = data.ParseJson<PersistentSubscriptionConfig>();
                if(ret.Version is null) ThrowHelper.ThrowBadConfigDataException_DeserializedButNoVersionPresent();

                UpdateIfRequired(ret);

                return ret;
            }
            catch (Exception ex)
            {
                ThrowHelper.ThrowBadConfigDataException_TheConfigDataAppearsToBeInvalid(ex); return null;
            }
        }

        private static void UpdateIfRequired(PersistentSubscriptionConfig ret)
        {
            if (ret.Version == "1")
            {
                if (ret.Entries is object)
                {
                    foreach (var persistentSubscriptionEntry in ret.Entries)
                    {
                        if (string.IsNullOrEmpty(persistentSubscriptionEntry.NamedConsumerStrategy))
                        {
                            persistentSubscriptionEntry.NamedConsumerStrategy = persistentSubscriptionEntry.PreferRoundRobin
                                ? SystemConsumerStrategies.RoundRobin
                                : SystemConsumerStrategies.DispatchToSingle;
                        }
                    }
                }
                ret.Version = "2";
            }
        }
    }

    public class BadConfigDataException : Exception
    {
        public BadConfigDataException(string message, Exception inner) : base(message, inner)
        {
            
        }
    }

    public class PersistentSubscriptionEntry
    {
        public string Stream;
        public string Group;
        public bool ResolveLinkTos;
        public bool ExtraStatistics;
        public int MessageTimeout;
        public long StartFrom;
        public int LiveBufferSize;
        public int HistoryBufferSize;
        public int MaxRetryCount;
        public int ReadBatchSize;
        public bool PreferRoundRobin;
        public int CheckPointAfter { get; set; }
        public int MinCheckPointCount { get; set; }
        public int MaxCheckPointCount { get; set; }
        public int MaxSubscriberCount { get; set; }
        public string NamedConsumerStrategy { get; set; }
    }
}