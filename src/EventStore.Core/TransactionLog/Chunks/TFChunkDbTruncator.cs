﻿using System;
using System.IO;
using EventStore.Common.Utils;
using Microsoft.Extensions.Logging;

namespace EventStore.Core.TransactionLog.Chunks
{
    public class TFChunkDbTruncator
    {
        private static readonly ILogger Log = TraceLogger.GetLogger<TFChunkDbTruncator>();

        private readonly TFChunkDbConfig _config;

        public TFChunkDbTruncator(TFChunkDbConfig config)
        {
            if (config is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.config); }
            _config = config;
        }

        public void TruncateDb(long truncateChk)
        {
            var writerChk = _config.WriterCheckpoint.Read();
            var oldLastChunkNum = (int)(writerChk / _config.ChunkSize);
            var newLastChunkNum = (int)(truncateChk / _config.ChunkSize);

            var excessiveChunks = _config.FileNamingStrategy.GetAllVersionsFor(oldLastChunkNum + 1);
            if ((uint)excessiveChunks.Length > 0u)
            {
                ThrowHelper.ThrowException_DuringTruncationOfDBExcessiveTFChunksWereFound(excessiveChunks);
            }

            ChunkHeader newLastChunkHeader = null;
            string newLastChunkFilename = null;
            for (int chunkNum = 0; chunkNum <= newLastChunkNum;)
            {
                var chunks = _config.FileNamingStrategy.GetAllVersionsFor(chunkNum);
                if (0u >= (uint)chunks.Length)
                {
                    if (chunkNum != newLastChunkNum)
                    {
                        ThrowHelper.ThrowException_CouldnotFindAnyChunk(chunkNum);
                    }

                    break;
                }
                using (var fs = File.OpenRead(chunks[0]))
                {
                    var chunkHeader = ChunkHeader.FromStream(fs);
                    if (chunkHeader.ChunkEndNumber >= newLastChunkNum)
                    {
                        newLastChunkHeader = chunkHeader;
                        newLastChunkFilename = chunks[0];
                        break;
                    }
                    chunkNum = chunkHeader.ChunkEndNumber + 1;
                }
            }

            var infoEnabled = Log.IsInformationLevelEnabled();
            // we need to remove excessive chunks from largest number to lowest one, so in case of crash
            // mid-process, we don't end up with broken non-sequential chunks sequence.
            for (int idx = oldLastChunkNum; idx > newLastChunkNum; idx -= 1)
            {
                var chunksToDelete = _config.FileNamingStrategy.GetAllVersionsFor(idx);
                for (int chunkFileIdx = 0; chunkFileIdx < chunksToDelete.Length; chunkFileIdx++)
                {
                    string chunkFile = chunksToDelete[chunkFileIdx];
                    if (infoEnabled) Log.FileWillBeDeletedDuringTruncatedbProcedure(chunkFile);
                    File.SetAttributes(chunkFile, FileAttributes.Normal);
                    File.Delete(chunkFile);
                }
            }

            // it's not bad if there is no file, it could have been deleted on previous run
            if (newLastChunkHeader is object)
            {
                // if the chunk we want to truncate into is already scavenged 
                // we have to truncate (i.e., delete) the whole chunk, not just part of it
                if (newLastChunkHeader.IsScavenged)
                {
                    truncateChk = newLastChunkHeader.ChunkStartPosition;

                    // we need to delete EVERYTHING from ChunkStartNumber up to newLastChunkNum, inclusive
                    if (infoEnabled) { Log.SettingTruncatecheckpointAndDeletingAllChunksFromInclusively(truncateChk, newLastChunkHeader.ChunkStartNumber); }

                    for (int idx = newLastChunkNum; idx >= newLastChunkHeader.ChunkStartNumber; --idx)
                    {
                        var chunksToDelete = _config.FileNamingStrategy.GetAllVersionsFor(idx);
                        for (int chunkFileIdx = 0; chunkFileIdx < chunksToDelete.Length; chunkFileIdx++)
                        {
                            string chunkFile = chunksToDelete[chunkFileIdx];
                            if (infoEnabled) Log.FileWillBeDeletedDuringTruncatedbProcedure(chunkFile);
                            File.SetAttributes(chunkFile, FileAttributes.Normal);
                            File.Delete(chunkFile);
                        }
                    }
                }
                else
                {
                    TruncateChunkAndFillWithZeros(newLastChunkHeader, newLastChunkFilename, truncateChk);
                }
            }

            if (_config.EpochCheckpoint.Read() >= truncateChk)
            {
                if (infoEnabled) Log.TruncatingEpochFrom(_config.EpochCheckpoint.Read());
                _config.EpochCheckpoint.Write(-1);
                _config.EpochCheckpoint.Flush();
            }

            if (_config.ChaserCheckpoint.Read() > truncateChk)
            {
                if (infoEnabled) Log.TruncatingChaserFrom(_config.ChaserCheckpoint.Read(), truncateChk);
                _config.ChaserCheckpoint.Write(truncateChk);
                _config.ChaserCheckpoint.Flush();
            }

            if (_config.WriterCheckpoint.Read() > truncateChk)
            {
                if (infoEnabled) Log.TruncatingWriterFrom(_config.WriterCheckpoint.Read(), truncateChk);
                _config.WriterCheckpoint.Write(truncateChk);
                _config.WriterCheckpoint.Flush();
            }

            if (infoEnabled) Log.ResettingTruncatecheckpointTo();
            _config.TruncateCheckpoint.Write(-1);
            _config.TruncateCheckpoint.Flush();
        }

        private void TruncateChunkAndFillWithZeros(ChunkHeader chunkHeader, string chunkFilename, long truncateChk)
        {
            if (chunkHeader.IsScavenged
                || chunkHeader.ChunkStartNumber != chunkHeader.ChunkEndNumber
                || truncateChk < chunkHeader.ChunkStartPosition
                || truncateChk >= chunkHeader.ChunkEndPosition)
            {
                ThrowHelper.ThrowException_ChunkIsNotCorrectUnscavengedChunk(chunkHeader, chunkFilename, truncateChk);
            }

            File.SetAttributes(chunkFilename, FileAttributes.Normal);
            using (var fs = new FileStream(chunkFilename, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                fs.SetLength(ChunkHeader.Size + chunkHeader.ChunkSize + ChunkFooter.Size);
                fs.Position = ChunkHeader.Size + chunkHeader.GetLocalLogPosition(truncateChk);
                var zeros = new byte[65536];
                var leftToWrite = fs.Length - fs.Position;
                while (leftToWrite > 0)
                {
                    var toWrite = (int)Math.Min(leftToWrite, zeros.Length);
                    fs.Write(zeros, 0, toWrite);
                    leftToWrite -= toWrite;
                }
                fs.FlushToDisk();
            }
        }
    }
}
