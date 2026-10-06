using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VhdAttachCommon {

    /// <summary>
    /// Validates a path before the service uses it: no junctions, symbolic links or mount points in any component,
    /// optionally no hard links, and the path must resolve to itself. This does not pin the file (an attribute-only
    /// handle takes no part in sharing checks); race-free identity comes from opening the virtual disk under the
    /// caller's own token (see AttachHelper).
    /// </summary>
    internal sealed class PathGuard : IDisposable {

        private readonly SafeFileHandle Handle;

        private PathGuard(SafeFileHandle handle, string finalPath) {
            this.Handle = handle;
            this.FinalPath = finalPath;
        }

        /// <summary>
        /// Path resolved from the open handle; this is what the service must use.
        /// </summary>
        public string FinalPath { get; }

        /// <param name="path">Fully qualified local path.</param>
        /// <param name="directory">True to pin a directory.</param>
        /// <param name="rejectHardLinks">Refuse files with more than one hard link (a link can point at someone else's data).</param>
        public static PathGuard Open(string path, bool directory, bool rejectHardLinks) {
            if (!Path.IsPathFullyQualified(path)) { throw new ArgumentException(string.Format("\"{0}\" is not a full path.", path)); }
            var fullPath = GetLongPath(Path.GetFullPath(path)).TrimEnd('\\');
            EnsureNoReparsePoints(fullPath, includeLast: true);

            var flags = NativeMethods.FILE_FLAG_OPEN_REPARSE_POINT | (directory ? NativeMethods.FILE_FLAG_BACKUP_SEMANTICS : 0);
            var handle = NativeMethods.CreateFile(fullPath, NativeMethods.FILE_READ_ATTRIBUTES, NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE, IntPtr.Zero, NativeMethods.OPEN_EXISTING, flags, IntPtr.Zero); 
            if (handle.IsInvalid) {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                if ((error == 2) || (error == 3)) { throw directory ? (Exception)new DirectoryNotFoundException(string.Format("\"{0}\" does not exist.", path)) : new FileNotFoundException(string.Format("\"{0}\" does not exist.", path), path); }
                throw new Win32Exception(error, string.Format("{0} ({1})", new Win32Exception(error).Message, path));
            }
            try {
                if (!NativeMethods.GetFileInformationByHandle(handle, out var info)) { throw new Win32Exception(); }
                var isDirectory = (info.FileAttributes & (uint)FileAttributes.Directory) != 0;
                if (isDirectory != directory) { throw new IOException(string.Format("\"{0}\" is not a {1}.", path, directory ? "folder" : "file")); }
                if ((info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0) { throw new UnauthorizedAccessException(string.Format("\"{0}\" is a link or mount point.", path)); }
                if (!directory && rejectHardLinks && (info.NumberOfLinks > 1)) { throw new UnauthorizedAccessException(string.Format("\"{0}\" has multiple hard links and cannot be used.", path)); }

                var finalPath = GetFinalPath(handle);
                if (!string.Equals(finalPath.TrimEnd('\\'), fullPath, StringComparison.OrdinalIgnoreCase)) {
                    throw new UnauthorizedAccessException(string.Format("\"{0}\" resolves to a different location (\"{1}\").", path, finalPath));
                }
                return new PathGuard(handle, finalPath);
            } catch {
                handle.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Throws if any component of the path (optionally excluding the last) is a junction, symbolic link or mount point.
        /// </summary>
        public static void EnsureNoReparsePoints(string fullPath, bool includeLast) {
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) { throw new ArgumentException(string.Format("\"{0}\" must be on a local drive.", fullPath)); }
            var current = root;
            var parts = fullPath.Substring(root.Length).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++) {
                if ((i == parts.Length - 1) && !includeLast) { break; }
                current = Path.Combine(current, parts[i]);
                var attributes = NativeMethods.GetFileAttributes(current);
                if (attributes == uint.MaxValue) { continue; } //missing: reported by the caller's open
                if ((attributes & (uint)FileAttributes.ReparsePoint) != 0) {
                    throw new UnauthorizedAccessException(string.Format("\"{0}\" is a junction, link or mount point; paths through links are not accepted.", current));
                }
            }
        }

        internal static string GetLongPath(string path) {
            var buffer = new System.Text.StringBuilder(1024);
            var length = NativeMethods.GetLongPathName(path, buffer, buffer.Capacity);
            return ((length > 0) && (length < buffer.Capacity)) ? buffer.ToString() : path; //8.3 names would not match the final path
        }

        private static string GetFinalPath(SafeFileHandle handle) {
            var buffer = new System.Text.StringBuilder(1024);
            var length = NativeMethods.GetFinalPathNameByHandle(handle, buffer, buffer.Capacity, 0);
            if (length == 0) { throw new Win32Exception(); }
            if (length > buffer.Capacity) {
                buffer.Capacity = (int)length + 1;
                length = NativeMethods.GetFinalPathNameByHandle(handle, buffer, buffer.Capacity, 0);
                if (length == 0) { throw new Win32Exception(); }
            }
            var path = buffer.ToString();
            return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path.Substring(4) : path;
        }

        public void Dispose() {
            this.Handle.Dispose();
        }


        private static class NativeMethods {
            public const uint FILE_READ_ATTRIBUTES = 0x80;
            public const uint FILE_SHARE_READ = 0x1;
            public const uint FILE_SHARE_WRITE = 0x2;
            public const uint OPEN_EXISTING = 3;
            public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
            public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

            [StructLayout(LayoutKind.Sequential)]
            public struct BY_HANDLE_FILE_INFORMATION {
                public uint FileAttributes;
                public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
                public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
                public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
                public uint VolumeSerialNumber;
                public uint FileSizeHigh;
                public uint FileSizeLow;
                public uint NumberOfLinks;
                public uint FileIndexHigh;
                public uint FileIndexLow;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern uint GetFinalPathNameByHandle(SafeFileHandle hFile, System.Text.StringBuilder lpszFilePath, int cchFilePath, uint dwFlags);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern uint GetFileAttributes(string lpFileName);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern uint GetLongPathName(string lpszShortPath, System.Text.StringBuilder lpszLongPath, int cchBuffer);
        }

    }
}
