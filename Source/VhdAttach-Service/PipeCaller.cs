using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace VhdAttachService {

    /// <summary>
    /// Identity of the user on the other end of the command pipe.
    /// The service runs as Local System, so every file it touches on behalf of a client is first opened
    /// with the client's own token: users can only attach/detach/auto-mount files they can access themselves.
    /// </summary>
    internal sealed class PipeCaller : IDisposable {

        private readonly WindowsIdentity Identity;

        private PipeCaller(WindowsIdentity identity) {
            this.Identity = identity;
        }

        /// <summary>
        /// Captures the client identity. Must be called after at least one read from the pipe.
        /// </summary>
        public static PipeCaller FromPipe(IntPtr pipeHandle) {
            if (!NativeMethods.ImpersonateNamedPipeClient(pipeHandle)) { throw new Win32Exception(); }
            try {
                return new PipeCaller(WindowsIdentity.GetCurrent(System.Security.Principal.TokenAccessLevels.Query | System.Security.Principal.TokenAccessLevels.Duplicate | System.Security.Principal.TokenAccessLevels.Impersonate));
            } finally {
                if (!NativeMethods.RevertToSelf()) { Environment.FailFast("RevertToSelf failed."); } //never continue as the client
            }
        }

        public string Name => this.Identity.Name;

        /// <summary>
        /// True for an elevated administrator (UAC-filtered tokens do not count).
        /// </summary>
        public bool IsAdministrator => new WindowsPrincipal(this.Identity).IsInRole(WindowsBuiltInRole.Administrator) || this.Identity.IsSystem;

        /// <summary>
        /// Throws UnauthorizedAccessException unless the caller can open the file with the given access.
        /// </summary>
        public void DemandFileAccess(string path, FileAccess access) {
            if (this.Identity.IsSystem) { return; }
            try {
                if ((access != FileAccess.Read) && File.Exists(path) && ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)) {
                    access = FileAccess.Read; //attach reports the read-only attribute with a clearer message
                }
                WindowsIdentity.RunImpersonated(this.Identity.AccessToken, () => {
                    using (new FileStream(path, FileMode.Open, access, FileShare.ReadWrite | FileShare.Delete)) { }
                });
            } catch (UnauthorizedAccessException ex) {
                throw new UnauthorizedAccessException(string.Format("{0} does not have {1} access to \"{2}\".", this.Name, (access == FileAccess.Read) ? "read" : "read/write", Path.GetFileName(path)), ex);
            } catch (IOException) {
                //sharing violations, file in use by the VHD driver etc. are not permission problems
            }
        }

        public void DemandAdministrator(string operation) {
            if (!this.IsAdministrator) {
                throw new UnauthorizedAccessException(operation + " requires administrator rights (run elevated).");
            }
        }

        public void Dispose() {
            this.Identity.Dispose();
        }


        private static class NativeMethods {
            [DllImport("advapi32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool ImpersonateNamedPipeClient(IntPtr hNamedPipe);

            [DllImport("advapi32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool RevertToSelf();
        }

    }
}
