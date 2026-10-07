using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VhdAttach {
    internal static class PathHelper {

        /// <summary>
        /// Converts a path into a form the service (running as Local System) can open.
        /// Mapped network drives exist only in the user's logon session, so they are rewritten to UNC paths.
        /// </summary>
        public static string ToServicePath(string path) {
            if (string.IsNullOrEmpty(path)) { return path; }
            var fullPath = Path.GetFullPath(path);
            if ((fullPath.Length >= 3) && (fullPath[1] == ':') && (fullPath[2] == '\\')) {
                var drive = fullPath.Substring(0, 2);
                try {
                    if (new DriveInfo(drive).DriveType == DriveType.Network) {
                        var length = 1024;
                        var remote = new StringBuilder(length);
                        if (NativeMethods.WNetGetConnection(drive, remote, ref length) == 0) {
                            return remote.ToString().TrimEnd('\\') + fullPath.Substring(2);
                        }
                    }
                } catch (ArgumentException) { }
            }
            return fullPath;
        }


        private static class NativeMethods {
            [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
            public static extern int WNetGetConnection(string lpLocalName, StringBuilder lpRemoteName, ref int lpnLength);
        }

    }
}
