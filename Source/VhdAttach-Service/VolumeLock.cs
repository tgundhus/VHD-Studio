using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace VhdAttachCommon {

    /// <summary>
    /// Thrown when a volume cannot be locked because files on it are open.
    /// </summary>
    internal sealed class VolumeInUseException : InvalidOperationException {
        public VolumeInUseException(IList<string> volumes)
            : base(string.Format("Files are open on {0}. Close all programs and Explorer windows using {1} and try again; continuing anyway may lose unsaved data.", string.Join(", ", volumes), (volumes.Count == 1) ? "it" : "them")) {
            this.Volumes = volumes;
        }
        public IList<string> Volumes { get; }
    }


    /// <summary>
    /// Exclusive lock on one or more volumes: flushes, locks and dismounts them so a disk can be detached
    /// or changed without losing buffered writes. Equivalent to "Safely remove hardware".
    /// Dispose releases the locks.
    /// </summary>
    internal sealed class VolumeLock : IDisposable {

        private readonly List<SafeFileHandle> Handles = new List<SafeFileHandle>();

        private VolumeLock() { }

        /// <summary>
        /// Locks all given volumes (\\?\Volume{GUID}\ names).
        /// </summary>
        /// <param name="volumeNames">Volume GUID paths.</param>
        /// <param name="displayNames">Names used in error messages (same order).</param>
        /// <param name="force">If true, volumes that cannot be locked are still flushed and dismounted (open handles become invalid).</param>
        /// <exception cref="VolumeInUseException">Volume is in use and force is false.</exception>
        public static VolumeLock Acquire(IList<string> volumeNames, IList<string> displayNames, bool force) {
            var result = new VolumeLock();
            var inUse = new List<string>();
            try {
                for (int i = 0; i < volumeNames.Count; i++) {
                    var handle = OpenVolume(volumeNames[i]);
                    if (handle == null) { continue; } //volume vanished
                    result.Handles.Add(handle);
                    NativeMethods.FlushFileBuffers(handle); //best effort; fails on read-only volumes
                    if (!TryLock(handle)) {
                        if (!force) { inUse.Add(displayNames[i]); continue; }
                    }
                }
                if (inUse.Count > 0) { throw new VolumeInUseException(inUse); }
                foreach (var handle in result.Handles) {
                    int bytes = 0;
                    NativeMethods.DeviceIoControl(handle, NativeMethods.FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, ref bytes, IntPtr.Zero);
                }
                return result;
            } catch {
                result.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Returns display names of volumes that have open files. Does not keep any lock.
        /// </summary>
        public static IList<string> FindInUse(IList<string> volumeNames, IList<string> displayNames) {
            var inUse = new List<string>();
            for (int i = 0; i < volumeNames.Count; i++) {
                using (var handle = OpenVolume(volumeNames[i])) {
                    if (handle == null) { continue; }
                    if (TryLock(handle)) {
                        int bytes = 0;
                        NativeMethods.DeviceIoControl(handle, NativeMethods.FSCTL_UNLOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, ref bytes, IntPtr.Zero);
                    } else {
                        inUse.Add(displayNames[i]);
                    }
                }
            }
            return inUse;
        }

        private static SafeFileHandle OpenVolume(string volumeName) {
            var path = volumeName.TrimEnd('\\');
            var handle = NativeMethods.CreateFile(path, NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE, NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE, IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid) {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                if ((error == 2) || (error == 3) || (error == 21)) { return null; } //not found / not ready
                if (error == 19) { return null; } //write-protected (read-only attach): nothing buffered that could be lost
                throw new Win32Exception(error);
            }
            return handle;
        }

        private static bool TryLock(SafeFileHandle handle) {
            for (int attempt = 0; attempt < 10; attempt++) { //Explorer, indexer and AV scanners hold short-lived handles
                int bytes = 0;
                if (NativeMethods.DeviceIoControl(handle, NativeMethods.FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, ref bytes, IntPtr.Zero)) { return true; }
                Thread.Sleep(200);
            }
            return false;
        }

        public void Dispose() {
            foreach (var handle in this.Handles) { handle.Dispose(); } //closing the handle releases the lock
            this.Handles.Clear();
        }


        private static class NativeMethods {
            public const uint GENERIC_READ = 0x80000000;
            public const uint GENERIC_WRITE = 0x40000000;
            public const uint FILE_SHARE_READ = 0x1;
            public const uint FILE_SHARE_WRITE = 0x2;
            public const uint OPEN_EXISTING = 3;
            public const uint FSCTL_LOCK_VOLUME = 0x00090018;
            public const uint FSCTL_UNLOCK_VOLUME = 0x0009001C;
            public const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer, int nInBufferSize, IntPtr lpOutBuffer, int nOutBufferSize, ref int lpBytesReturned, IntPtr lpOverlapped);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool FlushFileBuffers(SafeFileHandle hFile);
        }

    }
}
