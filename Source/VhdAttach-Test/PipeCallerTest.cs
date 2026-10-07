using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VhdAttachCommon;
using VhdAttachService;

namespace VhdAttachTest {

    [TestClass()]
    public class PipeCallerTest {

        private static PipeCaller Connect(TokenImpersonationLevel level, out NamedPipeServerStream server, out NamedPipeClientStream client) {
            var pipeName = "VhdStudioTest-" + Guid.NewGuid().ToString("N");
            server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, level);
            var connected = server.WaitForConnectionAsync();
            Assert.IsTrue(client.ConnectAsync(5000).Wait(10000), "client connect");
            Assert.IsTrue(connected.Wait(5000), "server connect");
            var buffer = new byte[1];
            var read = server.ReadAsync(buffer, 0, 1); //pipe has no buffer: the write completes only once it is read
            Assert.IsTrue(client.WriteAsync(new byte[] { 42 }, 0, 1).Wait(5000), "write");
            Assert.IsTrue(read.Wait(5000), "read"); //impersonation needs a read first
            return PipeCaller.FromPipe(server.SafePipeHandle.DangerousGetHandle());
        }

        [TestMethod()]
        public void Test_PipeCaller_IdentifiesClientAndChecksFileAccess() {
            var allowed = Path.GetTempFileName();
            var denied = Path.GetTempFileName();
            NamedPipeServerStream server = null; NamedPipeClientStream client = null;
            try {
                var acl = new FileInfo(denied).GetAccessControl();
                acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.ReadData, AccessControlType.Deny));
                new FileInfo(denied).SetAccessControl(acl);

                using (var caller = Connect(TokenImpersonationLevel.Impersonation, out server, out client)) {
                    Assert.AreEqual(WindowsIdentity.GetCurrent().Name, caller.Name);
                    Assert.IsNull(WindowsIdentity.GetCurrent(true), "Thread must not keep impersonating the client.");
                    using (var guard = caller.GuardFile(allowed, FileAccess.ReadWrite)) {
                        Assert.IsNotNull(guard.FinalPath);
                    }
                    Assert.ThrowsExactly<UnauthorizedAccessException>(() => caller.GuardFile(denied, FileAccess.Read));
                    Assert.ThrowsExactly<UnauthorizedAccessException>(() => caller.GuardFile(allowed + ".missing", FileAccess.Read), "Unverifiable access must be denied, not skipped.");
                }
            } finally {
                server?.Dispose(); client?.Dispose();
                try {
                    var acl = new FileInfo(denied).GetAccessControl();
                    acl.PurgeAccessRules(WindowsIdentity.GetCurrent().User);
                    new FileInfo(denied).SetAccessControl(acl);
                } catch (Exception) { }
                File.Delete(allowed);
                File.Delete(denied);
            }
        }

        [TestMethod()]
        public void Test_PipeCaller_RejectsIdentificationOnlyClient() { //review finding C1
            NamedPipeServerStream server = null; NamedPipeClientStream client = null;
            try {
                Assert.ThrowsExactly<UnauthorizedAccessException>(() => Connect(TokenImpersonationLevel.Identification, out server, out client).Dispose());
            } finally {
                server?.Dispose(); client?.Dispose();
            }
        }

        [TestMethod()]
        public void Test_PathGuard_RejectsJunctionInPath() { //review finding H1
            var root = Path.Combine(Path.GetTempPath(), "VhdStudioGuard-" + Guid.NewGuid().ToString("N"));
            var real = Directory.CreateDirectory(Path.Combine(root, "real")).FullName;
            var junction = Path.Combine(root, "junction");
            File.WriteAllText(Path.Combine(real, "disk.vhdx"), "x");
            try {
                using (var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + junction + "\" \"" + real + "\"") { CreateNoWindow = true, UseShellExecute = false })) { p.WaitForExit(); }
                Assert.IsTrue(Directory.Exists(junction), "junction created");
                using (PathGuard.Open(Path.Combine(real, "disk.vhdx"), false, true)) { }
                Assert.ThrowsExactly<UnauthorizedAccessException>(() => PathGuard.Open(Path.Combine(junction, "disk.vhdx"), false, true));
            } finally {
                if (Directory.Exists(junction)) { Directory.Delete(junction); } //removes the link only
                Directory.Delete(root, true);
            }
        }

        [TestMethod()]
        public void Test_PathGuard_RejectsHardLinkedFile() { //review finding H1 (hard link variant)
            var original = Path.GetTempFileName();
            var link = original + ".link";
            try {
                Assert.IsTrue(CreateHardLink(link, original, IntPtr.Zero), "hard link created");
                Assert.ThrowsExactly<UnauthorizedAccessException>(() => PathGuard.Open(link, false, true));
                using (PathGuard.Open(link, false, false)) { } //allowed when not strict (administrators)
            } finally {
                File.Delete(link);
                File.Delete(original);
            }
        }

        [TestMethod()]
        public void Test_PathGuard_ResolvesShortNamesAndReportsFinalPath() {
            var file = Path.GetTempFileName(); //temp path often contains 8.3 names (e.g. USERNA~1)
            try {
                using (var guard = PathGuard.Open(file, false, true)) {
                    Assert.IsTrue(File.Exists(guard.FinalPath));
                    Assert.IsFalse(guard.FinalPath.Contains("~"), "Final path must be the long form.");
                }
            } finally {
                File.Delete(file);
            }
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    }
}
