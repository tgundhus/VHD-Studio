using System;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace VhdAttachCommon {

    /// <summary>
    /// Append-only record of every change VHD Studio makes to disks and images
    /// (%ProgramData%\xGND Software\VHD Studio\Logs\audit-yyyy-MM.log, one JSON object per line).
    /// Logging never blocks or fails the operation itself.
    /// </summary>
    internal static class AuditLog {

        private static readonly object SyncRoot = new object();

        public static string Directory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "xGND Software", "VHD Studio", "Logs");

        public static void Started(string operation, string target, string details = null, string user = null) {
            Write("started", operation, target, details, user);
        }

        public static void Succeeded(string operation, string target, string details = null, string user = null) {
            Write("succeeded", operation, target, details, user);
        }

        public static void Failed(string operation, string target, Exception exception, string user = null) {
            Write("failed", operation, target, exception?.GetType().Name + ": " + exception?.Message, user);
        }

        private static void Write(string result, string operation, string target, string details, string user) {
            try {
                var line = new StringBuilder();
                line.Append(JsonSerializer.Serialize(new {
                    time = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
                    result,
                    operation,
                    target,
                    details,
                    user = user ?? CurrentUser(),
                    machine = Environment.MachineName,
                    process = Path.GetFileName(Environment.ProcessPath),
                }));
                var file = Path.Combine(Directory, "audit-" + DateTime.Now.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".log");
                lock (SyncRoot) {
                    if (!EnsureSecureDirectory()) { return; }
                    using (var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) {
                        writer.WriteLine(line.ToString());
                    }
                }
            } catch (IOException) {
            } catch (UnauthorizedAccessException) { } //non-elevated UI process; the service logs its own actions
        }

        /// <summary>
        /// Log folders are created with an Administrators/SYSTEM-only ACL. A folder that is a link, or that was
        /// pre-created by someone else (owner not Administrators/SYSTEM), is never written to: a privileged writer
        /// must not be redirected into files chosen by another user.
        /// </summary>
        private static bool EnsureSecureDirectory() {
            var product = Path.GetDirectoryName(Directory);
            var vendor = Path.GetDirectoryName(product);
            foreach (var path in new[] { vendor, product, Directory }) {
                var info = new DirectoryInfo(path);
                if (!info.Exists) {
                    var security = new System.Security.AccessControl.DirectorySecurity();
                    security.SetAccessRuleProtection(true, false);
                    var inherit = System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit;
                    security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), System.Security.AccessControl.FileSystemRights.FullControl, inherit, System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
                    security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), System.Security.AccessControl.FileSystemRights.FullControl, inherit, System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
                    security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), System.Security.AccessControl.FileSystemRights.ReadAndExecute, inherit, System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
                    security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
                    info.Create(security);
                    info.Refresh();
                }
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) { return false; }
                var owner = info.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                if ((owner == null) || !(owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))) { return false; }
            }
            return true;
        }

        private static string CurrentUser() {
            try {
                using (var identity = WindowsIdentity.GetCurrent()) { return identity.Name; }
            } catch (System.Security.SecurityException) {
                return Environment.UserName;
            }
        }

    }
}
