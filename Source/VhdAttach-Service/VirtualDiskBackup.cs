using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace VhdAttachCommon {

    /// <summary>
    /// Verified copies of virtual disk files, taken before any operation that changes a file in place.
    /// </summary>
    internal static class VirtualDiskBackup {

        private const int BufferSize = 4 * 1024 * 1024;
        private const long SafetyMargin = 256L * 1024 * 1024; //never fill the volume completely

        /// <summary>
        /// Returns the default backup file name next to the original: "name.backup-yyyyMMdd-HHmmss.ext".
        /// </summary>
        public static string GetDefaultBackupPath(string fileName) {
            var directory = Path.GetDirectoryName(fileName);
            var name = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
            return Path.Combine(directory, name + ".backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + extension);
        }

        /// <summary>
        /// True if the destination volume has room for a copy of the file (plus a safety margin).
        /// </summary>
        public static bool HasRoomFor(string fileName, string destinationPath, out long required, out long available) {
            required = new FileInfo(fileName).Length + SafetyMargin;
            available = GetFreeSpace(destinationPath);
            return available >= required;
        }

        /// <summary>
        /// Copies the file and verifies the copy with SHA-256. Writers are blocked on the source during the copy.
        /// On any failure (including cancellation) the partial backup is removed.
        /// </summary>
        public static void Create(string fileName, string backupPath, IProgress<VirtualDiskProgress> progress, CancellationToken cancellationToken) {
            if (File.Exists(backupPath)) { throw new IOException(string.Format("Backup file \"{0}\" already exists.", backupPath)); }
            if (!HasRoomFor(fileName, backupPath, out var required, out var available)) {
                throw new IOException(string.Format(CultureInfo.CurrentCulture, "Not enough free space for a backup ({0:#,##0} MB needed, {1:#,##0} MB available).", required / 1048576, available / 1048576));
            }

            var created = false;
            try {
                byte[] sourceHash;
                using (var source = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan))
                using (var target = new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.WriteThrough))
                using (var sha = SHA256.Create()) {
                    created = true;
                    var total = source.Length * 2; //copy + verification pass
                    var buffer = new byte[BufferSize];
                    long done = 0;
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0) {
                        cancellationToken.ThrowIfCancellationRequested();
                        sha.TransformBlock(buffer, 0, read, null, 0);
                        target.Write(buffer, 0, read);
                        done += read;
                        progress?.Report(new VirtualDiskProgress(done, total));
                    }
                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    sourceHash = sha.Hash;
                    target.Flush(true);
                }

                byte[] backupHash;
                using (var verify = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan))
                using (var sha = SHA256.Create()) {
                    var total = verify.Length * 2;
                    var buffer = new byte[BufferSize];
                    long done = verify.Length;
                    int read;
                    while ((read = verify.Read(buffer, 0, buffer.Length)) > 0) {
                        cancellationToken.ThrowIfCancellationRequested();
                        sha.TransformBlock(buffer, 0, read, null, 0);
                        done += read;
                        progress?.Report(new VirtualDiskProgress(done, total));
                    }
                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    backupHash = sha.Hash;
                }

                if (!CryptographicOperations.FixedTimeEquals(sourceHash, backupHash)) {
                    throw new IOException("Backup verification failed: the copy does not match the original.");
                }
            } catch {
                if (created) {
                    try { File.Delete(backupPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                throw;
            }
        }

        public static long GetFreeSpace(string path) {
            var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(Path.GetFullPath(path));
            if (!NativeMethods.GetDiskFreeSpaceEx(directory, out var available, out _, out _)) { return long.MaxValue; } //unknown: let the copy itself fail if full
            return (long)available;
        }


        private static class NativeMethods {
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool GetDiskFreeSpaceEx(string lpDirectoryName, out ulong lpFreeBytesAvailable, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);
        }

    }
}
