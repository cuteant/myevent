﻿using System;
using System.IO;
using EventStore.Common.Utils;

namespace EventStore.Core.TransactionLog.LogRecords
{
    public enum SystemRecordType: byte
    {
        Invalid = 0,
        Epoch = 1
    }

    public enum SystemRecordSerialization: byte
    {
        Invalid = 0,
        Binary = 1,
        Json = 2,
        Bson = 3
    }

    public class SystemLogRecord: LogRecord, IEquatable<SystemLogRecord>
    {
        public const byte SystemRecordVersion = 0;

        public readonly DateTime TimeStamp;
        public readonly SystemRecordType SystemRecordType;
        public readonly SystemRecordSerialization SystemRecordSerialization;
        public readonly long Reserved;
        public readonly byte[] Data;

        public SystemLogRecord(long logPosition,
                               DateTime timeStamp,
                               SystemRecordType systemRecordType,
                               SystemRecordSerialization systemRecordSerialization,
                               byte[] data)
            : base(LogRecordType.System, SystemRecordVersion, logPosition)
        {
            TimeStamp = timeStamp;
            SystemRecordType = systemRecordType;
            SystemRecordSerialization = systemRecordSerialization;
            Reserved = 0;
            Data = data ?? NoData;
        }

        internal SystemLogRecord(BinaryReader reader, byte version, long logPosition): base(LogRecordType.System, version, logPosition)
        {
            if (version != SystemRecordVersion)
                ThrowHelper.ThrowArgumentException_SystemRecordVersionIsIncorrect(version);

            TimeStamp = new DateTime(reader.ReadInt64());
            SystemRecordType = (SystemRecordType) reader.ReadByte();
            if (SystemRecordType == SystemRecordType.Invalid)
                ThrowHelper.ThrowArgumentException_InvalidSystemRecordType(SystemRecordType, LogPosition);
            SystemRecordSerialization = (SystemRecordSerialization) reader.ReadByte();
            if (SystemRecordSerialization == SystemRecordSerialization.Invalid)
                ThrowHelper.ThrowArgumentException_InvalidSystemRecordSerialization(SystemRecordSerialization, LogPosition);
            Reserved = reader.ReadInt64();

            var dataCount = reader.ReadInt32();
            Data = 0u >= (uint)dataCount ? NoData : reader.ReadBytes(dataCount);
        }

        public EpochRecord GetEpochRecord()
        {
            if (SystemRecordType != SystemRecordType.Epoch)
                ThrowHelper.ThrowArgumentException_UnexpectedTypeOfSystemRecord(SystemRecordType);

            switch (SystemRecordSerialization)
            {
                case SystemRecordSerialization.Json:
                    var dto = Data.ParseJson<EpochRecord.EpochRecordDto>();
                    return new EpochRecord(dto);
                default:
                    ThrowHelper.ThrowArgumentOutOfRangeException_UnexpectedSystemRecordSerializationType(SystemRecordSerialization); return null;
            }
        }

        public override void WriteTo(BinaryWriter writer)
        {
            base.WriteTo(writer);

            writer.Write(TimeStamp.Ticks);
            writer.Write((byte)SystemRecordType);
            writer.Write((byte)SystemRecordSerialization);
            writer.Write(Reserved);
            writer.Write(Data.Length);
            writer.Write(Data);
        }

        public bool Equals(SystemLogRecord other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return other.LogPosition == LogPosition
                   && other.TimeStamp.Equals(TimeStamp)
                   && other.SystemRecordType == SystemRecordType
                   && other.SystemRecordSerialization == SystemRecordSerialization
                   && other.Reserved == Reserved;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != typeof (SystemRecordType)) return false;
            return Equals((SystemLogRecord) obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int result = LogPosition.GetHashCode();
                result = (result * 397) ^ TimeStamp.GetHashCode();
                result = (result * 397) ^ SystemRecordType.GetHashCode();
                result = (result * 397) ^ SystemRecordSerialization.GetHashCode();
                result = (result * 397) ^ Reserved.GetHashCode();
                return result;
            }
        }

        public static bool operator ==(SystemLogRecord left, SystemLogRecord right)
        {
            return Equals(left, right);
        }

        public static bool operator !=(SystemLogRecord left, SystemLogRecord right)
        {
            return !Equals(left, right);
        }

        public override string ToString()
        {
            return string.Format("LogPosition: {0}, "
                                 + "TimeStamp: {1}, "
                                 + "SystemRecordType: {2}, "
                                 + "SystemRecordSerialization: {3}, "
                                 + "Reserved: {4}",
                                 LogPosition,
                                 TimeStamp,
                                 SystemRecordType,
                                 SystemRecordSerialization,
                                 Reserved);
        }
    }
}