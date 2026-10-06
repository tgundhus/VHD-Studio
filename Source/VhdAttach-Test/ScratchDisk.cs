using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VhdAttach.Storage;
using VhdAttachCommon;

namespace VhdAttachTest {

    /// <summary>
    /// Temporary virtual disk with an NTFS volume and known content for data-safety tests.
    /// Every storage operation is guarded: it only proceeds when the target disk is virtual and backed by this file.
    /// </summary>
    internal sealed class ScratchDisk : IDisposable {

        public static readonly string Root = Path.Combine(Path.GetTempPath(), "VhdStudioSafetyTests");

        public string FileName { get; }
        private Medo.IO.VirtualDisk Attached;

        private ScratchDisk(string fileName) {
            this.FileName = fileName;
        }

        public static void RequireElevation() {
            using (var identity = WindowsIdentity.GetCurrent()) {
                if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) {
                    Assert.Inconclusive("Requires an elevated test run (dotnet test --filter TestCategory=Elevated from an administrator prompt).");
                }
            }
        }

        public static string NewPath(string extension = ".vhdx") {
            Directory.CreateDirectory(Root);
            return Path.Combine(Root, "disk-" + Guid.NewGuid().ToString("N").Substring(0, 8) + extension);
        }

        /// <summary>
        /// Creates a dynamic (or fixed) disk, partitions and formats it with NTFS, detached afterwards.
        /// </summary>
        public static ScratchDisk Create(long size = 1L << 30, bool fixedSize = false, string extension = ".vhdx") {
            var disk = new ScratchDisk(NewPath(extension));
            using (var vd = new Medo.IO.VirtualDisk(disk.FileName)) {
                vd.Create(size, fixedSize ? Medo.IO.VirtualDiskCreateOptions.FullPhysicalAllocation : Medo.IO.VirtualDiskCreateOptions.None, 0, 0, extension.EndsWith("x", StringComparison.OrdinalIgnoreCase) ? Medo.IO.VirtualDiskType.Vhdx : Medo.IO.VirtualDiskType.Vhd);
            }
            disk.Attach(readOnly: false);
            var info = disk.StorageDisk();
            StorageManager.InitializeDisk(info, PartitionStyle.Gpt);
            info = disk.StorageDisk();
            StorageManager.CreatePartition(info, 0, false, "NTFS", "Safety");
            disk.WaitForVolume();
            disk.Detach();
            return disk;
        }

        public static ScratchDisk Open(string fileName) => new ScratchDisk(fileName);


        #region Attach

        public void Attach(bool readOnly) {
            if (this.Attached != null) { throw new InvalidOperationException("Already attached."); }
            var vd = new Medo.IO.VirtualDisk(this.FileName);
            vd.Open(readOnly ? (Medo.IO.VirtualDiskAccessMask.AttachReadOnly | Medo.IO.VirtualDiskAccessMask.GetInfo | Medo.IO.VirtualDiskAccessMask.Detach) : Medo.IO.VirtualDiskAccessMask.All);
            vd.Attach(Medo.IO.VirtualDiskAttachOptions.NoDriveLetter | (readOnly ? Medo.IO.VirtualDiskAttachOptions.ReadOnly : Medo.IO.VirtualDiskAttachOptions.None));
            this.Attached = vd;
            var disk = this.StorageDisk();
            if (disk.IsOffline) { StorageManager.SetDiskOnline(disk, true); }
        }

        public void Detach() {
            if (this.Attached == null) { return; }
            VhdAttachService.AttachHelper.Detach(this.FileName, false, VhdAttachService.PipeCaller.ForService());
            this.Attached.Dispose();
            this.Attached = null;
        }

        public string PhysicalPath => this.Attached?.GetAttachedPath();

        /// <summary>
        /// The storage view of this disk, verified to be virtual and backed by our file.
        /// </summary>
        public DiskInfo StorageDisk() {
            var number = int.Parse(this.PhysicalPath.Substring(this.PhysicalPath.IndexOf("PhysicalDrive", StringComparison.OrdinalIgnoreCase) + 13));
            for (int i = 0; i < 40; i++) {
                var disk = StorageManager.GetDisks().FirstOrDefault(d => d.Number == number);
                if ((disk != null) && disk.IsVirtual && string.Equals(Path.GetFullPath(disk.Location ?? ""), Path.GetFullPath(this.FileName), StringComparison.OrdinalIgnoreCase)) { return disk; }
                Thread.Sleep(250);
            }
            throw new InvalidOperationException("SAFETY STOP: disk " + number + " is not the scratch disk " + this.FileName);
        }

        public PartitionInfo DataPartition() => StorageManager.GetPartitions(this.StorageDisk().Number).Where(p => p.FileSystem == "NTFS").OrderByDescending(p => p.Size).First();

        public string WaitForVolume() {
            for (int i = 0; i < 60; i++) {
                var volume = Volume.GetVolumesOnPhysicalDrive(this.PhysicalPath).Select(v => v.VolumeName).FirstOrDefault(v => Directory.Exists(v));
                if (volume != null) { return volume; }
                Thread.Sleep(250);
            }
            throw new InvalidOperationException("Volume did not appear.");
        }

        #endregion


        #region Content

        /// <summary>
        /// Writes random files and returns name → SHA-256.
        /// </summary>
        public Dictionary<string, string> WriteFiles(int count, int sizeEach, string prefix = "file") {
            var volume = this.WaitForVolume();
            var hashes = new Dictionary<string, string>();
            var buffer = new byte[sizeEach];
            for (int i = 0; i < count; i++) {
                RandomNumberGenerator.Fill(buffer);
                var name = prefix + i.ToString("00") + ".bin";
                using (var stream = new FileStream(Path.Combine(volume, name), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.WriteThrough)) {
                    stream.Write(buffer, 0, buffer.Length);
                }
                hashes[name] = Convert.ToHexString(SHA256.HashData(buffer));
            }
            return hashes;
        }

        public void DeleteFiles(IEnumerable<string> names) {
            var volume = this.WaitForVolume();
            foreach (var name in names) { File.Delete(Path.Combine(volume, name)); }
        }

        /// <summary>
        /// Attaches read-only, verifies every file byte-for-byte via SHA-256, detaches.
        /// </summary>
        public void Verify(IDictionary<string, string> expected) {
            this.Attach(readOnly: true);
            try {
                var volume = this.WaitForVolume();
                foreach (var pair in expected) {
                    var path = Path.Combine(volume, pair.Key);
                    Assert.IsTrue(File.Exists(path), "Missing file " + pair.Key + " in " + Path.GetFileName(this.FileName));
                    using (var stream = File.OpenRead(path)) {
                        Assert.AreEqual(pair.Value, Convert.ToHexString(SHA256.HashData(stream)), "Content changed: " + pair.Key + " in " + Path.GetFileName(this.FileName));
                    }
                }
            } finally {
                this.Detach();
            }
        }

        public static string HashFile(string fileName) {
            using (var stream = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                return Convert.ToHexString(SHA256.HashData(stream));
            }
        }

        #endregion


        public void Dispose() {
            try { this.Detach(); } catch (Exception) { try { this.Attached?.Detach(); } catch (Exception) { } this.Attached?.Dispose(); this.Attached = null; }
            try {
                if (File.Exists(this.FileName)) {
                    File.SetAttributes(this.FileName, FileAttributes.Normal);
                    File.Delete(this.FileName);
                }
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

    }
}
