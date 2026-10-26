using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VhdAttachCommon;

namespace VhdAttachTest {

    [TestClass]
    [DoNotParallelize]
    public class VolumeHandleTest {
        [TestMethod]
        public void ExtentProbeFailure_ReleasesEveryNativeHandleBeforeReturning() {
            var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "VhdStudioVolumeHandles-" + Guid.NewGuid().ToString("N"))).FullName;
            var source = Path.Combine(folder, "extent-probe.bin");
            var target = Path.Combine(folder, "renamed.bin");
            File.WriteAllBytes(source, new byte[4096]);
            var noGc = false;
            try {
                // A regular fixture file can be opened, but has no disk extents.
                // Prevent finalization from disguising a missing Dispose after each probe.
                noGc = GC.TryStartNoGCRegion(16L * 1024 * 1024);
                Assert.IsTrue(noGc, "The handle lifetime regression needs a brief region without garbage collection.");
                var retained = new List<Volume>();
                for (var attempt = 0; attempt < 4; attempt++) { Assert.IsNull(new Volume(source).PhysicalDriveNumber); } // Warm native marshalling and JIT.
                var before = HandleCount();
                for (var attempt = 0; attempt < 64; attempt++) {
                    var volume = new Volume(source);
                    retained.Add(volume);
                    Assert.IsNull(volume.PhysicalDriveNumber);
                }
                var after = HandleCount();
                Assert.AreEqual(GCLatencyMode.NoGCRegion, GCSettings.LatencyMode);
                Assert.IsTrue(after <= before + 8, $"Extent probes retained native handles ({before} before, {after} after). Finalization must not be needed to close them.");
                File.Move(source, target);
                GC.KeepAlive(retained);
            } finally {
                if (noGc && GCSettings.LatencyMode == GCLatencyMode.NoGCRegion) { GC.EndNoGCRegion(); }
                // Also permits cleanup when running this regression against the unfixed implementation.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                foreach (var file in Directory.GetFiles(folder)) { File.Delete(file); }
                Directory.Delete(folder); // Only this test's flat, uniquely named directory.
            }
        }

        private static uint HandleCount() {
            if (!GetProcessHandleCount(new IntPtr(-1), out var count)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            return count;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessHandleCount(IntPtr process, out uint count);

        [TestMethod]
        [TestCategory("Elevated")]
        public void RepeatedVolumeEnumeration_DoesNotPreventImmediateSafeVolumeLock() {
            ScratchDisk.RequireElevation();
            using (var disk = ScratchDisk.Create(size: 64L * 1024 * 1024)) {
                disk.Attach(readOnly: false);
                var retained = new List<Volume>();
                var noGc = GC.TryStartNoGCRegion(16L * 1024 * 1024);
                Assert.IsTrue(noGc, "The safe-lock regression needs a brief region without garbage collection.");
                try {
                    for (var attempt = 0; attempt < 12; attempt++) {
                        retained.AddRange(Volume.GetVolumesOnPhysicalDrive(disk.PhysicalPath));
                    }
                    Assert.IsTrue(retained.Count >= 12, "The fixture must expose its NTFS volume on every enumeration.");
                    var names = retained.Select(volume => volume.VolumeName).Distinct().ToList();
                    using (VolumeLock.Acquire(names, names, force: false)) {
                        Assert.IsTrue(retained.All(volume => volume.PhysicalDriveNumber.HasValue));
                    }
                    Assert.AreEqual(GCLatencyMode.NoGCRegion, GCSettings.LatencyMode);
                    GC.KeepAlive(retained);
                } finally {
                    if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion) { GC.EndNoGCRegion(); }
                }
                disk.Detach();
            }
        }
    }
}
