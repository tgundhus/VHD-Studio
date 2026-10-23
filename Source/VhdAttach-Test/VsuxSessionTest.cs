using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;
using VhdAttachCommon;
using VhdStudio.Vsux;

namespace VhdAttachTest {

    [TestClass]
    [DoNotParallelize]
    public class VsuxSessionTest {
        private const string Password = "VSUX disposable unlock session password";
        private const string SessionKey = @"Software\xGND Software\VHD Studio\VSUX\UnlockedSessions";
        private readonly List<Guid> DiskIds = new List<Guid>();
        private string Folder;

        [TestInitialize]
        public void Init() {
            this.Folder = PathGuard.GetLongPath(Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "VhdStudioVsuxSession-" + Guid.NewGuid().ToString("N"))).FullName);
        }

        [TestCleanup]
        public void Cleanup() {
            try {
                if (this.Folder == null || !Directory.Exists(this.Folder)) { return; }
                foreach (var file in Directory.GetFiles(this.Folder)) {
                    if (VsuxOperations.IsVsuxPath(file)) { VsuxOperations.Lock(file, Password); }
                    File.Delete(file);
                }
                Directory.Delete(this.Folder); // Only this run's flat directory; lock safely detaches its own fixtures first.
            } finally {
                using (var key = Registry.CurrentUser.OpenSubKey(SessionKey, writable: true)) {
                    foreach (var id in this.DiskIds) { key?.DeleteValue(id.ToString("D"), throwOnMissingValue: false); }
                }
            }
        }

        [TestMethod]
        [TestCategory("Elevated")]
        public void CachedRelock_RefusesOpenFilesKeepsSessionAndPreservesContent() {
            ScratchDisk.RequireElevation();
            var container = this.CreateProtected(prepareNtfs: true);
            Assert.IsFalse(VsuxOperations.HasUnlockSession(container));
            VsuxOperations.Attach(container, Password);
            Assert.IsTrue(VsuxOperations.HasUnlockSession(container));
            var volume = WaitForVolume(VsuxOperations.GetInfo(container).AttachedPath);
            var content = System.Security.Cryptography.RandomNumberGenerator.GetBytes(1024 * 1024);
            var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));
            using (var file = new FileStream(Path.Combine(volume.VolumeName, "busy-session.bin"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read)) {
                file.Write(content);
                file.Flush(flushToDisk: true);
                AssertBusyRefusal(() => VsuxOperations.LockUsingSession(container));
                Assert.IsTrue(VsuxOperations.GetInfo(container).IsAttached, "A busy relock must leave the drive attached.");
                Assert.IsFalse(VsuxOperations.GetInfo(container).IsLocked, "A busy relock must not mask an attached image.");
                Assert.IsTrue(VsuxOperations.HasUnlockSession(container), "A busy relock must retain the authorization for a later retry.");
            }
            VsuxOperations.LockUsingSession(container);
            Assert.IsTrue(VsuxOperations.GetInfo(container).IsLocked);
            Assert.IsFalse(VsuxOperations.HasUnlockSession(container), "A completed relock must remove its authorization.");
            VsuxOperations.Attach(container, Password, readOnly: true);
            var restored = WaitForVolume(VsuxOperations.GetInfo(container).AttachedPath);
            using (var file = File.OpenRead(Path.Combine(restored.VolumeName, "busy-session.bin"))) {
                Assert.AreEqual(expected, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file)), "Password-free relock changed the NTFS payload.");
            }
            VsuxOperations.LockUsingSession(container);
        }

        [TestMethod]
        [TestCategory("Elevated")]
        public void CopiedImage_CannotReuseAnotherFilesUnlockSession() {
            ScratchDisk.RequireElevation();
            var original = this.CreateProtected();
            var copy = Path.Combine(this.Folder, "copied.vsux");
            File.Copy(original, copy, overwrite: false);
            VsuxOperations.Unlock(copy, Password);
            VsuxOperations.Attach(original, Password, readOnly: true);
            Assert.AreEqual(VsuxOperations.GetInfo(original).DiskId, VsuxOperations.GetInfo(copy).DiskId, "The fixture must retain the copied disk identifier.");
            Assert.IsTrue(VsuxOperations.HasUnlockSession(original));
            var hash = ScratchDisk.HashFile(copy);
            Assert.IsFalse(VsuxOperations.HasUnlockSession(copy), "A disk identifier alone must not authorize a different file identity.");
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => VsuxOperations.LockUsingSession(copy));
            Assert.AreEqual(hash, ScratchDisk.HashFile(copy), "An unauthorized cached relock changed the copied file.");
            Assert.IsTrue(VsuxOperations.HasUnlockSession(original), "Looking up a copied image must not discard the original image's valid authorization.");
            VsuxOperations.Lock(copy, Password);
            Assert.IsTrue(VsuxOperations.GetInfo(copy).IsLocked);
            Assert.IsFalse(VsuxOperations.HasUnlockSession(copy));
            Assert.IsTrue(VsuxOperations.GetInfo(original).IsAttached);
            Assert.IsTrue(VsuxOperations.HasUnlockSession(original), "Password-locking a copy with the same disk identifier must preserve the original attached file's authorization.");
            VsuxOperations.LockUsingSession(original);
            Assert.IsTrue(VsuxOperations.GetInfo(original).IsLocked);
            Assert.IsFalse(VsuxOperations.HasUnlockSession(original));
        }

        [TestMethod]
        [TestCategory("Elevated")]
        public void WrongPasswordOrStandaloneUnlock_DoesNotAuthorizePasswordFreeRelock() {
            ScratchDisk.RequireElevation();
            var container = this.CreateProtected();
            var hash = ScratchDisk.HashFile(container);
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => VsuxOperations.Attach(container, "incorrect password"));
            Assert.AreEqual(hash, ScratchDisk.HashFile(container));
            Assert.IsFalse(VsuxOperations.HasUnlockSession(container));
            VsuxOperations.Unlock(container, Password);
            Assert.IsFalse(VsuxOperations.HasUnlockSession(container), "Restoring the signature alone must not create an attach session.");
            hash = ScratchDisk.HashFile(container);
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => VsuxOperations.LockUsingSession(container));
            Assert.AreEqual(hash, ScratchDisk.HashFile(container));
        }

        [TestMethod]
        [TestCategory("Elevated")]
        public void FailedAttach_AfterManualNativeMountDoesNotCreateSession() {
            ScratchDisk.RequireElevation();
            var container = this.CreateProtected();
            VsuxOperations.Unlock(container, Password);
            var native = typeof(VsuxOperations).Assembly.GetType("VhdStudio.Vsux.NativeDisk", throwOnError: true);
            try { native.GetMethod("Mount", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { container, true, true }); }
            catch (TargetInvocationException error) when (error.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
            Assert.IsTrue(VsuxOperations.GetInfo(container).IsAttached);
            Assert.IsFalse(VsuxOperations.HasUnlockSession(container));
            AssertBusyRefusal(() => VsuxOperations.Attach(container, Password, readOnly: true));
            Assert.IsFalse(VsuxOperations.HasUnlockSession(container), "A failed attach must not grant cached relock permission.");
        }

        [TestMethod]
        [TestCategory("Elevated")]
        public void CachedRelock_WorksInANewApplicationProcessWithoutPasswordArguments() {
            ScratchDisk.RequireElevation();
            var container = this.CreateProtected();
            VsuxOperations.Attach(container, Password, readOnly: true);
            Assert.IsTrue(VsuxOperations.HasUnlockSession(container));
            var executable = Path.Combine(AppContext.BaseDirectory, "VhdStudio.exe");
            Assert.IsTrue(File.Exists(executable), "The application host is required for the cross-process relock regression.");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("/VsuxLock");
            start.ArgumentList.Add(container); // No password is sent on the command line or through the environment.
            using (var child = Process.Start(start)) {
                Assert.IsNotNull(child);
                if (!child.WaitForExit(30_000)) {
                    child.Kill(entireProcessTree: false); // Only this test's own helper, never another application or service.
                    child.WaitForExit(5_000);
                    Assert.Fail("A fresh helper process requested input or failed to finish cached relock within thirty seconds.");
                }
                Assert.AreEqual(0, child.ExitCode);
            }
            Assert.IsTrue(VsuxOperations.GetInfo(container).IsLocked, "The fresh helper failed to relock the image using persistent authorization.");
            Assert.IsFalse(VsuxOperations.HasUnlockSession(container));
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        [TestCategory("Elevated")]
        public void RelockDialog_UsesValidSessionOrPromptsAgainForTamperedReceipt(bool corruptReceipt) {
            ScratchDisk.RequireElevation();
            var container = this.CreateProtected();
            VsuxOperations.Attach(container, Password, readOnly: true);
            Assert.IsTrue(VsuxOperations.HasUnlockSession(container));
            if (corruptReceipt) {
                var id = VsuxOperations.GetInfo(container).DiskId;
                using (var key = Registry.CurrentUser.OpenSubKey(SessionKey, writable: true)) {
                    Assert.IsNotNull(key);
                    var bytes = key.GetValue(id.ToString("D")) as byte[];
                    Assert.IsNotNull(bytes);
                    Assert.IsTrue(bytes.Length > 32);
                    bytes[bytes.Length - 1] ^= 0x40; // Corrupt only this freshly created fixture's protected receipt.
                    key.SetValue(id.ToString("D"), bytes, RegistryValueKind.Binary);
                }
                Assert.IsFalse(VsuxOperations.HasUnlockSession(container), "An invalid DPAPI receipt must fail closed.");
            }
            var savedExitCode = Environment.ExitCode;
            var passwordObserved = false;
            try {
                VsuxCreationTest.RunSta(() => {
                    using (var form = new VhdAttach.VsuxForm("Lock", container))
                    using (var timer = new System.Windows.Forms.Timer { Interval = 25 }) {
                        form.StartPosition = FormStartPosition.Manual;
                        form.Location = new Point(-30000, -30000);
                        var elapsed = Stopwatch.StartNew();
                        var timedOut = false;
                        timer.Tick += (sender, args) => {
                            foreach (var password in form.OwnedForms.OfType<VhdAttach.VsuxPasswordForm>()) {
                                passwordObserved = true;
                                password.StartPosition = FormStartPosition.Manual;
                                password.Location = new Point(-30000, -30000);
                                password.DialogResult = DialogResult.Cancel;
                            }
                            if (elapsed.Elapsed < TimeSpan.FromSeconds(20)) { return; }
                            timedOut = true;
                            foreach (var child in form.OwnedForms) { child.DialogResult = DialogResult.Cancel; }
                        };
                        DialogResult result;
                        try { timer.Start(); result = form.ShowDialog(); }
                        finally { timer.Stop(); }
                        Assert.IsFalse(timedOut);
                        Assert.AreEqual(corruptReceipt ? DialogResult.Cancel : DialogResult.OK, result);
                    }
                });
            } finally { Environment.ExitCode = savedExitCode; }
            Assert.AreEqual(corruptReceipt, passwordObserved, "A valid attach session must relock immediately; an invalid receipt must fall back to the password prompt.");
            var info = VsuxOperations.GetInfo(container);
            Assert.AreEqual(!corruptReceipt, info.IsLocked);
            Assert.AreEqual(corruptReceipt, info.IsAttached, "Cancelling fallback authentication must leave the attached image unchanged.");
            Assert.IsFalse(VsuxOperations.HasUnlockSession(container));
        }

        private string CreateProtected(bool prepareNtfs = false) {
            var container = Path.Combine(this.Folder, "disposable-" + Guid.NewGuid().ToString("N") + ".vsux");
            VhdAttach.VsuxCreation.Create(container, 64L * 1024 * 1024, false, Password, prepareNtfs);
            this.DiskIds.Add(VsuxOperations.GetInfo(container).DiskId);
            return container;
        }

        private static Volume WaitForVolume(string attachedPath) {
            for (var attempt = 0; attempt < 60; attempt++) {
                var volume = Volume.GetVolumesOnPhysicalDrive(attachedPath).FirstOrDefault(candidate => Directory.Exists(candidate.VolumeName));
                if (volume != null) { return volume; }
                Thread.Sleep(250);
            }
            throw new AssertFailedException("The disposable NTFS volume did not appear.");
        }

        private static void AssertBusyRefusal(Action action) {
            try { action(); Assert.Fail("An in-use virtual disk was accepted."); }
            catch (IOException) { }
            catch (Win32Exception error) { Assert.IsTrue(error.NativeErrorCode is 5 or 32 or 33 or 170, "Unexpected native error: " + error.Message); }
        }
    }
}
