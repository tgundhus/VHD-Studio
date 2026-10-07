using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace VhdAttachCommon {

    /// <summary>
    /// Verified copies of virtual disk files, taken before any operation that changes a file in place.
    /// Verification uses XXH128: SHA-256 manages about 2 GB/s per core, below NVMe speed, and copy verification
    /// guards against accidental corruption (not tampering), for which a 128-bit checksum is ample.
    /// </summary>
    internal static class VirtualDiskBackup {

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
        /// Copies the file and verifies the copy with a 128-bit XXH128 checksum (fast enough to never limit disk speed). Writers are blocked on the source during the copy.
        /// Reading, hashing and writing are pipelined; the copy is flushed to disk once and then re-read
        /// bypassing the file cache, so verification checks what is really on disk.
        /// On any failure (including cancellation) the partial backup is removed.
        /// </summary>
        public static void Create(string fileName, string backupPath, IProgress<VirtualDiskProgress> progress, CancellationToken cancellationToken) {
            if (File.Exists(backupPath)) { throw new IOException(string.Format("Backup file \"{0}\" already exists.", backupPath)); }
            if (!HasRoomFor(fileName, backupPath, out var required, out var available)) {
                throw new IOException(string.Format(CultureInfo.CurrentCulture, "Not enough free space for a backup ({0:#,##0} MB needed, {1:#,##0} MB available).", required / 1048576, available / 1048576));
            }

            var created = false;
            var phase = System.Diagnostics.Stopwatch.StartNew();
            try {
                byte[] sourceHash;
                long length;
                using (var source = OpenForSequentialRead(fileName, FileShare.Read)) { //FileShare.Read: no writer may change the source mid-copy
                    length = RandomAccess.GetLength(source);
                    var total = length * 2; //copy + verification pass
                    using (File.OpenHandle(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                    created = true; //from here on the file is ours
                    PlainFile.MakePlain(backupPath); //folders can force compression or encryption, which Windows refuses for virtual disks
                    using (var target = OpenForSequentialWrite(backupPath, length, out var unbuffered))
                    {
                        var hash = new System.IO.Hashing.XxHash128();
                        long readOffset = 0, writeOffset = 0;
                        Pipeline(
                            block => { //reader thread: read and hash
                                var n = RandomAccess.Read(source, block.AsSpan(), readOffset);
                                readOffset += n;
                                if (n > 0) { hash.Append(block.AsSpan(0, n)); }
                                return n;
                            },
                            (block, n) => { //writer: overlaps with the next read
                                var count = n;
                                if (unbuffered && ((count % Sector) != 0)) { //unbuffered writes must be whole sectors: pad the last block, trim afterwards
                                    var padded = (count + Sector - 1) / Sector * Sector;
                                    block.AsSpan(count, padded - count).Clear();
                                    count = padded;
                                }
                                RandomAccess.Write(target, block.AsSpan(0, count), writeOffset);
                                writeOffset += n;
                                progress?.Report(new VirtualDiskProgress(writeOffset, total));
                            },
                            cancellationToken);
                        if (writeOffset != length) { throw new IOException("The source changed size during the backup."); }
                        var copyTime = phase.Elapsed;
                        if (RandomAccess.GetLength(target) != length) { RandomAccess.SetLength(target, length); } //drop the padding of the last sector
                        if (!NativeMethods.FlushFileBuffers(target)) { throw new System.ComponentModel.Win32Exception(); } //one flush instead of write-through per block
                        LastTimings = (copyTime, phase.Elapsed - copyTime, TimeSpan.Zero);
                        sourceHash = hash.GetHashAndReset();
                    }
                }

                byte[] backupHash;
                using (var verify = OpenForSequentialRead(backupPath, FileShare.Read))
                {
                        var hash = new System.IO.Hashing.XxHash128();
                    if (RandomAccess.GetLength(verify) != length) { throw new IOException("Backup verification failed: the copy has a different size."); }
                    long offset = 0, hashed = 0;
                    Pipeline(
                        block => {
                            var n = RandomAccess.Read(verify, block.AsSpan(), offset);
                            offset += n;
                            return n;
                        },
                        (block, n) => {
                            hash.Append(block.AsSpan(0, n));
                            hashed += n;
                            progress?.Report(new VirtualDiskProgress(length + hashed, length * 2));
                        },
                        cancellationToken);
                    backupHash = hash.GetHashAndReset();
                }
                LastTimings = (LastTimings.Copy, LastTimings.Flush, phase.Elapsed - LastTimings.Copy - LastTimings.Flush);

                if (!CryptographicOperations.FixedTimeEquals(sourceHash, backupHash)) {
                    throw new IOException("Backup verification failed: the copy does not match the original.");
                }
                EnsureUsableAsVirtualDisk(fileName, backupPath);
            } catch {
                if (created) {
                    try { File.Delete(backupPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                throw;
            }
        }

        /// <summary>
        /// Duration of the phases of the last backup (diagnostics and benchmarks).
        /// </summary>
        internal static (TimeSpan Copy, TimeSpan Flush, TimeSpan Verify) LastTimings;

        private const int BlockSize = 16 * 1024 * 1024; //multiple of every sector size
        private const int BlockCount = 4;
        private const int Sector = 4096; //covers 512-byte and 4K-native disks
        private const FileOptions NoBuffering = (FileOptions)0x20000000; //FILE_FLAG_NO_BUFFERING

        /// <summary>
        /// Opens for unbuffered sequential reading (reads the device, not the cache, and does not evict other data);
        /// falls back to buffered reads where unbuffered I/O is not supported.
        /// </summary>
        private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenForSequentialRead(string fileName, FileShare share) {
            try {
                return File.OpenHandle(fileName, FileMode.Open, FileAccess.Read, share, NoBuffering | FileOptions.SequentialScan);
            } catch (IOException ex) when (ex.HResult == unchecked((int)0x80070057)) { //ERROR_INVALID_PARAMETER: file system without unbuffered support
                return File.OpenHandle(fileName, FileMode.Open, FileAccess.Read, share, FileOptions.SequentialScan);
            }
        }

        /// <summary>
        /// Opens the (just created, plain, empty) backup file for unbuffered writing, straight to the device like Explorer
        /// does for large files, so a multi-gigabyte copy does not flush everything else out of memory; falls back to
        /// buffered writes. The full length is reserved up front to keep the file contiguous.
        /// </summary>
        private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenForSequentialWrite(string fileName, long length, out bool unbuffered) {
            Microsoft.Win32.SafeHandles.SafeFileHandle handle;
            try {
                unbuffered = true;
                handle = File.OpenHandle(fileName, FileMode.Open, FileAccess.ReadWrite, FileShare.None, NoBuffering);
            } catch (IOException ex) when (ex.HResult == unchecked((int)0x80070057)) { //ERROR_INVALID_PARAMETER: no unbuffered support
                unbuffered = false;
                handle = File.OpenHandle(fileName, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            RandomAccess.SetLength(handle, length);
            return handle;
        }

        /// <summary>
        /// A backup of a virtual disk is only useful if Windows can open it as one. Checked after the checksum matched.
        /// </summary>
        private static void EnsureUsableAsVirtualDisk(string sourceFileName, string backupPath) {
            var extension = Path.GetExtension(sourceFileName).ToLowerInvariant();
            if ((extension != ".vhd") && (extension != ".vhdx") && (extension != ".avhd") && (extension != ".avhdx")) { return; }
            var problem = PlainFile.GetProblem(backupPath);
            if (problem != null) { throw new IOException("The backup was copied correctly, but Windows cannot use it as a virtual disk because " + problem + "."); }
            try {
                VirtualDiskImage.GetDetails(backupPath);
            } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is NotSupportedException || ex is System.ComponentModel.Win32Exception) {
                throw new IOException("The backup was copied correctly, but Windows cannot open it as a virtual disk: " + ex.Message, ex);
            }
        }

        /// <summary>
        /// Producer/consumer over a few sector-aligned blocks: the producer runs on its own thread so that
        /// reading overlaps with hashing or writing. Exceptions from either side are propagated.
        /// </summary>
        private static void Pipeline(Func<AlignedBlock, int> produce, Action<AlignedBlock, int> consume, CancellationToken cancellationToken) {
            using (var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (var free = new System.Collections.Concurrent.BlockingCollection<AlignedBlock>())
            using (var full = new System.Collections.Concurrent.BlockingCollection<(AlignedBlock Block, int Count)>(BlockCount)) {
                for (int i = 0; i < BlockCount; i++) { free.Add(new AlignedBlock(BlockSize)); }
                var producer = System.Threading.Tasks.Task.Factory.StartNew(() => {
                    try {
                        while (true) {
                            var block = free.Take(stop.Token);
                            var n = produce(block);
                            if (n <= 0) { break; }
                            full.Add((block, n), stop.Token);
                        }
                    } finally {
                        full.CompleteAdding();
                    }
                }, stop.Token, System.Threading.Tasks.TaskCreationOptions.LongRunning, System.Threading.Tasks.TaskScheduler.Default);
                try {
                    foreach (var item in full.GetConsumingEnumerable(stop.Token)) {
                        consume(item.Block, item.Count);
                        free.Add(item.Block);
                    }
                } catch {
                    stop.Cancel();
                    try { producer.Wait(); } catch (AggregateException) { }
                    throw;
                }
                try {
                    producer.Wait();
                } catch (AggregateException ex) {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).Throw();
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        /// <summary>
        /// Buffer whose start is aligned to 4 KiB, as unbuffered I/O requires (pinned, so the address never moves).
        /// </summary>
        private sealed class AlignedBlock {
            private const int Alignment = 4096;
            private readonly byte[] Storage;
            private readonly int Offset;
            public readonly int Length;

            public AlignedBlock(int length) {
                this.Storage = GC.AllocateArray<byte>(length + Alignment, pinned: true);
                var address = (long)System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(this.Storage, 0);
                this.Offset = (int)((Alignment - (address % Alignment)) % Alignment);
                this.Length = length;
            }

            public Span<byte> AsSpan() => new Span<byte>(this.Storage, this.Offset, this.Length);
            public Span<byte> AsSpan(int start, int count) => new Span<byte>(this.Storage, this.Offset + start, count);
        }
        public static long GetFreeSpace(string path) {
            var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(Path.GetFullPath(path));
            if (!NativeMethods.GetDiskFreeSpaceEx(directory, out var available, out _, out _)) { return long.MaxValue; } //unknown: let the copy itself fail if full
            return (long)available;
        }


        private static class NativeMethods {
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool FlushFileBuffers(Microsoft.Win32.SafeHandles.SafeFileHandle hFile);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool GetDiskFreeSpaceEx(string lpDirectoryName, out ulong lpFreeBytesAvailable, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);
        }

    }
}
