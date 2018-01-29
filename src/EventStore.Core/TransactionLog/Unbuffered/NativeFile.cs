﻿using System;
using System.ComponentModel;
using System.IO;
using EventStore.Common.Utils;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

#if !DESKTOPCLR
using Mono.Unix.Native;
using Mono.Unix;
#endif

namespace EventStore.Core.TransactionLog.Unbuffered
{
    public enum ExtendedFileOptions
    {
        NoBuffering = unchecked((int)0x20000000),
        Overlapped = unchecked((int)0x40000000),
        SequentialScan = unchecked((int)0x08000000),
        WriteThrough = unchecked((int)0x80000000)

    }

    internal unsafe static class NativeFile
    {
        const uint MAC_F_NOCACHE = 48;
#if !DESKTOPCLR
        private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
#endif

        public static uint GetDriveSectorSize(string path)
        {
#if !DESKTOPCLR
            if (!IsWindows)
            {
                return 0;
            }
            else
            {
#endif
                uint size;
                uint dontcare;
                WinNative.GetDiskFreeSpace(Path.GetPathRoot(path), out dontcare, out size, out dontcare, out dontcare);
                return size;
#if !DESKTOPCLR
            }
#endif
        }

        public static long GetPageSize(string path)
        {
#if !DESKTOPCLR
            if (!IsWindows)
            {
                int r = 0;
                do
                {
                    r = (int)Syscall.sysconf(SysconfName._SC_PAGESIZE);
                } while (UnixMarshal.ShouldRetrySyscall(r));
                UnixMarshal.ThrowExceptionForLastErrorIf(r);
                return r;
            }
            else
            {
#endif
                return GetDriveSectorSize(path);
#if !DESKTOPCLR
            }
#endif
        }

        public static void SetFileSize(SafeFileHandle handle, long count)
        {
#if !DESKTOPCLR
            if (!IsWindows)
            {
                int r;
                do
                {
                    r = Syscall.ftruncate(handle.DangerousGetHandle().ToInt32(), count);
                } while (UnixMarshal.ShouldRetrySyscall(r));
                UnixMarshal.ThrowExceptionForLastErrorIf(r);
            }
            else
            {
#endif
                var low = (int)(count & 0xffffffff);
                var high = (int)(count >> 32);
                WinNative.SetFilePointer(handle, low, out high, WinNative.EMoveMethod.Begin);
                if (!WinNative.SetEndOfFile(handle))
                {
                    throw new Win32Exception();
                }
#if !DESKTOPCLR
            }
#endif

            FSync(handle);
        }

        private static void FSync(SafeFileHandle handle)
        {
#if !DESKTOPCLR
            if (!IsWindows)
            {
                Syscall.fsync(handle.DangerousGetHandle().ToInt32());
            }
            else
            {
#endif
                WinNative.FlushFileBuffers(handle);
#if !DESKTOPCLR
            }
#endif
        }


        public static void Write(SafeFileHandle handle, byte* buffer, uint count, ref int written)
        {
#if !DESKTOPCLR
            if (!IsWindows)
            {
                int ret = 0;
                do
                {
                    ret = (int)Syscall.write(handle.DangerousGetHandle().ToInt32(), buffer, count);
                } while (Mono.Unix.UnixMarshal.ShouldRetrySyscall((int)ret));
                if (ret == -1)
                    Mono.Unix.UnixMarshal.ThrowExceptionForLastErrorIf((int)ret);
                written = (int)count;
            }
            else
            {
#endif
                if (!WinNative.WriteFile(handle, buffer, count, ref written, IntPtr.Zero))
                {
                    throw new Win32Exception();
                }
#if !DESKTOPCLR
            }
#endif
        }

        public static int Read(SafeFileHandle handle, byte* buffer, int offset, int count)
        {
#if !DESKTOPCLR
            if (!IsWindows)
            {
                int r;
                do
                {
                    r = (int)Syscall.read(handle.DangerousGetHandle().ToInt32(), buffer, (ulong)count);
                } while (UnixMarshal.ShouldRetrySyscall((int)r));
                if (r == -1)
                    UnixMarshal.ThrowExceptionForLastError();
                return count;
            }
            else
            {
#endif
                var read = 0;

                if (!WinNative.ReadFile(handle, buffer, count, ref read, 0))
                {
                    throw new Win32Exception();
                }
                return read;
#if !DESKTOPCLR
            }
#endif


        }

        public static long GetFileSize(SafeFileHandle handle)
        {
#if !DESKTOPCLR
            if (!IsWindows)
            {
                Stat s;
                int r;
                do
                {
                    r = (int)Syscall.fstat(handle.DangerousGetHandle().ToInt32(), out s);
                } while (UnixMarshal.ShouldRetrySyscall(r));
                UnixMarshal.ThrowExceptionForLastErrorIf(r);
                return s.st_size;
            }
            else
            {
#endif
                long size = 0;
                if (!WinNative.GetFileSizeEx(handle, out size))
                {
                    throw new Win32Exception();
                }
                return size;
#if !DESKTOPCLR
            }
#endif

        }

        //TODO UNBUFF use FileAccess etc or do custom?
        public static SafeFileHandle Create(string path, FileAccess acc, FileShare readWrite, FileMode mode, int flags)
        {
#if !DESKTOPCLR
            if (!IsWindows)
            {
                //TODO convert flags or separate methods?
                return new SafeFileHandle((IntPtr)0, true);
            }
            else
            {
#endif
                var handle = WinNative.CreateFile(path,
                    acc,
                    FileShare.ReadWrite,
                    IntPtr.Zero,
                    mode,
                    flags,
                    IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    throw new Win32Exception();
                }
                return handle;
#if !DESKTOPCLR
            }
#endif
        }


        public static SafeFileHandle CreateUnbufferedRW(string path, FileAccess acc, FileShare share, FileMode mode, bool writeThrough)
        {
#if !DESKTOPCLR
            if (!IsWindows)
            {
                var ismac = OS.OsFlavor == OsFlavor.MacOS;
                //O_RDONLY is 0
                var direct = ismac ? OpenFlags.O_RDONLY : OpenFlags.O_DIRECT;
                var flags = GetFlags(acc, mode) | direct;
                var han = Syscall.open(path, flags, FilePermissions.S_IRWXU);
                if (han < 0)
                    throw new Win32Exception();

                var handle = new SafeFileHandle((IntPtr)han, true);
                if (handle.IsInvalid) throw new Exception("Invalid handle");
                if (ismac) TurnOffMacCaching(handle);
                return handle;
            }
            else
            {
#endif
                var flags = ExtendedFileOptions.NoBuffering;
                if (writeThrough) flags = flags | ExtendedFileOptions.WriteThrough;
                var handle = WinNative.CreateFile(path,
                    acc,
                    share,
                    IntPtr.Zero,
                    FileMode.OpenOrCreate,
                    (int)flags,
                    IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    throw new Win32Exception();
                }
                return handle;
#if !DESKTOPCLR
            }
#endif
        }

#if !DESKTOPCLR
        private static OpenFlags GetFlags(FileAccess acc, FileMode mode)
        {
            OpenFlags flags = OpenFlags.O_RDONLY; //RDONLY is 0
            if (acc == FileAccess.Read) flags |= OpenFlags.O_RDONLY;
            if (acc == FileAccess.Write) flags |= OpenFlags.O_WRONLY;
            if (acc == FileAccess.ReadWrite) flags |= OpenFlags.O_RDWR;
            if (mode == FileMode.Append) flags |= OpenFlags.O_APPEND;
            if (mode == FileMode.Create) flags |= OpenFlags.O_CREAT;
            if (mode == FileMode.CreateNew) flags |= OpenFlags.O_CREAT;
            //if (mode == FileMode.Open);
            if (mode == FileMode.Truncate) flags |= OpenFlags.O_TRUNC;

            return flags;
        }
#endif
#if !DESKTOPCLR
        [DllImport("libc")]
        static extern int fcntl(int fd, uint command, int arg);
#endif

        public static void TurnOffMacCaching(SafeFileHandle handle)
        {

            if (OS.OsFlavor != OsFlavor.MacOS) return;
#if !DESKTOPCLR
            long r = 0;
            do
            {
                r = fcntl(handle.DangerousGetHandle().ToInt32(), MAC_F_NOCACHE, 1);
            } while (UnixMarshal.ShouldRetrySyscall((int)r));
            if (r == -1)
                UnixMarshal.ThrowExceptionForLastError();
#endif

        }

        public static void Seek(SafeFileHandle handle, long position, SeekOrigin origin)
        {
#if !DESKTOPCLR
            if (!IsWindows)
            {
                int r = 0;
                do
                {
                    r = (int)Syscall.lseek(handle.DangerousGetHandle().ToInt32(), position, SeekFlags.SEEK_SET);
                } while (UnixMarshal.ShouldRetrySyscall(r));
                UnixMarshal.ThrowExceptionForLastErrorIf(r);
            }
            else
            {
#endif
                var low = (int)(position & 0xffffffff);
                var high = (int)(position >> 32);
                var f = WinNative.SetFilePointer(handle, low, out high, WinNative.EMoveMethod.Begin);
                if (f == WinNative.INVALID_SET_FILE_POINTER)
                {
                    throw new Win32Exception();
                }
#if !DESKTOPCLR
            }
#endif
        }
    }
}