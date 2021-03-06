﻿using System;
using System.Runtime.CompilerServices;
using System.Text;
using CuteAnt.Pool;
using EventStore.ClientAPI.Internal;

namespace EventStore.ClientAPI.Common.Utils
{
    static class Helper
    {
        public static readonly UTF8Encoding UTF8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        public static void EatException(Action action)
        {
            if (action is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.action); }
            try
            {
                action();
            }
            catch (Exception)
            {
            }
        }

        public static T EatException<T>(Func<T> func, T defaultValue = default(T))
        {
            if (func is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.func); }
            try
            {
                return func();
            }
            catch (Exception)
            {
                return defaultValue;
            }
        }

        public static string FormatBinaryDump(byte[] logBulk)
        {
            return FormatBinaryDump(new ArraySegment<byte>(logBulk ?? Empty.ByteArray));
        }

        public static string FormatBinaryDump(ArraySegment<byte> logBulk)
        {
            if (0u >= (uint)logBulk.Count)
                return "--- NO DATA ---";

            var sb = StringBuilderManager.Allocate();
            int cur = 0;
            int len = logBulk.Count;
            for (int row = 0, rows = (logBulk.Count + 15) / 16; row < rows; ++row)
            {
                sb.AppendFormat("{0:000000}:", row * 16);
                for (int i = 0; i < 16; ++i, ++cur)
                {
                    if (cur >= len)
                        sb.Append("   ");
                    else
                        sb.AppendFormat(" {0:X2}", logBulk.Array[logBulk.Offset + cur]);
                }
                sb.Append("  | ");
                cur -= 16;
                for (int i = 0; i < 16; ++i, ++cur)
                {
                    if (cur < len)
                    {
                        var b = (char)logBulk.Array[logBulk.Offset + cur];
                        sb.Append(char.IsControl(b) ? '.' : b);
                    }
                }
                sb.AppendLine();
            }
            return StringBuilderManager.ReturnAndFree(sb);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsInvalidEventNumber(long value)
            => (ulong)(value - StreamPosition.End) > 9223372036854775808ul/*unchecked((ulong)(long.MaxValue - StreamPosition.End))*/;
    }
}