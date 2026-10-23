using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VhdStudio.Vsux;

/// <summary>Windows provider operations. Never edits existing image bytes directly.</summary>
internal static class NativeDisk {
    private const uint GetInfo = 0x00080000;
    private const uint DetachAccess = 0x00040000;
    private const uint Read = 0x80000000;
    private const uint Write = 0x40000000;
    private const uint ShareReadWrite = 3;
    private const uint VolumeExtents = 0x00560000;
    private const uint DiskIsWritable = 0x00070024;
    private const uint LockVolume = 0x00090018;
    private const uint DismountVolume = 0x00090020;
    private const int DeviceDoesNotExist = 55;

    /// <summary>Creates a fresh 64 MiB dynamic VHDX for disposable tests; never replaces a file.</summary>
    public static void CreateDisposable(string path) {
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(fullPath), ".vhdx", StringComparison.OrdinalIgnoreCase)) {
            throw new ArgumentException("Disposable native test images must use the .vhdx extension.", nameof(path));
        }
        if (File.Exists(fullPath) || Directory.Exists(fullPath)) { throw new IOException("The disposable image path already exists."); }
        var storage = VhdxStorage();
        var parameters = new CreateParameters {
            Version = 2,
            Version2 = new CreateParametersData {
                UniqueId = Guid.NewGuid(),
                MaximumSize = 64UL * 1024 * 1024,
                BlockSizeInBytes = 2 * 1024 * 1024,
                SectorSizeInBytes = 512,
                PhysicalSectorSizeInBytes = 4096,
            },
        };
        // Windows creates a new image atomically and refuses an existing name.
        var status = Native.CreateVirtualDisk(ref storage, fullPath, 0, IntPtr.Zero, 0, 0,
            ref parameters, IntPtr.Zero, out var handle);
        using (handle) { CheckCode(status, "Create the disposable VHDX"); }
    }

    // The normal VHDX identifier must be present. A masked image must instead be
    // opened with FileShare.None by the signature writer and kept exclusive.
    public static string? GetAttachedPath(string path) {
        using var disk = OpenVirtual(path, GetInfo | DetachAccess, writable: false);
        return GetAttachedPath(disk);
    }

    public static bool IsAttached(string path) => GetAttachedPath(path) != null;

    // Only the Windows VHDX provider writes user metadata. The caller verifies
    // the password and holds a file-identity guard before opening this handle.
    public static void SetPasswordMetadata(string path, byte[] record) {
        using var disk = OpenMetadata(path);
        if (GetAttachedPath(disk) != null) { throw new IOException("Detach the disk before changing VSUX metadata."); }
        var id = SignatureFile.PasswordMetadataId;
        CheckCode(Native.SetVirtualDiskMetadata(disk, ref id, (uint)record.Length, record), "Store the VSUX password record");
        uint size = 4096;
        var readBack = new byte[size];
        CheckCode(Native.GetVirtualDiskMetadata(disk, ref id, ref size, readBack), "Read back the VSUX password record");
        if (size != record.Length || !record.AsSpan().SequenceEqual(readBack.AsSpan(0, (int)size))) { throw new IOException("The embedded VSUX password record failed read-back verification."); }
        if (GetAttachedPath(disk) != null) { throw new IOException("The disk became attached during the operation. Its signature was not masked."); }
    }

    public static void DeletePasswordMetadata(string path) {
        using var disk = OpenMetadata(path);
        if (GetAttachedPath(disk) != null) { throw new IOException("Detach the disk before removing VSUX metadata."); }
        var id = SignatureFile.PasswordMetadataId;
        CheckCode(Native.DeleteVirtualDiskMetadata(disk, ref id), "Remove the VSUX password record");
        if (GetAttachedPath(disk) != null) { throw new IOException("The disk became attached during conversion."); }
    }

    public static void EnsureDetached(string path) {
        if (IsAttached(path)) {
            throw new IOException("The disk is attached. Safely detach it before changing its signature.");
        }
    }

    /// <summary>Reject remote storage and images hosted inside another mounted VHD.</summary>
    public static void EnsurePhysicalBackingVolume(string path) {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal)) {
            throw new NotSupportedException("VSUX requires a local physical backing volume.");
        }
        var mountPoint = new StringBuilder(32768);
        Check(Native.GetVolumePathName(fullPath, mountPoint, mountPoint.Capacity), "Locate the backing volume");
        var volumeName = new StringBuilder(1024);
        Check(Native.GetVolumeNameForVolumeMountPoint(mountPoint.ToString(), volumeName, volumeName.Capacity), "Identify the backing volume");
        using var volume = OpenDevice(volumeName.ToString().TrimEnd('\\'), 0);
        if (DependencyCount(volume, 1) != 0) { // HOST_VOLUMES: parents of this volume
            throw new NotSupportedException("Images stored inside another mounted virtual disk are outside the supported VSUX safety limits.");
        }
    }

    /// <summary>
    /// Holds every filesystem volume exclusively, flushes writable volumes, then
    /// dismounts and detaches. Any uncertain or failed operation aborts; no force.
    /// </summary>
    public static void Detach(string path) {
        RequireElevation();
        EnsurePhysicalBackingVolume(path);
        using var disk = OpenVirtual(path, GetInfo | DetachAccess, writable: false);
        var physicalPath = GetAttachedPath(disk);
        if (physicalPath == null) { return; }
        var diskNumber = ParsePhysicalDiskNumber(physicalPath);
        using var physical = OpenDevice(physicalPath, 0);
        var writable = IsWritable(physical);
        var volumeNames = GetVolumes(diskNumber);
        var locked = new List<SafeFileHandle>();
        try {
            foreach (var volumeName in volumeNames) {
                var volume = OpenDevice(volumeName.TrimEnd('\\'), Read | (writable ? Write : 0));
                locked.Add(volume);
                // FSCTL_LOCK_VOLUME success proves that no files remain open,
                // including backing files of nested mounted virtual disks.
                // Keep every lock until DetachVirtualDisk finishes. Windows
                // rejects descendant dependency queries on physical handles.
                LockForDetach(volume, volumeName);
                if (writable) {
                    Check(Native.FlushFileBuffers(volume), $"Flush {volumeName}");
                }
            }
            foreach (var volume in locked) {
                Check(Native.DeviceIoControl(volume, DismountVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero), "Dismount the locked volume");
            }
            CheckCode(Native.DetachVirtualDisk(disk, 0, 0), "Detach the virtual disk");
        } finally {
            foreach (var volume in locked) { volume.Dispose(); } // closes and releases locks even on failure
        }
        if (GetAttachedPath(disk) != null) {
            throw new IOException("Windows still reports the disk attached; its signature must not be changed.");
        }
    }

    /// <summary>Call only after the caller validates a clean, standalone VHDX.</summary>
    public static string Mount(string path, bool readOnly = false, bool noDriveLetter = false) {
        RequireElevation();
        EnsurePhysicalBackingVolume(path);
        using var disk = OpenVirtual(path, (readOnly ? 0x00010000u : 0x00020000u) | GetInfo | DetachAccess, writable: !readOnly);
        if (GetAttachedPath(disk) != null) { throw new IOException("The disk is already attached."); }
        var parameters = new AttachParameters { Version = 1 };
        var flags = 4u | (readOnly ? 1u : 0u) | (noDriveLetter ? 2u : 0u); // permanent lifetime
        CheckCode(Native.AttachVirtualDisk(disk, IntPtr.Zero, flags, 0, ref parameters, IntPtr.Zero), "Attach the virtual disk");
        return GetAttachedPath(disk) ?? throw new IOException("Windows attached the disk but did not return its physical path.");
    }

    public static string MountReadOnly(string path) => Mount(path, readOnly: true);

    public static void RequireElevation() {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) {
            throw new UnauthorizedAccessException("Administrator rights are required to attach or safely detach a VSUX disk.");
        }
    }

    private static SafeFileHandle OpenVirtual(string path, uint access, bool writable) {
        var storage = VhdxStorage();
        var parameters = new OpenParameters { Version = 1, RWDepth = writable ? 1u : 0u };
        // Explicit VHDX provider is required when the filename has the .vsux suffix.
        var status = Native.OpenVirtualDisk(ref storage, Path.GetFullPath(path), access, 0, ref parameters, out var handle);
        if (status != 0) { handle?.Dispose(); CheckCode(status, "Open the VHDX with Windows"); }
        return handle!;
    }

    private static SafeFileHandle OpenMetadata(string path) {
        var storage = VhdxStorage();
        var parameters = new OpenMetadataParameters { Version = 2 };
        // Version 2 takes access NONE; ReadOnly=false opens the backing store for
        // writing without attaching or initializing the virtual disk.
        var status = Native.OpenVirtualDiskMetadata(ref storage, Path.GetFullPath(path), 0, 0, ref parameters, out var handle);
        if (status != 0) { handle?.Dispose(); CheckCode(status, "Open the VHDX metadata with Windows"); }
        return handle!;
    }

    private static VirtualStorageType VhdxStorage() => new() {
        DeviceId = 3, VendorId = new Guid("EC984AEC-A0F9-47E9-901F-71415A66345B"),
    };

    private static string? GetAttachedPath(SafeFileHandle disk) {
        var buffer = new StringBuilder(512);
        uint bytes = (uint)buffer.Capacity * 2;
        var status = Native.GetVirtualDiskPhysicalPath(disk, ref bytes, buffer);
        if (status == 122 && bytes <= 65536) {
            buffer.Capacity = checked((int)(bytes / 2 + 1));
            bytes = (uint)buffer.Capacity * 2;
            status = Native.GetVirtualDiskPhysicalPath(disk, ref bytes, buffer);
        }
        if (status == DeviceDoesNotExist) { return null; }
        CheckCode(status, "Determine whether the VHDX is attached");
        if (buffer.Length == 0) { throw new IOException("Windows returned an empty attached disk path."); }
        return buffer.ToString();
    }

    private static uint ParsePhysicalDiskNumber(string path) {
        const string prefix = @"\\.\PhysicalDrive";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !uint.TryParse(path.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number)) {
            throw new IOException("Windows returned an unexpected physical disk path.");
        }
        return number;
    }

    private static bool IsWritable(SafeFileHandle disk) {
        if (Native.DeviceIoControl(disk, DiskIsWritable, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero)) { return true; }
        var error = Marshal.GetLastWin32Error();
        if (error == 19) { return false; } // ERROR_WRITE_PROTECT, checked rather than inferred from a failed flush
        throw new Win32Exception(error, "Determine disk write protection");
    }

    private static void LockForDetach(SafeFileHandle volume, string volumeName) {
        var lastError = 0;
        for (var attempt = 0; attempt < 10; attempt++) {
            if (Native.DeviceIoControl(volume, LockVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero)) { return; }
            lastError = Marshal.GetLastWin32Error();
            // File-system filters and Explorer can briefly retain a closed file.
            // Retry the same exclusive lock; never dismount an unlocked volume.
            if (lastError is not (5 or 32 or 33 or 170) || attempt == 9) { break; }
            Thread.Sleep(200);
        }
        throw new Win32Exception(lastError,
            $"Lock {volumeName} (Windows error {lastError}: {new Win32Exception(lastError).Message}). Close files and Explorer windows using this disk, then retry.");
    }

    private static uint DependencyCount(SafeFileHandle handle, uint flags) {
        var size = 4096;
        while (true) {
            var buffer = Marshal.AllocHGlobal(size);
            try {
                Marshal.WriteInt32(buffer, 1); // STORAGE_DEPENDENCY_INFO_VERSION_1
                Marshal.WriteInt32(buffer, 4, 0);
                var status = Native.GetStorageDependencyInformation(handle, flags, (uint)size, buffer, out var used);
                if (status == 122 && used > size && used <= 1048576) { size = (int)used; continue; }
                // Physical volumes have no virtual parents. Windows reports this
                // explicit status instead of an empty list for HOST_VOLUMES.
                if (status == unchecked((int)0xC03A0015) && flags == 1) { return 0; }
                CheckCode(status, "Check virtual disk dependencies");
                return unchecked((uint)Marshal.ReadInt32(buffer, 4));
            } finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    private static List<string> GetVolumes(uint diskNumber) {
        var volumes = new List<string>();
        var name = new StringBuilder(1024);
        var search = Native.FindFirstVolume(name, name.Capacity);
        if (search == new IntPtr(-1)) { throw new Win32Exception(Marshal.GetLastWin32Error(), "Enumerate disk volumes"); }
        try {
            while (true) {
                using (var volume = OpenDevice(name.ToString().TrimEnd('\\'), 0)) {
                    var extentDisks = GetExtentDisks(volume);
                    if (extentDisks.Contains(diskNumber)) {
                        if (extentDisks.Count != 1) { throw new NotSupportedException("Spanned or multi-extent volumes are outside the supported VSUX safety limits."); }
                        volumes.Add(name.ToString());
                    }
                }
                if (!Native.FindNextVolume(search, name, name.Capacity)) {
                    var error = Marshal.GetLastWin32Error();
                    if (error != 18) { throw new Win32Exception(error, "Complete volume enumeration"); }
                    break;
                }
            }
        } finally { Native.FindVolumeClose(search); }
        return volumes;
    }

    private static List<uint> GetExtentDisks(SafeFileHandle volume) {
        var buffer = new byte[65536];
        if (!Native.DeviceIoControlBytes(volume, VolumeExtents, IntPtr.Zero, 0, buffer, buffer.Length, out var returned, IntPtr.Zero)) {
            var error = Marshal.GetLastWin32Error();
            // Optical and empty removable-media volumes have no disk extents.
            if (error is 1 or 21 or 50) { return []; }
            throw new Win32Exception(error, "Read volume disk extents");
        }
        if (returned < 8) { throw new IOException("Windows returned incomplete volume extents."); }
        var count = BitConverter.ToUInt32(buffer, 0);
        if (count > (returned - 8) / 24) { throw new IOException("Windows returned truncated volume extents."); }
        var disks = new List<uint>();
        for (var i = 0; i < count; i++) { disks.Add(BitConverter.ToUInt32(buffer, 8 + i * 24)); }
        return disks;
    }

    private static SafeFileHandle OpenDevice(string path, uint access) {
        var handle = Native.CreateFile(path, access, ShareReadWrite, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"Open device {path}");
        }
        return handle;
    }

    private static void Check(bool success, string operation) {
        if (!success) { throw new Win32Exception(Marshal.GetLastWin32Error(), operation); }
    }

    private static void CheckCode(int error, string operation) {
        if (error != 0) { throw new Win32Exception(error, $"{operation} (0x{error:X8})"); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VirtualStorageType { public uint DeviceId; public Guid VendorId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct OpenParameters { public uint Version; public uint RWDepth; }
    [StructLayout(LayoutKind.Sequential)]
    private struct OpenMetadataParameters { public uint Version; public int GetInfoOnly; public int ReadOnly; public Guid ResiliencyGuid; }
    [StructLayout(LayoutKind.Sequential)]
    private struct AttachParameters { public uint Version; public ulong Reserved; }
    // The native union has 8-byte alignment. Nest it so UniqueId starts at offset 8,
    // rather than offset 4 (flattening the fields silently shifts every parameter).
    [StructLayout(LayoutKind.Sequential)]
    private struct CreateParameters { public uint Version; public CreateParametersData Version2; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CreateParametersData {
        public Guid UniqueId;
        public ulong MaximumSize;
        public uint BlockSizeInBytes;
        public uint SectorSizeInBytes;
        public uint PhysicalSectorSizeInBytes;
        public IntPtr ParentPath;
        public IntPtr SourcePath;
        public uint OpenFlags;
        public VirtualStorageType ParentVirtualStorageType;
        public VirtualStorageType SourceVirtualStorageType;
        public Guid ResiliencyGuid;
    }

    private static class Native {
        [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
        internal static extern int CreateVirtualDisk(ref VirtualStorageType type, string path, uint access, IntPtr security, uint flags, uint providerFlags, ref CreateParameters parameters, IntPtr overlapped, out SafeFileHandle handle);
        [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
        internal static extern int OpenVirtualDisk(ref VirtualStorageType type, string path, uint access, uint flags, ref OpenParameters parameters, out SafeFileHandle handle);
        [DllImport("virtdisk.dll", EntryPoint = "OpenVirtualDisk", CharSet = CharSet.Unicode)]
        internal static extern int OpenVirtualDiskMetadata(ref VirtualStorageType type, string path, uint access, uint flags, ref OpenMetadataParameters parameters, out SafeFileHandle handle);
        [DllImport("virtdisk.dll")]
        internal static extern int SetVirtualDiskMetadata(SafeFileHandle disk, ref Guid item, uint size, byte[] data);
        [DllImport("virtdisk.dll")]
        internal static extern int GetVirtualDiskMetadata(SafeFileHandle disk, ref Guid item, ref uint size, [Out] byte[] data);
        [DllImport("virtdisk.dll")]
        internal static extern int DeleteVirtualDiskMetadata(SafeFileHandle disk, ref Guid item);
        [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetVirtualDiskPhysicalPath(SafeFileHandle disk, ref uint size, StringBuilder path);
        [DllImport("virtdisk.dll")]
        internal static extern int DetachVirtualDisk(SafeFileHandle disk, uint flags, uint providerFlags);
        [DllImport("virtdisk.dll")]
        internal static extern int AttachVirtualDisk(SafeFileHandle disk, IntPtr securityDescriptor, uint flags, uint providerFlags, ref AttachParameters parameters, IntPtr overlapped);
        [DllImport("virtdisk.dll")]
        internal static extern int GetStorageDependencyInformation(SafeFileHandle handle, uint flags, uint size, IntPtr info, out uint used);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
        [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControlBytes(SafeFileHandle handle, uint code, IntPtr input, int inputSize, [Out] byte[] output, int outputSize, out int returned, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FlushFileBuffers(SafeFileHandle handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr FindFirstVolume(StringBuilder name, int length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FindNextVolume(IntPtr search, StringBuilder name, int length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FindVolumeClose(IntPtr search);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetVolumePathName(string fileName, StringBuilder volumePath, int length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, int length);
    }
}
