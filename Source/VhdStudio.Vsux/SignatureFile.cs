using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VhdStudio.Vsux;

/// <summary>The only raw image writer. All writes are exactly eight bytes at offset zero.</summary>
internal sealed class SignatureFile : IDisposable {
    internal const int SnapshotSize = 1024 * 1024;
    private static readonly byte[] Normal = "vhdxfile"u8.ToArray();
    private static readonly byte[] Masked = "vsuxlock"u8.ToArray();
    private readonly FileStream stream;
    private readonly bool exclusive;
    public Guid DiskId { get; private set; }
    public bool IsLocked { get; private set; }
    public long Length { get; }
    public string FileIdentity { get; }

    private SignatureFile(FileStream stream, bool allowDamagedSignature, bool allowPendingLog, bool exclusive) {
        this.stream = stream;
        this.exclusive = exclusive;
        Length = stream.Length;
        if (!Native.GetFileInformationByHandle(stream.SafeFileHandle, out var info)) { throw new Win32Exception(); }
        if (info.NumberOfLinks != 1 || (info.Attributes & (0x10u | 0x400u | 0x800u | 0x4000u | 0x200u)) != 0) {
            throw new IOException("Use an ordinary, uncompressed local file without links, encryption or sparse storage.");
        }
        FileIdentity = $"{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
        if (Length < SnapshotSize) { throw new InvalidDataException("The image is too short to contain a VHDX header section."); }
        var signature = Read(0, 8);
        IsLocked = signature.AsSpan().SequenceEqual(Masked);
        if (!allowDamagedSignature && !IsLocked && !signature.AsSpan().SequenceEqual(Normal)) {
            throw new InvalidDataException("Unknown image signature. Use standalone recovery for a damaged VSUX signature.");
        }
        ValidateStructure(allowPendingLog);
    }

    public static SignatureFile Open(string path, bool allowDamagedSignature = false, bool allowPendingLog = false, bool exclusive = true) {
        var fullPath = ValidatePath(path);
        var stream = new FileStream(fullPath, FileMode.Open, exclusive ? FileAccess.ReadWrite : FileAccess.Read,
            exclusive ? FileShare.None : FileShare.ReadWrite, 4096, exclusive ? FileOptions.WriteThrough : FileOptions.None);
        try {
            var finalPath = new StringBuilder(32768);
            var length = Native.GetFinalPathNameByHandle(stream.SafeFileHandle, finalPath, finalPath.Capacity, 0);
            if (length == 0 || length >= finalPath.Capacity ||
                !string.Equals(finalPath.ToString(), @"\\?\" + fullPath, StringComparison.OrdinalIgnoreCase)) {
                throw new IOException("The opened file resolves to a different path; refusing the operation.");
            }
            return new SignatureFile(stream, allowDamagedSignature, allowPendingLog, exclusive);
        } catch { stream.Dispose(); throw; }
    }

    public static string ValidatePath(string path) {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.AsSpan(2).Contains(':')) {
            throw new NotSupportedException("Use a local file, not a network path, device path or alternate data stream.");
        }
        var current = Path.GetPathRoot(full)!;
        foreach (var component in full[current.Length..].Split(Path.DirectorySeparatorChar)) {
            if (component.EndsWith(' ') || component.EndsWith('.')) { throw new IOException("Trailing spaces or dots in a path are not supported."); }
            current = Path.Combine(current, component);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) {
                throw new IOException("Links, junctions and mounted-folder paths are not supported for VSUX.");
            }
        }
        return full;
    }

    public byte[] Snapshot() => Read(0, SnapshotSize);

    public void SetLocked(bool locked) {
        if (!exclusive) { throw new InvalidOperationException("An exclusive file handle is required to write the signature."); }
        var expected = locked ? Masked : Normal;
        if (Read(0, 8).AsSpan().SequenceEqual(expected)) { return; }
        stream.Position = 0;
        stream.Write(expected, 0, 8); // Never expand this write to include a header, sector or snapshot.
        stream.Flush(flushToDisk: true);
        if (stream.Length != Length || !Read(0, 8).AsSpan().SequenceEqual(expected)) {
            throw new IOException("Signature verification failed. Do not mount the image; use standalone recovery.");
        }
        IsLocked = locked;
    }

    public static void Restore(string path) {
        using var file = Open(path, allowDamagedSignature: true);
        file.SetLocked(false); // No password, sidecar, stale header or saved metadata is needed.
    }

    private byte[] Read(long offset, int count) {
        if (offset < 0 || count < 0 || offset > Length - count) { throw new InvalidDataException("A VHDX structure extends beyond the file."); }
        var buffer = new byte[count];
        stream.Position = offset;
        stream.ReadExactly(buffer);
        return buffer;
    }

    private void ValidateStructure(bool allowPendingLog) {
        byte[]? current = null;
        foreach (var offset in new[] { 64 * 1024, 128 * 1024 }) {
            var header = Read(offset, 4096);
            if (!header.AsSpan(0, 4).SequenceEqual("head"u8) || !ChecksumMatches(header) || U16(header, 66) != 1) { continue; }
            if (current == null || U64(header, 8) > U64(current, 8)) { current = header; }
            else if (U64(header, 8) == U64(current, 8) && !header.AsSpan().SequenceEqual(current)) {
                throw new InvalidDataException("The VHDX headers have ambiguous sequence numbers.");
            }
        }
        if (current == null) { throw new InvalidDataException("Neither VHDX header has a valid checksum and version."); }
        if (!allowPendingLog && new Guid(current.AsSpan(48, 16)) != Guid.Empty) {
            throw new InvalidDataException("The VHDX has a pending log. VSUX support does not replay logs or change disk metadata.");
        }
        byte[]? regions = null;
        foreach (var offset in new[] { 192 * 1024, 256 * 1024 }) {
            var table = Read(offset, 64 * 1024);
            if (!table.AsSpan(0, 4).SequenceEqual("regi"u8) || !ChecksumMatches(table) || U32(table, 8) > 2047) { continue; }
            if (regions != null && !regions.AsSpan().SequenceEqual(table)) { throw new InvalidDataException("The valid region tables disagree."); }
            regions = table;
        }
        if (regions == null) { throw new InvalidDataException("Neither VHDX region table is valid."); }
        var metadataGuid = new Guid("8B7CA206-4790-4B9A-B8FE-575F050F886E");
        long metadataOffset = 0;
        uint metadataLength = 0;
        for (var i = 0; i < U32(regions, 8); i++) {
            var entry = 16 + i * 32;
            var offset = U64(regions, entry + 16);
            var length = U32(regions, entry + 24);
            if (offset < SnapshotSize || offset % SnapshotSize != 0 || length < 65536 || offset > (ulong)Length || length > (ulong)Length - offset) {
                throw new InvalidDataException("An invalid VHDX region location was found.");
            }
            if (new Guid(regions.AsSpan(entry, 16)) == metadataGuid) {
                if (metadataOffset != 0) { throw new InvalidDataException("Duplicate VHDX metadata region."); }
                metadataOffset = checked((long)offset);
                metadataLength = length;
            }
        }
        if (metadataOffset == 0) { throw new InvalidDataException("No VHDX metadata region was found."); }
        var metadata = Read(metadataOffset, 65536);
        if (!metadata.AsSpan(0, 8).SequenceEqual("metadata"u8) || U16(metadata, 10) > 2047) { throw new InvalidDataException("Invalid VHDX metadata table."); }
        var foundParameters = false;
        var diskIdGuid = new Guid("BECA12AB-B2E6-4523-93EF-C309E000C746");
        var parametersGuid = new Guid("CAA16737-FA36-4D43-B3B6-33F0AA44E76B");
        for (var i = 0; i < U16(metadata, 10); i++) {
            var entry = 32 + i * 32;
            var offset = U32(metadata, entry + 16);
            var length = U32(metadata, entry + 20);
            if (offset < 65536 || offset > metadataLength || length > metadataLength - offset) { throw new InvalidDataException("Invalid VHDX metadata item bounds."); }
            var id = new Guid(metadata.AsSpan(entry, 16));
            if (id == diskIdGuid) {
                if (length != 16 || DiskId != Guid.Empty) { throw new InvalidDataException("Invalid or duplicate VHDX disk identifier."); }
                DiskId = new Guid(Read(metadataOffset + offset, 16));
            } else if (id == parametersGuid) {
                if (length != 8 || foundParameters) { throw new InvalidDataException("Invalid or duplicate VHDX file parameters."); }
                var parameters = Read(metadataOffset + offset, 8);
                if ((U32(parameters, 4) & 2) != 0) { throw new NotSupportedException("Differencing disks are not supported for VSUX."); }
                foundParameters = true;
            }
        }
        if (DiskId == Guid.Empty || !foundParameters) { throw new InvalidDataException("Required VHDX metadata is missing."); }
    }

    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static ulong U64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
    private static bool ChecksumMatches(byte[] bytes) {
        var expected = U32(bytes, 4);
        uint crc = uint.MaxValue;
        for (var i = 0; i < bytes.Length; i++) {
            crc ^= i is >= 4 and < 8 ? 0u : bytes[i];
            for (var bit = 0; bit < 8; bit++) { crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0x82F63B78u : 0u); }
        }
        return ~crc == expected;
    }

    public void Dispose() => stream.Dispose();

    private static class Native {
        [StructLayout(LayoutKind.Sequential)]
        internal struct FileInfo {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, AccessTime, WriteTime;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInfo info);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, int capacity, uint flags);
    }
}
