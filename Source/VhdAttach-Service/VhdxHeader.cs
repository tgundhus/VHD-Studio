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
                var header = Read(fileName, denyWriters: true); //a file open for writing (VM, WSL, attached) legitimately has an active log
                return (header != null) && header.HasPendingLog;
            } catch (IOException) {
                return false;
            } catch (UnauthorizedAccessException) {
                return false;
            }
        }

        /// <summary>
        /// Reads the current header of a VHDX file, or null if it is not VHDX.
        /// </summary>
        public static VhdxHeader Read(string fileName, bool denyWriters = false) {
            var buffer = ReadBytes(fileName, 0, (int)(HeaderOffsets[1] + HeaderSize), denyWriters);
            return (buffer == null) ? null : Parse(buffer);
        }

        /// <summary>
        /// Returns the parent linkage GUIDs stored in a differencing VHDX ("parent_linkage" and the optional "parent_linkage2"),
        /// or null if the file has no VHDX parent locator. A valid parent's DataWriteGuid equals one of them.
        /// </summary>
        public static Guid[] ReadParentLinkage(string fileName) {
            var regions = ReadBytes(fileName, RegionTableOffset, 64 * 1024, false);
            if ((regions == null) || (Encoding.ASCII.GetString(regions, 0, 4) != "regi")) { return null; }
            var entryCount = BitConverter.ToInt32(regions, 8);
            long metadataOffset = -1;
            for (int i = 0; (i < entryCount) && (i < 2047); i++) {
                var entry = 16 + i * 32;
                if (ReadGuid(regions, entry) == MetadataRegionGuid) { metadataOffset = BitConverter.ToInt64(regions, entry + 16); break; }
            }
            if (metadataOffset < 0) { return null; }

            var table = ReadBytes(fileName, metadataOffset, 64 * 1024, false);
            if ((table == null) || (Encoding.ASCII.GetString(table, 0, 8) != "metadata")) { return null; }
            var itemCount = BitConverter.ToUInt16(table, 10);
            for (int i = 0; (i < itemCount) && (i < 2047); i++) {
                var entry = 32 + i * 32;
                if (ReadGuid(table, entry) != ParentLocatorItemGuid) { continue; }
                var itemOffset = BitConverter.ToUInt32(table, entry + 16);
                var itemLength = (int)BitConverter.ToUInt32(table, entry + 20);
                var locator = ReadBytes(fileName, metadataOffset + itemOffset, Math.Min(itemLength, 1024 * 1024), false);
                if (locator == null) { return null; }
                var keyValueCount = BitConverter.ToUInt16(locator, 18);
                var result = new System.Collections.Generic.List<Guid>();
                for (int k = 0; k < keyValueCount; k++) {
                    var kv = 20 + k * 12;
                    var key = Encoding.Unicode.GetString(locator, (int)BitConverter.ToUInt32(locator, kv), BitConverter.ToUInt16(locator, kv + 8));
                    var value = Encoding.Unicode.GetString(locator, (int)BitConverter.ToUInt32(locator, kv + 4), BitConverter.ToUInt16(locator, kv + 10));
                    if (((key == "parent_linkage") || (key == "parent_linkage2")) && Guid.TryParse(value, out var linkage)) { result.Add(linkage); }
                }
                return result.ToArray();
            }
            return null;
        }

        private const long RegionTableOffset = 192 * 1024;
        private static readonly Guid MetadataRegionGuid = new Guid("8B7CA206-4790-4B9A-B8FE-575F050F886E");
        private static readonly Guid ParentLocatorItemGuid = new Guid("A8D35F2D-B30B-454D-ABF7-D3D84834AB0C");

        private static byte[] ReadBytes(string fileName, long offset, int count, bool denyWriters) {
            using (var stream = new FileStream(fileName, FileMode.Open, FileAccess.Read, denyWriters ? FileShare.Read : (FileShare.ReadWrite | FileShare.Delete))) {
                if (stream.Length < offset + Math.Min(count, 4096)) { return null; }
                stream.Position = offset;
                var buffer = new byte[(int)Math.Min(count, stream.Length - offset)];
                var read = 0;
                while (read < buffer.Length) {
                    var n = stream.Read(buffer, read, buffer.Length - read);
                    if (n == 0) { break; }
                    read += n;
                }
                return (read == buffer.Length) ? buffer : null;
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
