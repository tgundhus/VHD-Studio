using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using VhdAttachCommon;

namespace VhdAttachService {

    /// <summary>
    /// Attach logic shared by interactive attach (pipe) and auto-attach at boot.
    /// </summary>
    internal static class AttachHelper {

        public static void Attach(FileWithOptions file, bool initializeDisk = false) {
            var path = file.FileName;
            if (!File.Exists(path)) {
                if (path.StartsWith(@"\\", StringComparison.Ordinal)) {
                    throw new FileNotFoundException(string.Format("Network file \"{0}\" is not reachable by the service. The service runs as Local System; give the computer account access to the share or change the service log-on account.", path), path);
                }
                throw new FileNotFoundException(string.Format("File \"{0}\" was not found.", path), path);
            }

            if (VhdxHeader.NeedsLogReplay(path)) { //unclean shutdown; a read-only open would fail with "Access denied"
                Trace.TraceInformation("Replaying VHDX log for \"" + path + "\".");
                try {
                    VirtualDiskImage.ReplayLog(path);
                } catch (Exception ex) {
                    throw new InvalidOperationException(string.Format("\"{0}\" was not closed cleanly and its log could not be replayed: {1}", Path.GetFileName(path), ex.Message), ex);
                }
            }

            string diskPath = null;
            using (var disk = new Medo.IO.VirtualDisk(path)) {
                var access = Medo.IO.VirtualDiskAccessMask.All;
                var options = Medo.IO.VirtualDiskAttachOptions.PermanentLifetime;
                if (file.ReadOnly) {
                    if (!initializeDisk) { access = Medo.IO.VirtualDiskAccessMask.AttachReadOnly | Medo.IO.VirtualDiskAccessMask.GetInfo | Medo.IO.VirtualDiskAccessMask.Detach; }
                    options |= Medo.IO.VirtualDiskAttachOptions.ReadOnly;
                }
                if (file.NoDriveLetter || !string.IsNullOrEmpty(file.MountFolder)) { options |= Medo.IO.VirtualDiskAttachOptions.NoDriveLetter; }

                try {
                    disk.Open(access);
                } catch (IOException ex) when (!file.ReadOnly && ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)) {
                    throw new IOException(string.Format("\"{0}\" has the read-only attribute. Attach it read-only or clear the attribute.", Path.GetFileName(path)), ex);
                }
                disk.Attach(options);
                if (initializeDisk || !string.IsNullOrEmpty(file.MountFolder)) { diskPath = disk.GetAttachedPath(); }
            }

            if (initializeDisk) { DiskIO.InitializeDisk(diskPath); }

            if (!string.IsNullOrEmpty(file.MountFolder) && (diskPath != null)) {
                MountFirstVolume(diskPath, file.MountFolder);
            }
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
