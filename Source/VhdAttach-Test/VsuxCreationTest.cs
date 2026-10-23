using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
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
    public class VsuxCreationTest {
        private const long DiskSize = 64L * 1024 * 1024;
        private const string Password = "VSUX disposable creation password";
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private string Folder;

        [TestInitialize]
        public void Init() {
            this.Folder = PathGuard.GetLongPath(Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "VhdStudioVsuxCreate-" + Guid.NewGuid().ToString("N"))).FullName);
        }

        [TestCleanup]
        public void Cleanup() {
            if (this.Folder == null || !Directory.Exists(this.Folder)) { return; }
            foreach (var file in Directory.GetFiles(this.Folder)) { File.Delete(file); }
            Directory.Delete(this.Folder); // Only this test's flat directory, with all native handles disposed.
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void Test_CreateVsux_DynamicAndFixedProduceLockedNativeVhdx(bool fixedSize) {
            this.ProbeNativeCreation();
            var target = Path.Combine(this.Folder, "new-disk.vsux");

            VhdAttach.VsuxCreation.Create(target, DiskSize, fixedSize, Password);

            CollectionAssert.AreEquivalent(new[] { target, target + ".json" }, Directory.GetFiles(this.Folder), "Creation must leave only the locked container and its companion, without a plaintext scratch image.");
            var info = VsuxOperations.GetInfo(target);
            Assert.IsTrue(info.IsLocked);
            Assert.IsFalse(info.IsAttached, "Creating a protected disk must not attach it or leave its signature unlocked.");
            Assert.AreNotEqual(Guid.Empty, info.DiskId);
            var imageHash = ScratchDisk.HashFile(target);
            var companionHash = ScratchDisk.HashFile(target + ".json");
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => VsuxOperations.Unlock(target, "incorrect password"));
            Assert.AreEqual(imageHash, ScratchDisk.HashFile(target));
            Assert.AreEqual(companionHash, ScratchDisk.HashFile(target + ".json"));

            VsuxOperations.Unlock(target, Password);
            // Generic Maintenance excludes VSUX; inspect only this unlocked disposable fixture under its native extension.
            var nativePath = Path.ChangeExtension(target, ".vhdx");
            File.Move(target, nativePath);
            var details = VirtualDiskImage.GetDetails(nativePath);
            Assert.AreEqual("VHDX", details.Format, "Windows must recognize the unlocked result as a real VHDX.");
            Assert.AreEqual(DiskSize, details.VirtualSize);
            Assert.AreEqual(fixedSize ? VirtualDiskKind.Fixed : VirtualDiskKind.Dynamic, details.Kind);
            Assert.IsNull(details.AttachedPath);
            Assert.IsFalse(details.NeedsLogReplay);
            using (var stream = File.OpenRead(nativePath)) {
                var signature = new byte[8];
                stream.ReadExactly(signature);
                CollectionAssert.AreEqual("vhdxfile"u8.ToArray(), signature);
            }
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void Test_CreateVsux_ExistingTargetOrCompanionIsNeverChanged(bool companionExists) {
            var target = Path.Combine(this.Folder, "existing.vsux");
            var existing = companionExists ? target + ".json" : target;
            File.WriteAllBytes(existing, RandomNumberGenerator.GetBytes(4096));
            var originalHash = ScratchDisk.HashFile(existing);

            Assert.ThrowsExactly<IOException>(() => VhdAttach.VsuxCreation.Create(target, DiskSize, false, Password));

            Assert.AreEqual(originalHash, ScratchDisk.HashFile(existing), "A name collision changed the existing file.");
            CollectionAssert.AreEquivalent(new[] { existing }, Directory.GetFiles(this.Folder), "A refused creation left an image or plaintext scratch file behind.");
        }

        [TestMethod]
        public void Test_CreateVsux_InvalidPasswordCreatesNothing() {
            var target = Path.Combine(this.Folder, "never-created.vsux");
            Assert.ThrowsExactly<ArgumentException>(() => VhdAttach.VsuxCreation.Create(target, DiskSize, false, "short"));
            Assert.AreEqual(0, Directory.GetFiles(this.Folder).Length, "Password validation must finish before creating either the disk or its companion.");
        }

        [TestMethod]
        public void Test_NewDiskForm_OffersVisibleSelectableVsuxFormat() {
            RunSta(() => {
                using (var form = new VhdAttach.NewDiskForm()) {
                    var settingsHandler = typeof(VhdAttach.NewDiskForm).GetMethod("Form_FormClosed", PrivateInstance);
                    form.FormClosed -= (FormClosedEventHandler)settingsHandler.CreateDelegate(typeof(FormClosedEventHandler), form);
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-30000, -30000);
                    form.Show();
                    try {
                        var vsux = (RadioButton)typeof(VhdAttach.NewDiskForm).GetField("radFormatVsux", PrivateInstance).GetValue(form);
                        Assert.IsTrue(vsux.Visible, "New virtual disk must visibly offer the VSUX format.");
                        Assert.IsTrue(vsux.Enabled);
                        StringAssert.Contains(vsux.Text, "VSUX");
                        Assert.IsTrue(vsux.Parent.ClientRectangle.Contains(vsux.Bounds), "The VSUX option is clipped by the format group.");
                        vsux.Checked = true;
                        Assert.IsTrue(vsux.Checked);
                        Assert.IsFalse(((RadioButton)typeof(VhdAttach.NewDiskForm).GetField("radFormatVhdX", PrivateInstance).GetValue(form)).Checked);
                        var help = (Label)typeof(VhdAttach.NewDiskForm).GetField("lblVsuxHelp", PrivateInstance).GetValue(form);
                        Assert.IsTrue(help.Visible, "Selecting VSUX must explain the password prompt and its unencrypted payload.");
                        StringAssert.Contains(help.Text, "unencrypted");
                        Assert.IsTrue(help.Parent.ClientRectangle.Contains(help.Bounds), "The VSUX explanation is clipped by the format group.");
                    } finally {
                        form.Hide(); // Dispose without FormClosed: the test must not persist the user's last format or size.
                    }
                }
            });
        }

        [TestMethod]
        public void Test_Maintenance_OffersSoftProtectionForDetachedVhdxAndRefusesVhd() {
            this.ProbeNativeCreation();
            foreach (var extension in new[] { ".vhdx", ".vhd" }) {
                var file = this.CreateNativeImage(extension);
                RunSta(() => this.AssertProtectionPage(file, extension == ".vhdx"));
            }
        }

        [TestMethod]
        [TestCategory("Elevated")]
        public void Test_Maintenance_RefusesSoftProtectionForAttachedVhdx() {
            ScratchDisk.RequireElevation();
            this.ProbeNativeCreation();
            var file = this.CreateNativeImage(".vhdx");
            using (var disk = ScratchDisk.Open(file)) {
                disk.Attach(readOnly: true);
                try {
                    RunSta(() => this.AssertProtectionPage(file, false));
                } finally { disk.Detach(); }
            }
        }

        [TestMethod]
        [TestCategory("Elevated")]
        public void Test_CreateVsux_PasswordCancelHappensBeforeAnyImageIsCreated() {
            ScratchDisk.RequireElevation();
            var target = Path.Combine(this.Folder, "cancelled.vsux");
            var savedExitCode = Environment.ExitCode;
            try {
                RunSta(() => {
                    using (var form = new VhdAttach.VsuxForm("Create", target, sizeInBytes: DiskSize))
                    using (var timer = new System.Windows.Forms.Timer { Interval = 25 }) {
                        form.StartPosition = FormStartPosition.Manual;
                        form.Location = new Point(-30000, -30000);
                        var observed = false;
                        var masked = false;
                        var emptyBeforePrompt = false;
                        var timedOut = false;
                        var elapsed = Stopwatch.StartNew();
                        timer.Tick += (sender, args) => {
                            foreach (var password in form.OwnedForms.OfType<VhdAttach.VsuxPasswordForm>()) {
                                password.StartPosition = FormStartPosition.Manual;
                                password.Location = new Point(-30000, -30000);
                                observed = true;
                                masked = ((TextBox)password.GetType().GetField("PasswordBox", PrivateInstance).GetValue(password)).UseSystemPasswordChar;
                                emptyBeforePrompt = Directory.GetFiles(this.Folder).Length == 0;
                                password.DialogResult = DialogResult.Cancel;
                            }
                            if (elapsed.Elapsed < TimeSpan.FromSeconds(10)) { return; }
                            timedOut = true;
                            foreach (var dialog in form.OwnedForms) { dialog.DialogResult = DialogResult.Cancel; }
                        };
                        DialogResult result;
                        try {
                            timer.Start();
                            result = form.ShowDialog();
                        } finally { timer.Stop(); }
                        Assert.IsFalse(timedOut, "The creation password prompt did not complete cancellation within ten seconds.");
                        Assert.IsTrue(observed, "VSUX creation did not ask for a password.");
                        Assert.IsTrue(masked);
                        Assert.IsTrue(emptyBeforePrompt, "VSUX creation wrote files before the password was accepted.");
                        Assert.AreEqual(DialogResult.Cancel, result);
                    }
                });
            } finally { Environment.ExitCode = savedExitCode; }
            Assert.AreEqual(0, Directory.GetFiles(this.Folder).Length, "Cancelling the password prompt must leave neither a container, companion nor plaintext scratch image.");
        }

        private void AssertProtectionPage(string file, bool enabled) {
            using (var form = new VhdAttach.MaintenanceForm(file)) {
                typeof(VhdAttach.MaintenanceForm).GetMethod("ReloadDetails", PrivateInstance).Invoke(form, null);
                var tasks = (ListBox)typeof(VhdAttach.MaintenanceForm).GetField("TaskList", PrivateInstance).GetValue(form);
                Assert.IsTrue(tasks.Items.Contains("Soft protection"), "Maintenance must visibly list soft protection alongside its existing tasks.");
                tasks.SelectedItem = "Soft protection";
                var protect = Descendants(form).OfType<Button>().Single(button => button.Name == "ProtectVsuxButton");
                Assert.AreEqual(enabled, protect.Enabled, "Protection must be offered only for a detached, eligible VHDX.");
                StringAssert.Contains(protect.AccessibleName, "Protect as VSUX");
            }
        }

        private string CreateNativeImage(string extension) {
            var path = Path.Combine(this.Folder, "native-" + Guid.NewGuid().ToString("N") + extension);
            using (var disk = new Medo.IO.VirtualDisk(path)) {
                disk.Create(DiskSize, Medo.IO.VirtualDiskCreateOptions.None, 0, 0,
                    extension == ".vhdx" ? Medo.IO.VirtualDiskType.Vhdx : Medo.IO.VirtualDiskType.Vhd);
            }
            return path;
        }

        private void ProbeNativeCreation() {
            string probe = null;
            try { probe = this.CreateNativeImage(".vhdx"); }
            catch (Exception ex) when (ex is Win32Exception || ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException) {
                Assert.Inconclusive("Cannot create disposable native VHDX files in this environment: " + ex.Message);
            } finally { if (probe != null) { File.Delete(probe); } }
        }

        private static IEnumerable<Control> Descendants(Control parent) {
            foreach (Control child in parent.Controls) {
                yield return child;
                foreach (var descendant in Descendants(child)) { yield return descendant; }
            }
        }

        private static void RunSta(Action action) {
            Exception failure = null;
            var thread = new Thread(() => {
                try { action(); }
                catch (Exception ex) { failure = ex is TargetInvocationException { InnerException: not null } invocation ? invocation.InnerException : ex; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "The VSUX UI check did not complete.");
            if (failure != null) { ExceptionDispatchInfo.Capture(failure).Throw(); }
        }
    }
}
