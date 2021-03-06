﻿using System;
using System.IO;

namespace EventStore.ClientAPI.Transport.Http
{
    internal class AsyncStreamCopier<T>
    {
        public event EventHandler Completed;

        public T AsyncState { get; private set; }
        public Exception Error { get; private set; }

        private readonly byte[] _buffer = new byte[4096];
        private readonly Stream _input;
        private readonly Stream _output;

        public AsyncStreamCopier(Stream input, Stream output, T state)
        {
            if (input is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.input); }
            if (output is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.output); }

            _input = input;
            _output = output;

            AsyncState = state;
            Error = null;
        }

        public void Start()
        {
            GetNextChunk();
        }

        private void GetNextChunk()
        {
            try
            {
                _input.BeginRead(_buffer, 0, _buffer.Length, InputReadCompleted, null);
            }
            catch (Exception e)
            {
                Error = e;
                OnCompleted();
            }
        }

        private void InputReadCompleted(IAsyncResult ar)
        {
            try
            {
                int bytesRead = _input.EndRead(ar);
                if ((uint)(bytesRead - 1) >= Consts.TooBigOrNegative) //mono can return -1
                {
                    OnCompleted();
                    return;
                }

                _output.BeginWrite(_buffer, 0, bytesRead, OutputWriteCompleted, null);
            }
            catch (Exception e)
            {
                Error = e;
                OnCompleted();
            }
        }

        private void OutputWriteCompleted(IAsyncResult ar)
        {
            try
            {
                _output.EndWrite(ar);
                GetNextChunk();
            }
            catch (Exception e)
            {
                Error = e;
                OnCompleted();
            }
        }

        private void OnCompleted()
        {
            if (Completed is object)
                Completed(this, EventArgs.Empty);
        }
    }
}