using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using VhdAttachCommon;

namespace VhdAttachService {

    /// <summary>
    /// Identity of the user on the other end of the command pipe.
    /// The service runs as Local System, so every file it touches on behalf of a client is first opened
    /// with the client's own token, then pinned (PathGuard) so it cannot be swapped before the service uses it.
    /// </summary>
    internal sealed class PipeCaller : IDisposable {

        private const int HResultSharingViolation = unchecked((int)0x80070020);
        private const int HResultLockViolation = unchecked((int)0x80070021);

        private readonly WindowsIdentity Identity;

        private PipeCaller(WindowsIdentity identity) {
            this.Identity = identity;
        }

        /// <summary>
        /// Captures the client identity. Must be called after at least one read from the pipe.
        /// </summary>
        /// <exception cref="UnauthorizedAccessException">Client did not allow impersonation (identification-only token).</exception>
        public static PipeCaller FromPipe(IntPtr pipeHandle) {
            if (!NativeMethods.ImpersonateNamedPipeClient(pipeHandle)) { throw new Win32Exception(); }
            WindowsIdentity identity;
            try {
                identity = WindowsIdentity.GetCurrent(System.Security.Principal.TokenAccessLevels.Query | System.Security.Principal.TokenAccessLevels.Duplicate | System.Security.Principal.TokenAccessLevels.Impersonate);
            } finally {
                if (!NativeMethods.RevertToSelf()) { Environment.FailFast("RevertToSelf failed."); } //never continue as the client
            }
            if (identity.ImpersonationLevel < TokenImpersonationLevel.Impersonation) { //identification tokens cannot open files: checks would be meaningless
                identity.Dispose();
                throw new UnauthorizedAccessException("The client must allow impersonation.");
            }
            return new PipeCaller(identity);
        }

        /// <summary>
        /// Wraps the service's own identity (boot-time auto-attach).
        /// </summary>
        public static PipeCaller ForService() {
            return new PipeCaller(WindowsIdentity.GetCurrent());
        }

        public string Name => this.Identity.Name;

        /// <summary>
        /// True for an elevated administrator (UAC-filtered tokens do not count) or the service itself.
        /// </summary>
        public bool IsAdministrator => this.Identity.IsSystem || new WindowsPrincipal(this.Identity).IsInRole(WindowsBuiltInRole.Administrator);

        /// <summary>
        /// Verifies the caller can open the file with the given access and that its path is free of links.
        /// This is an early, user-friendly check; the authoritative check is opening the disk under the caller's token.
        /// </summary>
        public PathGuard GuardFile(string path, FileAccess access, bool strictPaths = false) {
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) { //impersonation tokens cannot prove access on remote servers
                this.DemandAdministrator("Using files on network shares");
                return null;
            }
            if (!this.Identity.IsSystem) {
                if ((access != FileAccess.Read) && File.Exists(path) && ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)) {
                    access = FileAccess.Read; //attach reports the read-only attribute with a clearer message
                }
                try {
                    WindowsIdentity.RunImpersonated(this.Identity.AccessToken, () => {
                        using (new FileStream(path, FileMode.Open, access, FileShare.ReadWrite | FileShare.Delete)) { }
                    });
                } catch (IOException ex) when ((ex.HResult == HResultSharingViolation) || (ex.HResult == HResultLockViolation)) {
                    //access was granted (checked before sharing); the file is just open elsewhere, e.g. already attached
                } catch (UnauthorizedAccessException ex) {
                    throw new UnauthorizedAccessException(string.Format("{0} does not have {1} access to \"{2}\".", this.Name, (access == FileAccess.Read) ? "read" : "read/write", Path.GetFileName(path)), ex);
                } catch (IOException ex) { //not found, bad impersonation level, anything else: deny
                    throw new UnauthorizedAccessException(string.Format("Access to \"{0}\" could not be verified for {1}: {2}", Path.GetFileName(path), this.Name, ex.Message), ex);
                }
            }
            return PathGuard.Open(path, directory: false, rejectHardLinks: strictPaths || !this.IsAdministrator);
        }

        /// <summary>
        /// Runs code with the caller's token (Windows checks the caller's rights on every file it opens).
        /// The service's own boot-time work runs directly.
        /// </summary>
        public T RunAsCaller<T>(Func<T> action) {
            if (this.Identity.IsSystem) { return action(); }
            return WindowsIdentity.RunImpersonated(this.Identity.AccessToken, action);
        }

        public void RunAsCaller(Action action) {
            this.RunAsCaller<object>(() => { action(); return null; });
        }

        /// <summary>
        /// Validates the (existing, empty, link-free) mount folder. Mounting runs as Local System and cannot be made
        /// race-free against folder swaps, so it is reserved for administrators.
        /// </summary>
        public PathGuard GuardMountFolder(string path) {
            this.DemandAdministrator("Mounting into a folder");
            AttachHelper.ValidateMountFolder(path);
            var guard = PathGuard.Open(path, directory: true, rejectHardLinks: false);
            try {
                if (!this.Identity.IsSystem) {
                    var allowed = false;
                    WindowsIdentity.RunImpersonated(this.Identity.AccessToken, () => {
                        using (var handle = NativeMethods.CreateFile(guard.FinalPath, NativeMethods.FILE_ADD_FILE | NativeMethods.FILE_ADD_SUBDIRECTORY, 7, IntPtr.Zero, 3, NativeMethods.FILE_FLAG_BACKUP_SEMANTICS | NativeMethods.FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero)) {
                            allowed = !handle.IsInvalid;
                        }
                    });
                    if (!allowed) { throw new UnauthorizedAccessException(string.Format("{0} cannot write to \"{1}\".", this.Name, path)); }
                }
                return guard;
            } catch {
                guard.Dispose();
                throw;
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
            public const uint FILE_ADD_FILE = 0x2;
            public const uint FILE_ADD_SUBDIRECTORY = 0x4;
            public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
            public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

            [DllImport("advapi32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool ImpersonateNamedPipeClient(IntPtr hNamedPipe);

            [DllImport("advapi32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool RevertToSelf();
        }

    }
}
