﻿using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using CuteAnt.Buffers;
using CuteAnt.Pool;
using CuteAnt.Text;
using DotNetty.Common;
using EventStore.Core.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SpanJson.Serialization;

namespace EventStore.Core.Data
{
    public class StreamMetadata
    {
        private const int c_initialBufferSize = 1024 * 4;
        private static readonly ArrayPool<byte> s_sharedBufferPool = BufferManager.Shared;

        public static readonly StreamMetadata Empty = new StreamMetadata();

        public readonly long? MaxCount;
        public readonly TimeSpan? MaxAge;

        public readonly long? TruncateBefore;
        public readonly bool? TempStream;

        public readonly TimeSpan? CacheControl;
        public readonly StreamAcl Acl;

        public StreamMetadata(long? maxCount = null, TimeSpan? maxAge = null,
                            long? truncateBefore = null, bool? tempStream = null,
                            TimeSpan? cacheControl = null, StreamAcl acl = null)
        {
            if (maxCount <= 0)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException_StreamMetadata_MaxCount();
            }
            if (maxAge <= TimeSpan.Zero)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException_StreamMetadata_MaxAge();
            }
            if (truncateBefore < 0)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException_StreamMetadata_TruncateBefore();
            }
            if (cacheControl <= TimeSpan.Zero)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException_StreamMetadata_CacheControl();
            }

            MaxCount = maxCount;
            MaxAge = maxAge;
            TruncateBefore = truncateBefore;
            TempStream = tempStream;
            CacheControl = cacheControl;
            Acl = acl;
        }

        public override string ToString()
        {
            return string.Format("MaxCount: {0}, MaxAge: {1}, TruncateBefore: {2}, TempStream: {3}, CacheControl: {4}, Acl: {5}",
                                 MaxCount, MaxAge, TruncateBefore, TempStream, CacheControl, Acl);
        }

        public static StreamMetadata FromJsonBytes(byte[] json)
        {
            using (var reader = new JsonTextReader(new StreamReader(new MemoryStream(json), Encoding.UTF8)))
            {
                reader.ArrayPool = JsonConvertX.GlobalCharacterArrayPool;
                reader.CloseInput = false;

                return FromJsonReader(reader);
            }
        }

        public static StreamMetadata FromJson(string json)
        {
            using (var reader = new JsonTextReader(new StringReader(json)))
            {
                reader.ArrayPool = JsonConvertX.GlobalCharacterArrayPool;
                reader.CloseInput = false;

                return FromJsonReader(reader);
            }
        }

        public static StreamMetadata FromJsonReader(JsonTextReader reader)
        {
            Check(reader.Read(), reader);
            Check(JsonToken.StartObject, reader);

            long? maxCount = null;
            TimeSpan? maxAge = null;
            long? truncateBefore = null;
            bool? tempStream = null;
            TimeSpan? cacheControl = null;
            StreamAcl acl = null;

            while (true)
            {
                Check(reader.Read(), reader);
                if (reader.TokenType == JsonToken.EndObject) { break; }
                Check(JsonToken.PropertyName, reader);
                var name = (string)reader.Value;
                switch (name)
                {
                    case SystemMetadata.MaxCount:
                        {
                            Check(reader.Read(), reader);
                            Check(JsonToken.Integer, reader);
                            maxCount = (long)reader.Value;
                            break;
                        }
                    case SystemMetadata.MaxAge:
                        {
                            Check(reader.Read(), reader);
                            Check(JsonToken.Integer, reader);
                            maxAge = TimeSpan.FromSeconds((long)reader.Value);
                            break;
                        }
                    case SystemMetadata.TruncateBefore:
                        {
                            Check(reader.Read(), reader);
                            Check(JsonToken.Integer, reader);
                            truncateBefore = (long)reader.Value;
                            break;
                        }
                    case SystemMetadata.TempStream:
                        {
                            Check(reader.Read(), reader);
                            Check(JsonToken.Boolean, reader);
                            tempStream = (bool)reader.Value;
                            break;
                        }
                    case SystemMetadata.CacheControl:
                        {
                            Check(reader.Read(), reader);
                            Check(JsonToken.Integer, reader);
                            cacheControl = TimeSpan.FromSeconds((long)reader.Value);
                            break;
                        }
                    case SystemMetadata.Acl:
                        {
                            acl = ReadAcl(reader);
                            break;
                        }
                    default:
                        {
                            Check(reader.Read(), reader);
                            // skip
                            JToken.ReadFrom(reader);
                            break;
                        }
                }
            }
            return new StreamMetadata(
                maxCount > 0 ? maxCount : null, maxAge > TimeSpan.Zero ? maxAge : null,
                truncateBefore >= 0 ? truncateBefore : null, tempStream, cacheControl > TimeSpan.Zero ? cacheControl : null, acl);
        }

        internal static StreamAcl ReadAcl(JsonTextReader reader)
        {
            Check(reader.Read(), reader);
            Check(JsonToken.StartObject, reader);

            string[] read = null;
            string[] write = null;
            string[] delete = null;
            string[] metaRead = null;
            string[] metaWrite = null;

            while (true)
            {
                Check(reader.Read(), reader);
                if (reader.TokenType == JsonToken.EndObject) { break; }
                Check(JsonToken.PropertyName, reader);
                var name = (string)reader.Value;
                switch (name)
                {
                    case SystemMetadata.AclRead: read = ReadRoles(reader); break;
                    case SystemMetadata.AclWrite: write = ReadRoles(reader); break;
                    case SystemMetadata.AclDelete: delete = ReadRoles(reader); break;
                    case SystemMetadata.AclMetaRead: metaRead = ReadRoles(reader); break;
                    case SystemMetadata.AclMetaWrite: metaWrite = ReadRoles(reader); break;
                }
            }
            return new StreamAcl(read, write, delete, metaRead, metaWrite);
        }

        private static string[] ReadRoles(JsonTextReader reader)
        {
            Check(reader.Read(), reader);
            if (reader.TokenType == JsonToken.String)
            {
                return new[] { (string)reader.Value };
            }

            if (reader.TokenType == JsonToken.StartArray)
            {
                var roles = ThreadLocalList<string>.NewInstance();
                try
                {
                    while (true)
                    {
                        Check(reader.Read(), reader);
                        if (reader.TokenType == JsonToken.EndArray) { break; }
                        Check(JsonToken.String, reader);
                        roles.Add((string)reader.Value);
                    }
                    return roles.ToArray();
                }
                finally
                {
                    roles.Return();
                }
            }

            ThrowHelper.ThrowException_InvalidJson(); return null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Check(JsonToken type, JsonTextReader reader)
        {
            if (reader.TokenType != type) { ThrowHelper.ThrowException_InvalidJson(); }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Check(bool read, JsonTextReader reader)
        {
            if (!read) { ThrowHelper.ThrowException_InvalidJson(); }
        }

        public byte[] ToJsonBytes()
        {
            using (var pooledOutputStream = BufferManagerOutputStreamManager.Create())
            {
                var outputStream = pooledOutputStream.Object;
                outputStream.Reinitialize(c_initialBufferSize, s_sharedBufferPool);

                using (JsonTextWriter jsonWriter = new JsonTextWriter(new StreamWriterX(outputStream, StringHelper.UTF8NoBOM)))
                {
                    jsonWriter.ArrayPool = JsonConvertX.GlobalCharacterArrayPool;
                    jsonWriter.CloseOutput = false;

                    WriteAsJson(jsonWriter);
                    jsonWriter.Flush();
                }
                return outputStream.ToByteArray();
            }
        }

        public string ToJsonString()
        {
            using (var pooledStringWriter = StringWriterManager.Create())
            {
                var sw = pooledStringWriter.Object;

                using (JsonTextWriter jsonWriter = new JsonTextWriter(sw))
                {
                    jsonWriter.ArrayPool = JsonConvertX.GlobalCharacterArrayPool;
                    jsonWriter.CloseOutput = false;

                    WriteAsJson(jsonWriter);
                    jsonWriter.Flush();
                }
                return sw.ToString();
            }
        }

        private void WriteAsJson(JsonTextWriter jsonWriter)
        {
            jsonWriter.WriteStartObject();
            if (MaxCount.HasValue)
            {
                jsonWriter.WritePropertyName(SystemMetadata.MaxCount);
                jsonWriter.WriteValue(MaxCount.Value);
            }
            if (MaxAge.HasValue)
            {
                jsonWriter.WritePropertyName(SystemMetadata.MaxAge);
                jsonWriter.WriteValue((long)MaxAge.Value.TotalSeconds);
            }
            if (TruncateBefore.HasValue)
            {
                jsonWriter.WritePropertyName(SystemMetadata.TruncateBefore);
                jsonWriter.WriteValue(TruncateBefore.Value);
            }
            if (TempStream.HasValue)
            {
                jsonWriter.WritePropertyName(SystemMetadata.TempStream);
                jsonWriter.WriteValue(TempStream.Value);
            }
            if (CacheControl.HasValue)
            {
                jsonWriter.WritePropertyName(SystemMetadata.CacheControl);
                jsonWriter.WriteValue((long)CacheControl.Value.TotalSeconds);
            }
            if (Acl is object)
            {
                jsonWriter.WritePropertyName(SystemMetadata.Acl);
                WriteAcl(jsonWriter, Acl);
            }
            jsonWriter.WriteEndObject();
        }

        internal static void WriteAcl(JsonTextWriter jsonWriter, StreamAcl acl)
        {
            jsonWriter.WriteStartObject();
            WriteAclRoles(jsonWriter, SystemMetadata.AclRead, acl.ReadRoles);
            WriteAclRoles(jsonWriter, SystemMetadata.AclWrite, acl.WriteRoles);
            WriteAclRoles(jsonWriter, SystemMetadata.AclDelete, acl.DeleteRoles);
            WriteAclRoles(jsonWriter, SystemMetadata.AclMetaRead, acl.MetaReadRoles);
            WriteAclRoles(jsonWriter, SystemMetadata.AclMetaWrite, acl.MetaWriteRoles);
            jsonWriter.WriteEndObject();
        }

        private static void WriteAclRoles(JsonTextWriter jsonWriter, string propertyName, string[] roles)
        {
            if (roles is null) { return; }
            jsonWriter.WritePropertyName(propertyName);
            if (roles.Length == 1)
            {
                jsonWriter.WriteValue(roles[0]);
            }
            else
            {
                jsonWriter.WriteStartArray();
                Array.ForEach(roles, jsonWriter.WriteValue);
                jsonWriter.WriteEndArray();
            }
        }
    }
}
