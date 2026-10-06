using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VhdAttachService;

namespace VhdAttachTest {

    [TestClass()]
    public class PipeCallerTest {

        [TestMethod()]
        public void Test_PipeCaller_IdentifiesClientAndChecksFileAccess() {
            var pipeName = "VhdStudioTest-" + Guid.NewGuid().ToString("N");
            var allowed = Path.GetTempFileName();
            var denied = Path.GetTempFileName();
            try {
                var me = WindowsIdentity.GetCurrent().User;
                var acl = new FileInfo(denied).GetAccessControl();
                acl.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.ReadData, AccessControlType.Deny));
                new FileInfo(denied).SetAccessControl(acl);

                using (var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation)) {
                    var connected = server.WaitForConnectionAsync();
                    Assert.IsTrue(client.ConnectAsync(5000).Wait(10000), "client connect");
                    Assert.IsTrue(connected.Wait(5000), "server connect");
                    var buffer = new byte[1];
                    var read = server.ReadAsync(buffer, 0, 1); //pipe has no buffer: the write completes only once it is read
                    Assert.IsTrue(client.WriteAsync(new byte[] { 42 }, 0, 1).Wait(5000), "write");
                    Assert.IsTrue(read.Wait(5000), "read");
                    Assert.AreEqual(42, buffer[0]); //impersonation needs a read first
                    using (var caller = PipeCaller.FromPipe(server.SafePipeHandle.DangerousGetHandle())) {
                        Assert.AreEqual(WindowsIdentity.GetCurrent().Name, caller.Name);
                        Assert.IsNull(WindowsIdentity.GetCurrent(true), "Thread must not keep impersonating the client.");
                        caller.DemandFileAccess(allowed, FileAccess.ReadWrite);
                        Assert.ThrowsExactly<UnauthorizedAccessException>(() => caller.DemandFileAccess(denied, FileAccess.Read));
                    }
                }
            } finally {
                try {
                    var acl = new FileInfo(denied).GetAccessControl();
                    acl.PurgeAccessRules(WindowsIdentity.GetCurrent().User);
                    new FileInfo(denied).SetAccessControl(acl);
                } catch (Exception) { }
                File.Delete(allowed);
                File.Delete(denied);
            }
        }

    }
}
