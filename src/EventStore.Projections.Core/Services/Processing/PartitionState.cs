﻿using System;
using EventStore.Common.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SpanJson.Serialization;

namespace EventStore.Projections.Core.Services.Processing
{
    public class PartitionState
    {
        public bool IsChanged(PartitionState newState)
        {
            return State != newState.State || Result != newState.Result;
        }

        public static PartitionState Deserialize(string serializedState, CheckpointTag causedBy)
        {
            if (serializedState is null)
                return new PartitionState("", null, causedBy);

            JToken state = null;
            JToken result = null;

            if (!string.IsNullOrEmpty(serializedState))
            {
                var deserialized = JsonConvertX.DeserializeObject(serializedState);
                var array = deserialized as JArray;
                if (array is object && array.Count > 0)
                {
                    state = array[0] as JToken;
                    if (array.Count == 2)
                    {
                        result = array[1] as JToken;
                    }
                }
                else
                {
                    state = deserialized as JObject;
                }
            }

            var stateJson = state is object ? state.ToCanonicalJson() : "";
            var resultJson = result is object ? result.ToCanonicalJson() : null;

            return new PartitionState(stateJson, resultJson, causedBy);
        }

        private static void Error(JsonTextReader reader, string message)
        {
            throw new Exception(string.Format("{0} (At: {1}, {2})", message, reader.LineNumber, reader.LinePosition));
        }

        private readonly string _state;
        private readonly string _result;
        private readonly CheckpointTag _causedBy;

        public PartitionState(string state, string result, CheckpointTag causedBy)
        {
            if (null == state) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.state); }
            if (null == causedBy) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.causedBy); }

            _state = state;
            _result = result;
            _causedBy = causedBy;
        }

        public string State
        {
            get { return _state; }
        }

        public CheckpointTag CausedBy
        {
            get { return _causedBy; }
        }

        public string Result
        {
            get { return _result; }
        }

        public string Serialize()
        {
            var state = _state;
            if (state == "" && Result is object)
                throw new Exception("state == \"\" && Result is object");
            return Result is object
                       ? "[" + state + "," + _result + "]"
                       : "[" + state + "]";
        }
    }
}
