﻿using System;
using System.Collections.Generic;
using System.Linq;
using EventStore.Common.Utils;

namespace EventStore.Core.Services.Monitoring.Stats
{
    public class StatsContainer
    {
        private readonly Dictionary<string, object> _stats = new Dictionary<string, object>(StringComparer.Ordinal);

        private const string Separator = "-";
        private static readonly string[] SplitSeparator = new[] {Separator};

        public void Add(IDictionary<string, object> statGroup)
        {
            if (statGroup is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.statGroup); }

            foreach (var stat in statGroup)
                _stats.Add(stat.Key, stat.Value);
        }

        public Dictionary<string, object> GetStats(bool useGrouping, bool useMetadata)
        {
            if (useGrouping && useMetadata)
                return GetGroupedStatsWithMetadata();

            if (useGrouping && !useMetadata)
                return GetGroupedStats();

            if (!useGrouping && useMetadata)
                return GetRawStatsWithMetadata();

            //if (!useGrouping && !useMetadata)
                return GetRawStats();
        }

        private Dictionary<string, object> GetGroupedStatsWithMetadata()
        {
            var grouped = Group(_stats);
            return grouped;
        }

        private Dictionary<string, object> GetGroupedStats()
        {
            var values = GetStatsValues(_stats);
            var grouped = Group(values);
            return grouped;
        }

        private Dictionary<string, object> GetRawStatsWithMetadata()
        {
            return new Dictionary<string, object>(_stats, StringComparer.Ordinal);
        }

        private Dictionary<string, object> GetRawStats()
        {
            var values = GetStatsValues(_stats);
            return values;
        }

        private static Dictionary<string, object> GetStatsValues(Dictionary<string, object> dictionary)
        {
            return dictionary.ToDictionary(
                kvp => kvp.Key,
                kvp =>
                {
                    var statInfo = kvp.Value as StatMetadata;
                    return statInfo is null ? kvp.Value : statInfo.Value;
                });
        }

        public static Dictionary<string, object> Group(Dictionary<string, object> input)
        {
            if (input is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.input); }

            if (input.IsEmpty())
                return input;

            var groupContainer = NewDictionary();
            var hasSubGroups = false;

            foreach (var entry in input)
            {
                var groups = entry.Key.Split(SplitSeparator, StringSplitOptions.RemoveEmptyEntries);
                if (2u > (uint)groups.Length)
                {
                    groupContainer.Add(entry.Key, entry.Value);
                    continue;
                }

                hasSubGroups = true;

                string prefix = groups[0];
                string remaining = string.Join(Separator, groups.Skip(1).ToArray());

                if (!groupContainer.ContainsKey(prefix))
                    groupContainer.Add(prefix, NewDictionary());

                ((Dictionary<string, object>)groupContainer[prefix]).Add(remaining, entry.Value);
            }

            if (!hasSubGroups)
                return groupContainer;

            // we must first iterate through all dictionary and then aggregate it again
            var result = NewDictionary();

            foreach (var entry in groupContainer)
            {
                var subgroup = entry.Value as Dictionary<string, object>;
                if (subgroup is object)
                    result[entry.Key] = Group(subgroup);
                else
                    result[entry.Key] = entry.Value;
            }

            return result;
        }

        private static Dictionary<string,object> NewDictionary()
        {
            return new Dictionary<string, object>(new CaseInsensitiveStringComparer());
        }

        private class CaseInsensitiveStringComparer: IEqualityComparer<string>
        {
            public bool Equals(string x, string y)
            {
                return string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
            }

            public int GetHashCode(string obj)
            {
                return obj is object ? obj.ToUpperInvariant().GetHashCode() : -1;
            }
        }
    }
}
