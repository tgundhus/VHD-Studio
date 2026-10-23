using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VhdAttachCommon;
using VhdStudio.Vsux;

namespace VhdAttachTest {

    [TestClass]
    public class VsuxIntegrationTest {
        private const string Password = "VSUX disposable integration password";
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private string Folder;

        [TestInitialize]
        public void Init() {
            var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "VhdStudioVsux-" + Guid.NewGuid().ToString("N")));
            this.Folder = PathGuard.GetLongPath(directory.FullName);
        }

        [TestCleanup]
        public void Cleanup() {
            if (this.Folder == null || !Directory.Exists(this.Folder)) { return; }
            foreach (var file in Directory.GetFiles(this.Folder)) { File.Delete(file); }
            Directory.Delete(this.Folder); // Only this run's flat directory; no recursive deletion or mounted disks.
        }

        [TestMethod]
        public void Test_ServiceRoutes_RefuseVsuxBeforeOpeningOrCheckingCaller() {
            var container = Path.Combine(this.Folder, "missing.VSUX");
            Assert.IsTrue(VsuxGuard.IsVsux(container));
            Assert.IsFalse(VsuxGuard.IsVsux(container + ".json"));
            Assert.IsFalse(VsuxGuard.IsVsux(Path.ChangeExtension(container, ".vhdx")));

            // A null caller proves the password guard runs before caller access or elevation checks.
            var attachError = Assert.ThrowsExactly<InvalidOperationException>(() =>
                VhdAttachService.AttachHelper.Attach(new FileWithOptions(container), null));
            StringAssert.Contains(attachError.Message, "password");
            Assert.ThrowsExactly<InvalidOperationException>(() => VhdAttachService.AttachHelper.Detach(container, false, null));
            Assert.ThrowsExactly<InvalidOperationException>(() => VhdAttachService.AttachHelper.Detach(container, true, null));
            Assert.ThrowsExactly<FileNotFoundException>(() =>
                VhdAttachService.AttachHelper.Attach(new FileWithOptions(Path.ChangeExtension(container, ".vhdx")), null));
        }

        [TestMethod]
        public void Test_PublicCore_PasswordRoundTripChangesOnlySignature() {
            var image = this.CreateDisposableImage();
            var original = File.ReadAllBytes(image);
            var originalHash = Hash(original);
            var container = Path.ChangeExtension(image, ".vsux");

            VsuxOperations.Protect(image, container, Password);
            Assert.IsFalse(File.Exists(image));
            Assert.IsTrue(File.Exists(container + ".json"));
            var locked = File.ReadAllBytes(container);
            Assert.AreEqual(original.Length, locked.Length);
            Assert.IsTrue(locked.AsSpan(0, 8).SequenceEqual("vsuxlock"u8));
            Assert.IsTrue(original.AsSpan(8).SequenceEqual(locked.AsSpan(8)), "Protect changed bytes outside the eight-byte signature.");
            var info = VsuxOperations.GetInfo(container);
            Assert.IsTrue(info.IsLocked);
            Assert.IsFalse(info.IsAttached);
            Assert.IsNull(info.AttachedPath);
            Assert.AreNotEqual(Guid.Empty, info.DiskId);
            Assert.AreEqual((long)original.Length, info.Length);

            var lockedHash = Hash(locked);
            var companionHash = Hash(File.ReadAllBytes(container + ".json"));
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => VsuxOperations.Unlock(container, "incorrect password"));
            Assert.AreEqual(lockedHash, Hash(File.ReadAllBytes(container)), "A wrong password changed the image.");
            Assert.AreEqual(companionHash, Hash(File.ReadAllBytes(container + ".json")), "A wrong password changed the companion file.");

            VsuxOperations.Unlock(container, Password);
            Assert.AreEqual(originalHash, Hash(File.ReadAllBytes(container)), "The complete image did not survive the signature round trip.");
            var unlocked = VsuxOperations.GetInfo(container);
            Assert.IsFalse(unlocked.IsLocked);
            Assert.IsFalse(unlocked.IsAttached);
            Assert.IsNull(unlocked.AttachedPath);
            Assert.AreEqual(info.DiskId, unlocked.DiskId);
        }

        [TestMethod]
        public void Test_MainForm_VsuxCommandsRemainPasswordGated() {
            var image = this.CreateDisposableImage();
            var container = Path.ChangeExtension(image, ".vsux");
            VsuxOperations.Protect(image, container, Password);

            RunSta(() => {
                // Do not show/load/close the form: those paths persist window and recent-file settings.
                using (var form = new VhdAttach.MainForm()) {
                    typeof(VhdAttach.MainForm).GetField("VhdFileName", PrivateInstance).SetValue(form, container);
                    RefreshCommands(form, container);
                    AssertCommands(form, canLock: false);

                    VsuxOperations.Unlock(container, Password);
                    RefreshCommands(form, container);
                    AssertCommands(form, canLock: true);
                }
            });
        }

        [TestMethod]
        [TestCategory("Elevated")]
        public void Test_MainForm_AttachPromptsForPasswordAndCancelLeavesLockedImageUnchanged() {
            ScratchDisk.RequireElevation();
            var image = this.CreateDisposableImage();
            var container = Path.ChangeExtension(image, ".vsux");
            VsuxOperations.Protect(image, container, Password);
            var lockedHash = Hash(File.ReadAllBytes(container));
            var savedExitCode = Environment.ExitCode;
            try {
                RunSta(() => {
                    using (var form = new VhdAttach.MainForm())
                    using (var timer = new System.Windows.Forms.Timer { Interval = 25 }) {
                        form.StartPosition = FormStartPosition.Manual;
                        form.Location = new Point(-30000, -30000);
                        typeof(VhdAttach.MainForm).GetField("VhdFileName", PrivateInstance).SetValue(form, container);
                        RefreshCommands(form, container);
                        var observedPassword = false;
                        var maskedPassword = false;
                        var timedOut = false;
                        var elapsed = Stopwatch.StartNew();
                        timer.Tick += (sender, args) => {
                            var dialogs = OwnedDialogs(form);
                            foreach (var dialog in dialogs) {
                                dialog.StartPosition = FormStartPosition.Manual;
                                dialog.Location = new Point(-30000, -30000);
                                if (dialog is VhdAttach.VsuxPasswordForm) {
                                    observedPassword = true;
                                    var box = (TextBox)dialog.GetType().GetField("PasswordBox", PrivateInstance).GetValue(dialog);
                                    maskedPassword = box.UseSystemPasswordChar;
                                    dialog.DialogResult = DialogResult.Cancel;
                                }
                            }
                            if (elapsed.Elapsed < TimeSpan.FromSeconds(10)) { return; }
                            timedOut = true;
                            // Close deepest owned dialogs first. Never close the unshown MainForm.
                            for (var i = dialogs.Count - 1; i >= 0; i--) {
                                dialogs[i].DialogResult = DialogResult.Cancel;
                                dialogs[i].Close();
                            }
                        };
                        try {
                            timer.Start();
                            ((ToolStripSplitButton)Command(form, "mnuAttach")).PerformButtonClick();
                        } finally {
                            timer.Stop();
                            var remaining = OwnedDialogs(form);
                            for (var i = remaining.Count - 1; i >= 0; i--) {
                                remaining[i].DialogResult = DialogResult.Cancel;
                                remaining[i].Close();
                            }
                        }
                        Assert.IsFalse(timedOut, "The Attach button did not finish the password cancellation within ten seconds.");
                        Assert.IsTrue(observedPassword, "The Attach button did not open the VSUX password prompt.");
                        Assert.IsTrue(maskedPassword, "The VSUX prompt must mask the password by default.");
                    }
                });
            } finally { Environment.ExitCode = savedExitCode; }
            Assert.AreEqual(lockedHash, Hash(File.ReadAllBytes(container)), "Cancelling Attach changed the locked image.");
            Assert.IsTrue(VsuxOperations.GetInfo(container).IsLocked, "Cancelling Attach restored the VHDX signature.");
        }

        private string CreateDisposableImage() {
            var path = Path.Combine(this.Folder, "disposable.vhdx");
            try {
                using (var disk = new Medo.IO.VirtualDisk(path)) {
                    disk.Create(64L * 1024 * 1024, Medo.IO.VirtualDiskCreateOptions.None, 0, 0, Medo.IO.VirtualDiskType.Vhdx);
                }
            } catch (Exception ex) when (ex is Win32Exception || ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException) {
                Assert.Inconclusive("Cannot create disposable VHDX files in this environment: " + ex.Message);
            }
            return path;
        }

        private static void RefreshCommands(VhdAttach.MainForm form, string path) {
            typeof(VhdAttach.MainForm).GetMethod("UpdateData", PrivateInstance).Invoke(form, new object[] { path });
            typeof(VhdAttach.MainForm).GetMethod("tmrUpdateMenu_Tick", PrivateInstance).Invoke(form, new object[] { null, EventArgs.Empty });
        }

        private static void AssertCommands(VhdAttach.MainForm form, bool canLock) {
            Assert.IsTrue(Command(form, "mnuAttach").Enabled, "Attach must remain available for the password prompt.");
            Assert.AreEqual(canLock, Command(form, "mnuDetach").Enabled, "Only an unlocked VSUX image can be detached and locked.");
            Assert.AreEqual("Detach && lock", Command(form, "mnuDetach").Text);
            foreach (var name in new[] { "mnuMaintenance", "mnuAutomount", "mnuDrive", "mnuProtectVsux" }) {
                Assert.IsFalse(Command(form, name).Enabled, name + " must remain unavailable for VSUX images.");
            }
        }

        private static ToolStripItem Command(VhdAttach.MainForm form, string name) =>
            (ToolStripItem)typeof(VhdAttach.MainForm).GetField(name, PrivateInstance).GetValue(form);

        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

        private static List<Form> OwnedDialogs(Form owner) {
            var dialogs = new List<Form>();
            foreach (Form dialog in Application.OpenForms) {
                if (dialog == owner || dialog.InvokeRequired) { continue; }
                for (var ancestor = dialog.Owner; ancestor != null; ancestor = ancestor.Owner) {
                    if (ancestor == owner) { dialogs.Add(dialog); break; }
                }
            }
            return dialogs;
        }

        private static void RunSta(Action action) {
            Exception failure = null;
            var thread = new Thread(() => {
                try { action(); }
                catch (Exception ex) { failure = ex is TargetInvocationException { InnerException: not null } invocation ? invocation.InnerException : ex; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "The VSUX UI state check did not complete.");
            if (failure != null) { ExceptionDispatchInfo.Capture(failure).Throw(); }
        }
    }
}
