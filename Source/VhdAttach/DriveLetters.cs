using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace VhdAttach {

    /// <summary>
    /// Drive letters as this user sees them. Mapped network drives and subst drives exist only in the user's own
    /// logon session, so the service (Local System) can't see them; the UI tells it which letters to keep disks off.
    /// </summary>
    internal static class DriveLetters {

        /// <summary>
        /// Letters this user uses for network drives (connected or remembered) and subst drives, e.g. "HSZ".
        /// </summary>
        public static string GetSessionLetters() {
            var letters = new StringBuilder();
            for (var letter = 'A'; letter <= 'Z'; letter++) {
                if (Describe(letter) != null) { letters.Append(letter); }
            }
            return letters.ToString();
        }

        /// <summary>
        /// Letters in use for this user: everything visible in this session, plus remembered network drives,
        /// which reconnect at sign-in and are invisible to an elevated process.
        /// </summary>
        public static ISet<char> GetUsedLetters() {
            var used = new HashSet<char>();
            for (var letter = 'A'; letter <= 'Z'; letter++) {
                if ((QueryTarget(letter + ":") != null) || (GetNetworkPath(letter + ":") != null)) { used.Add(letter); }
            }
            return used;
        }

        /// <summary>
        /// The network path or subst folder this user has on the letter, or null.
        /// </summary>
        public static string Describe(char letter) {
            var drive = letter + ":";
            var network = GetNetworkPath(drive);
            if (network != null) { return network; }
            var target = QueryTarget(drive);
            return ((target != null) && target.StartsWith(@"\??\", StringComparison.Ordinal)) ? target.Substring(4) : null; //subst
        }

        /// <summary>
        /// Turns the service's drive letter notices of an attach (e.g. "inuse:Z:G;taken:E:F") into messages.
        /// </summary>
        public static IList<string> FormatNotices(string notices) {
            var messages = new List<string>();
            foreach (var entry in (notices ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)) {
                var parts = entry.Split(':');
                if ((parts.Length != 3) || (parts[1].Length != 1)) { continue; }
                var from = parts[1] + ":";
                var to = parts[2] + ":";
                var owner = Describe(parts[1][0]) ?? "another drive";
                switch (parts[0]) {
                    case "inuse": messages.Add(string.Format(CultureInfo.CurrentCulture, "Drive letter {0} is already used by {1}, so the disk was given {2} instead. It keeps {2} from now on.", from, owner, to)); break;
                    case "taken": messages.Add(string.Format(CultureInfo.CurrentCulture, "The disk normally uses {0}, but another drive has that letter right now, so the disk was given {1} this time.", from, to)); break;
                    case "nofree": messages.Add(string.Format(CultureInfo.CurrentCulture, "Drive letter {0} is also used by {1}, and no other letter is free. Free a letter, then use Drive → Change drive letter.", from, owner)); break;
                }
            }
            return messages;
        }


        private static string GetNetworkPath(string drive) {
            var buffer = new StringBuilder(1024);
            var length = buffer.Capacity;
            var res = NativeMethods.WNetGetConnection(drive, buffer, ref length);
            if ((res == NativeMethods.NO_ERROR) || (res == NativeMethods.ERROR_CONNECTION_UNAVAIL)) { return buffer.ToString(); }

            using (var key = Registry.CurrentUser.OpenSubKey(@"Network\" + drive.Substring(0, 1))) { //remembered mapping
                return (key?.GetValue("RemotePath") as string) ?? ((key != null) ? "a network drive" : null);
            }
        }

        private static string QueryTarget(string deviceName) {
            var buffer = new StringBuilder(1024);
            return (NativeMethods.QueryDosDevice(deviceName, buffer, buffer.Capacity) > 0) ? buffer.ToString() : null; //first entry only
        }


        private static class NativeMethods {

            public const int NO_ERROR = 0;
            public const int ERROR_CONNECTION_UNAVAIL = 1201;

            [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern int QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, int ucchMax);

            [DllImport("mpr.dll", EntryPoint = "WNetGetConnectionW", CharSet = CharSet.Unicode)]
            public static extern int WNetGetConnection(string lpLocalName, StringBuilder lpRemoteName, ref int lpnLength);

        }

    }
}
