using System;
using System.IO;
using System.Text;

namespace VhdAttachCommon {

    /// <summary>
    /// Minimal reader for the VHDX header region (MS-VHDX 2.2).
    /// Used to detect a non-empty log, which Windows must replay (read/write open) before the file can be attached.
    /// </summary>
    internal sealed class VhdxHeader {

        private const int FileIdentifierSize = 64 * 1024;
        private const int HeaderSize = 4 * 1024;
        private static readonly long[] HeaderOffsets = { 64 * 1024, 128 * 1024 };

        private VhdxHeader(long sequenceNumber, Guid fileWriteGuid, Guid dataWriteGuid, Guid logGuid, int logLength, long logOffset) {
            this.SequenceNumber = sequenceNumber;
            this.FileWriteGuid = fileWriteGuid;
            this.DataWriteGuid = dataWriteGuid;
            this.LogGuid = logGuid;
            this.LogLength = logLength;
            this.LogOffset = logOffset;
        }

        public long SequenceNumber { get; }
        public Guid FileWriteGuid { get; }
        public Guid DataWriteGuid { get; }
        public Guid LogGuid { get; }
        public int LogLength { get; }
        public long LogOffset { get; }

        /// <summary>
        /// Gets whether the log contains entries that still need to be replayed.
        /// </summary>
        public bool HasPendingLog => this.LogGuid != Guid.Empty;


        /// <summary>
        /// Returns the current (highest sequence number) header or null if data is not a VHDX file.
        /// </summary>
        /// <param name="buffer">First 192 KB (or more) of the file.</param>
        public static VhdxHeader Parse(byte[] buffer) {
            if ((buffer == null) || (buffer.Length < HeaderOffsets[1] + HeaderSize)) { return null; }
            if (Encoding.ASCII.GetString(buffer, 0, 8) != "vhdxfile") { return null; }

            VhdxHeader current = null;
            foreach (var offset in HeaderOffsets) {
                var header = ParseHeader(buffer, (int)offset);
                if ((header != null) && ((current == null) || (header.SequenceNumber > current.SequenceNumber))) {
                    current = header;
                }
            }
            return current;
        }

        /// <summary>
        /// Returns true if file is VHDX whose log has not been replayed. Never throws.
        /// </summary>
        public static bool NeedsLogReplay(string fileName) {
            try {
                if (!VirtualDiskImage.IsVhdx(fileName)) { return false; }
                var buffer = new byte[HeaderOffsets[1] + HeaderSize];
                using (var stream = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                    var read = 0;
                    while (read < buffer.Length) {
                        var n = stream.Read(buffer, read, buffer.Length - read);
                        if (n == 0) { return false; }
                        read += n;
                    }
                }
                var header = Parse(buffer);
                return (header != null) && header.HasPendingLog;
            } catch (IOException) {
                return false;
            } catch (UnauthorizedAccessException) {
                return false;
            }
        }


        private static VhdxHeader ParseHeader(byte[] buffer, int offset) {
            if (Encoding.ASCII.GetString(buffer, offset, 4) != "head") { return null; }

            var storedChecksum = BitConverter.ToUInt32(buffer, offset + 4);
            var copy = new byte[HeaderSize];
            Buffer.BlockCopy(buffer, offset, copy, 0, HeaderSize);
            copy[4] = copy[5] = copy[6] = copy[7] = 0;
            if (Crc32C(copy) != storedChecksum) { return null; } //torn or corrupted header; the other copy wins

            return new VhdxHeader(
                BitConverter.ToInt64(buffer, offset + 8),
                ReadGuid(buffer, offset + 16),
                ReadGuid(buffer, offset + 32),
                ReadGuid(buffer, offset + 48),
                BitConverter.ToInt32(buffer, offset + 68),
                BitConverter.ToInt64(buffer, offset + 72));
        }

        private static Guid ReadGuid(byte[] buffer, int offset) {
            var bytes = new byte[16];
            Buffer.BlockCopy(buffer, offset, bytes, 0, 16);
            return new Guid(bytes);
        }

        internal static uint Crc32C(byte[] data) {
            var crc = 0xFFFFFFFFu;
            foreach (var b in data) {
                crc ^= b;
                for (int k = 0; k < 8; k++) {
                    crc = ((crc & 1) != 0) ? (crc >> 1) ^ 0x82F63B78u : crc >> 1;
                }
            }
            return ~crc;
        }

    }

}
