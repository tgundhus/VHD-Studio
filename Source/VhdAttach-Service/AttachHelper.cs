using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using VhdAttachCommon;

namespace VhdAttachService {

    /// <summary>
    /// Attach/detach logic shared by interactive requests (pipe) and auto-attach at boot.
    /// </summary>
    internal static class AttachHelper {

        /// <param name="caller">User on whose behalf the service acts; PipeCaller.ForService() for boot-time auto-attach.</param>
        /// <param name="strictPaths">Also refuse hard-linked files (used for auto-attach entries, which non-admins may have added).</param>
        /// <param name="avoidLetters">Drive letters the user uses for network or subst drives (see DriveLetterMemory).</param>
        /// <returns>Drive letter notices for the user (see DriveLetterMemory.Apply); empty if none.</returns>
        public static string Attach(FileWithOptions file, PipeCaller caller, bool initializeDisk = false, bool strictPaths = false, ISet<char> avoidLetters = null) {
            if (!File.Exists(file.FileName)) {
                var missing = file.FileName;
                if (missing.StartsWith(@"\\", StringComparison.Ordinal)) {
                    throw new FileNotFoundException(string.Format("Network file \"{0}\" is not reachable by the service. The service runs as Local System; give the computer account access to the share or change the service log-on account.", missing), missing);
                }
                throw new FileNotFoundException(string.Format("File \"{0}\" was not found.", missing), missing);
            }
            using (var fileGuard = caller.GuardFile(file.FileName, (file.ReadOnly && !initializeDisk) ? FileAccess.Read : FileAccess.ReadWrite, strictPaths))
            using (var folderGuard = string.IsNullOrEmpty(file.MountFolder) ? null : caller.GuardMountFolder(file.MountFolder)) {
                return AttachPinned(file, fileGuard?.FinalPath ?? file.FileName, folderGuard?.FinalPath, initializeDisk, caller, avoidLetters ?? new HashSet<char>());
            }
        }

        private static string AttachPinned(FileWithOptions file, string path, string mountFolder, bool initializeDisk, PipeCaller caller, ISet<char> avoidLetters) {
            if (!File.Exists(path)) {
                if (path.StartsWith(@"\\", StringComparison.Ordinal)) {
                    throw new FileNotFoundException(string.Format("Network file \"{0}\" is not reachable by the service. The service runs as Local System; give the computer account access to the share or change the service log-on account.", path), path);
                }
                throw new FileNotFoundException(string.Format("File \"{0}\" was not found.", path), path);
            }

            if (VhdxHeader.NeedsLogReplay(path)) { //unclean shutdown; Windows refuses to open the file until the log is applied
                if (file.ReadOnly) { //a read-only request must never write to the file
                    throw new InvalidOperationException(string.Format("\"{0}\" was not closed cleanly and its log must be replayed before it can be attached. Read-only attach does not change the file; use Maintenance → Repair → Replay log (takes a backup first) or attach it read/write.", Path.GetFileName(path)));
                }
                Trace.TraceInformation("Replaying VHDX log for \"" + path + "\".");
                AuditLog.Started("Replay log (attach)", path);
                try {
                    caller.RunAsCaller(() => VirtualDiskImage.ReplayLog(path)); //caller must be able to write the file
                    AuditLog.Succeeded("Replay log (attach)", path);
                } catch (Exception ex) {
                    AuditLog.Failed("Replay log (attach)", path, ex);
                    throw new InvalidOperationException(string.Format("\"{0}\" was not closed cleanly and its log could not be replayed: {1}", Path.GetFileName(path), ex.Message), ex);
                }
            }

            var wantsLetters = !initializeDisk && (mountFolder == null) && !file.NoDriveLetter;
            string diskPath = null;
            using (var disk = new Medo.IO.VirtualDisk(path)) {
                var access = Medo.IO.VirtualDiskAccessMask.All;
                var options = Medo.IO.VirtualDiskAttachOptions.PermanentLifetime;
                if (file.ReadOnly) {
                    if (!initializeDisk) { access = Medo.IO.VirtualDiskAccessMask.AttachReadOnly | Medo.IO.VirtualDiskAccessMask.GetInfo | Medo.IO.VirtualDiskAccessMask.Detach; }
                    options |= Medo.IO.VirtualDiskAttachOptions.ReadOnly;
                }
                if (file.NoDriveLetter || (mountFolder != null)) { options |= Medo.IO.VirtualDiskAttachOptions.NoDriveLetter; }

                try { //opened with the caller's token: Windows binds the handle to a file the caller may access (no path race)
                    caller.RunAsCaller(() => disk.Open(access));
                } catch (IOException ex) when (!file.ReadOnly && ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)) {
                    throw new IOException(string.Format("\"{0}\" has the read-only attribute (for example a protected differencing parent). Attach it read-only, or clear the attribute deliberately.", Path.GetFileName(path)), ex);
                }
                disk.Attach(options);
                if (initializeDisk || (mountFolder != null) || wantsLetters) { diskPath = disk.GetAttachedPath(); }
            }

            if (initializeDisk) { DiskIO.InitializeDisk(diskPath); } //refuses anything that is not blank

            if ((mountFolder != null) && (diskPath != null)) {
                MountFirstVolume(diskPath, mountFolder);
            }

            if (wantsLetters && (diskPath != null)) {
                try {
                    return DriveLetterMemory.Apply(path, diskPath, avoidLetters);
                } catch (Exception ex) { //the disk is attached; a drive letter problem must not turn that into a failure
                    Trace.TraceWarning("Drive letters of \"" + path + "\" were not checked: " + ex.Message);
                }
            }
            return "";
        }

        /// <summary>
        /// Detaches after flushing, locking and dismounting every volume of the disk, so no buffered write is lost.
        /// </summary>
        /// <param name="force">Detach even if files are open (unsaved data in open files is lost).</param>
        /// <exception cref="VolumeInUseException">Files are open and force is false.</exception>
        /// <remarks>
        /// A disk attached read/write can only be detached by users with write access to its file;
        /// detaching while files are open (force) is reserved for administrators.
        /// </remarks>
        public static void Detach(string path, bool force, PipeCaller caller) {
            if (force) { caller.DemandAdministrator("Detaching a disk with open files"); }
            var normalized = path.StartsWith(@"\\", StringComparison.Ordinal) ? path : PathGuard.GetLongPath(Path.GetFullPath(path));
            var attached = GetAttachedImages().FirstOrDefault(i => string.Equals(i.Location, normalized, StringComparison.OrdinalIgnoreCase) || string.Equals(i.Location, path, StringComparison.OrdinalIgnoreCase));
            var access = ((attached != null) && !attached.IsReadOnly) ? FileAccess.ReadWrite : FileAccess.Read;
            using (var guard = caller.GuardFile(path, access)) {
                DetachPinned(guard?.FinalPath ?? path, force, caller, access);
            }
        }

        private static void DetachPinned(string path, bool force, PipeCaller caller, FileAccess access) {
            using (var disk = new Medo.IO.VirtualDisk(path)) {
                caller.RunAsCaller(() => { //bind to a file the caller can access before Local System acts on it
                    if (access == FileAccess.ReadWrite) {
                        try {
                            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete)) { }
                        } catch (IOException ex) when ((ex.HResult == unchecked((int)0x80070020)) || (ex.HResult == unchecked((int)0x80070021))) {
                            //sharing violation: access was granted, the attached disk holds the file
                        }
                    }
                    disk.Open(Medo.IO.VirtualDiskAccessMask.Detach | Medo.IO.VirtualDiskAccessMask.GetInfo);
                });
                string physicalPath = null;
                try { physicalPath = disk.GetAttachedPath(); } catch (IOException) { } catch (System.ComponentModel.Win32Exception) { }

                VolumeLock locks = null;
                if (physicalPath != null) {
                    var volumes = Volume.GetVolumesOnPhysicalDrive(physicalPath);
                    var roots = volumes.SelectMany(v => GetAccessPaths(v.VolumeName)).ToList();
                    var nested = GetAttachedImages().FirstOrDefault(i => roots.Any(r => i.Location.StartsWith(r, StringComparison.OrdinalIgnoreCase)));
                    if (nested != null) { //removing this disk would pull the storage out from under another attached disk
                        throw new InvalidOperationException(string.Format("Virtual disk {0} (\"{1}\") is stored on this disk. Detach it first.", nested.Number, nested.Location));
                    }
                    locks = VolumeLock.Acquire(
                        volumes.Select(v => v.VolumeName).ToList(),
                        volumes.Select(v => v.DriveLetter2 ?? v.VolumeName).ToList(),
                        force);
                }
                using (locks) {
                    disk.Detach();
                }
            }
        }

        /// <summary>
        /// The mount folder must already exist, be empty, and must not be a junction or symbolic link.
        /// The service never creates folders on behalf of a client (it runs as Local System).
        /// </summary>
        public static void ValidateMountFolder(string folder) {
            if (!Path.IsPathFullyQualified(folder) || folder.StartsWith(@"\\", StringComparison.Ordinal)) {
                throw new ArgumentException(string.Format("Mount folder \"{0}\" must be a full path on a local drive.", folder));
            }
            var directory = new DirectoryInfo(folder);
            if (!directory.Exists) { throw new DirectoryNotFoundException(string.Format("Mount folder \"{0}\" does not exist. Create an empty folder first.", folder)); }
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) { throw new InvalidOperationException(string.Format("Mount folder \"{0}\" is a junction, link or existing mount point.", folder)); }
            if (directory.EnumerateFileSystemInfos().Any()) { throw new InvalidOperationException(string.Format("Mount folder \"{0}\" is not empty.", folder)); }
        }

        internal sealed class AttachedImage {
            public int Number;
            public string Location;
            public bool IsReadOnly;
        }

        /// <summary>
        /// Currently attached file-backed virtual disks.
        /// </summary>
        public static System.Collections.Generic.IList<AttachedImage> GetAttachedImages() {
            var list = new System.Collections.Generic.List<AttachedImage>();
            using (var searcher = new System.Management.ManagementObjectSearcher(@"\\.\root\Microsoft\Windows\Storage", "SELECT Number, Location, IsReadOnly FROM MSFT_Disk WHERE BusType = 15")) {
                foreach (System.Management.ManagementObject mo in searcher.Get()) {
                    using (mo) {
                        if (mo["Location"] is string location) {
                            list.Add(new AttachedImage { Number = Convert.ToInt32(mo["Number"], System.Globalization.CultureInfo.InvariantCulture), Location = location, IsReadOnly = (bool)(mo["IsReadOnly"] ?? false) });
                        }
                    }
                }
            }
            return list;
        }

        private static System.Collections.Generic.IList<string> GetAccessPaths(string volumeName) {
            var paths = new System.Collections.Generic.List<string> { volumeName };
            var buffer = new char[4096];
            if (NativeMethods.GetVolumePathNamesForVolumeName(volumeName, buffer, buffer.Length, out var length)) {
                paths.AddRange(new string(buffer, 0, Math.Max(0, length)).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries));
            }
            return paths;
        }

        private static class NativeMethods {
            [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
            [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
            public static extern bool GetVolumePathNamesForVolumeName(string lpszVolumeName, char[] lpszVolumePathNames, int cchBufferLength, out int lpcchReturnLength);
        }

        private static void MountFirstVolume(string diskPath, string folder) {
            for (int i = 0; i < 20; i++) { //volumes show up shortly after the disk arrives
                var volumes = Volume.GetVolumesOnPhysicalDrive(diskPath);
                if (volumes.Count > 0) {
                    volumes[0].AddMountFolder(folder);
                    return;
                }
                Thread.Sleep(250);
            }
            throw new InvalidOperationException(string.Format("Disk was attached but no volume appeared to mount at \"{0}\".", folder));
        }

    }

}
