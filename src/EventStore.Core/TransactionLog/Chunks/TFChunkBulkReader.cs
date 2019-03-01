﻿using System;
using System.Diagnostics;
using System.IO;
using EventStore.Common.Utils;
using EventStore.Core.TransactionLog.Chunks.TFChunk;

namespace EventStore.Core.TransactionLog.Chunks
{
    public class TFChunkBulkReader : IDisposable
    {
        public TFChunk.TFChunk Chunk { get { return _chunk; } }
        internal Stream Stream { get { return _stream; } }

        private readonly TFChunk.TFChunk _chunk;
        private readonly Stream _stream;
        private bool _disposed;

        internal TFChunkBulkReader(TFChunk.TFChunk chunk, Stream streamToUse)
        {
            if (chunk == null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.chunk); }
            if (streamToUse == null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.streamToUse); }
            _chunk = chunk;
            _stream = streamToUse;
        }

        ~TFChunkBulkReader()
        {
            Dispose();
        }

        public void SetRawPosition(int rawPosition)
        {
            if (rawPosition >= _stream.Length)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException_RawPositionIsOutOfBounds(rawPosition);
            }

            _stream.Position = rawPosition;
        }

        public void SetDataPosition(long dataPosition)
        {
            var rawPos = dataPosition + ChunkHeader.Size;
            if (rawPos >= _stream.Length)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException_DataPositionIsOutOfBounds(dataPosition);
            }

            _stream.Position = rawPos;
        }

        public void Release()
        {
            _stream.Close();
            _stream.Dispose();
            _disposed = true;
            _chunk.ReleaseReader(this);
        }

        public BulkReadResult ReadNextRawBytes(int count, byte[] buffer)
        {
            if (null == buffer) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.buffer); }
            if (count < 0) { ThrowHelper.ThrowArgumentOutOfRangeException_Nonnegative(ExceptionArgument.count); }

            if ((uint)count > (uint)buffer.Length)
            {
                count = buffer.Length;
            }

            var oldPos = (int)_stream.Position;
            int bytesRead = _stream.Read(buffer, 0, count);
            return new BulkReadResult(oldPos, bytesRead, isEof: _stream.Length == _stream.Position);
        }

        public BulkReadResult ReadNextDataBytes(int count, byte[] buffer)
        {
            if (null == buffer) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.buffer); }
            if (count < 0) { ThrowHelper.ThrowArgumentOutOfRangeException_Nonnegative(ExceptionArgument.count); }

            if (_stream.Position == 0)
            {
                _stream.Position = ChunkHeader.Size;
            }

            if ((uint)count > (uint)buffer.Length)
            {
                count = buffer.Length;
            }

            var oldPos = (int)_stream.Position - ChunkHeader.Size;
            var toRead = Math.Min(_chunk.PhysicalDataSize - oldPos, count);
            Debug.Assert(toRead >= 0);
            _stream.Position = _stream.Position; // flush read buffer
            int bytesRead = _stream.Read(buffer, 0, toRead);
            return new BulkReadResult(oldPos,
                                      bytesRead,
                                      isEof: _chunk.IsReadOnly && oldPos + bytesRead == _chunk.PhysicalDataSize);
        }

        public void Dispose()
        {
            if (_disposed) { return; }
            Release();
            GC.SuppressFinalize(this);
        }
    }
}