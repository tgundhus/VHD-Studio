using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VhdAttachCommon {

    /// <summary>
    /// Windows only accepts virtual disk files that are uncompressed, unencrypted and not sparse.
    /// Files inherit compression and encryption from their folder, so backups and copies are normalised here.
    /// </summary>
    internal static class PlainFile {

        private const FileAttributes Blocking = FileAttributes.Compressed | FileAttributes.Encrypted | FileAttributes.SparseFile;

        /// <summary>
        /// Returns a description of what prevents the file from being used as a virtual disk, or null if nothing does.
        /// </summary>
        public static string GetProblem(string path) {
            var attributes = File.GetAttributes(path) & Blocking;
            if (attributes == 0) { return null; }
            var parts = new System.Collections.Generic.List<string>();
            if ((attributes & FileAttributes.Compressed) != 0) { parts.Add("compressed"); }
            if ((attributes & FileAttributes.Encrypted) != 0) { parts.Add("encrypted"); }
            if ((attributes & FileAttributes.SparseFile) != 0) { parts.Add("sparse"); }
            return "the file is " + string.Join(" and ", parts);
        }

        /// <summary>
        /// Removes compression, sparseness and encryption in place; the content does not change.
        /// Can take a while for large files (the data is rewritten). Throws if the result is still not plain.
        /// </summary>
        public static void MakePlain(string path) {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Encrypted) != 0) {
                File.Decrypt(path); //EFS: only the owner (or a recovery agent) can do this
            }
            if ((File.GetAttributes(path) & (FileAttributes.Compressed | FileAttributes.SparseFile)) != 0) {
                using (var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) {
                    if ((File.GetAttributes(path) & FileAttributes.Compressed) != 0) {
                        ushort none = 0; //COMPRESSION_FORMAT_NONE: the file system rewrites the data uncompressed
                        int returned = 0;
                        if (!NativeMethods.DeviceIoControl(handle, NativeMethods.FSCTL_SET_COMPRESSION, ref none, sizeof(ushort), IntPtr.Zero, 0, ref returned, IntPtr.Zero)) { throw new Win32Exception(); }
                    }
                    if ((File.GetAttributes(path) & FileAttributes.SparseFile) != 0) {
                        byte setSparse = 0; //FILE_SET_SPARSE_BUFFER { BOOLEAN SetSparse = FALSE }: holes are allocated
                        int returned = 0;
                        if (!NativeMethods.DeviceIoControl(handle, NativeMethods.FSCTL_SET_SPARSE, ref setSparse, 1, IntPtr.Zero, 0, ref returned, IntPtr.Zero)) { throw new Win32Exception(); }
                    }
                }
            }
            var problem = GetProblem(path);
            if (problem != null) { throw new IOException("Windows could not make the file usable as a virtual disk: " + problem + "."); }
        }


        private static class NativeMethods {
            public const uint FSCTL_SET_COMPRESSION = 0x0009C040;
            public const uint FSCTL_SET_SPARSE = 0x000900C4;

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, ref ushort lpInBuffer, int nInBufferSize, IntPtr lpOutBuffer, int nOutBufferSize, ref int lpBytesReturned, IntPtr lpOverlapped);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, ref byte lpInBuffer, int nInBufferSize, IntPtr lpOutBuffer, int nOutBufferSize, ref int lpBytesReturned, IntPtr lpOverlapped);
        }

    }
}
