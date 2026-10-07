using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VhdAttachCommon;

namespace VhdAttachTest {
    [TestClass()]
    public class AttachedDiagnosticTest {
        [TestMethod()]
        [TestCategory("Diagnostic")]
        public void Diagnose_AttachedPath() {
            var file = Environment.GetEnvironmentVariable("VHDSTUDIO_DIAG_FILE");
            if (string.IsNullOrEmpty(file)) { Assert.Inconclusive("Set VHDSTUDIO_DIAG_FILE."); }
            var lines = new System.Collections.Generic.List<string>();
            try {
                var details = VirtualDiskImage.GetDetails(file);
                lines.Add("GetDetails.AttachedPath = " + (details.AttachedPath ?? "(null)") + ", last error = " + VirtualDiskImage.LastPhysicalPathError);
                lines.Add("ParentResolved = " + details.ParentResolved + ", OpenError = " + details.OpenError);
            } catch (Exception ex) { lines.Add("GetDetails threw: " + ex.GetType().Name + ": " + ex.Message); }
            try {
                using (var disk = new Medo.IO.VirtualDisk(file)) {
                    disk.Open(Medo.IO.VirtualDiskAccessMask.GetInfo | Medo.IO.VirtualDiskAccessMask.Detach);
                    lines.Add("Medo GetAttachedPath = " + disk.GetAttachedPath());
                }
            } catch (Exception ex) { lines.Add("Medo threw: " + ex.GetType().Name + ": " + ex.Message); }
            lines.Add("IsAttached (storage) = " + VirtualDiskImage.IsAttached(file));
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "VhdStudioDiag.txt"), lines);
        }
    }
}
