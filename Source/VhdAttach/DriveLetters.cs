using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using VhdAttachCommon;

namespace VhdAttach {

    /// <summary>
    /// The service attaches disks as Local System, so Windows picks their drive letters in the global namespace.
    /// Mapped network drives and subst drives exist only in the user's own logon session, so a new volume can get
    /// a letter the user already uses; that letter then keeps showing the other drive. This class runs in the
    /// user's session, where both are visible.
    /// </summary>
    internal static class DriveLetters {

        /// <summary>
        /// Moves every volume of an attached disk off drive letters this user already uses.
        /// Returns one message per volume that was moved or could not be moved.
        /// </summary>
        public static IList<string> ResolveConflicts(string fileName) {
            string attachedPath;
            using (var disk = new Medo.IO.VirtualDisk(fileName)) {
                disk.Open(Medo.IO.VirtualDiskAccessMask.GetInfo | Medo.IO.VirtualDiskAccessMask.Detach); //GetAttachedPath requires Detach access
                attachedPath = disk.GetAttachedPath();
            }

            var notes = new List<string>();
            foreach (var volume in WaitForLetters(attachedPath)) {
                var conflict = GetConflict(volume);
                if (conflict == null) { continue; }

                var letter = volume.DriveLetter2;
                var free = FindFreeLetter();
                if (free == null) {
                    notes.Add(string.Format(CultureInfo.CurrentCulture, "Drive letter {0} is also used by {1}, and no other letter is free. Free a letter, then use Drive → Change drive letter.", letter, conflict));
                    continue;
                }
                var res = PipeClient.ChangeDriveLetter(volume.VolumeName, free + "\\");
                notes.Add(res.IsError
                    ? string.Format(CultureInfo.CurrentCulture, "Drive letter {0} is also used by {1}. Moving the disk to {2} failed: {3}", letter, conflict, free, res.Message)
                    : string.Format(CultureInfo.CurrentCulture, "Drive letter {0} is already used by {1}, so the disk was given {2} instead.", letter, conflict, free));
            }
            return notes;
        }

        /// <summary>
        /// Returns what this user sees at the volume's drive letter when it is not the volume itself
        /// (a network path, a subst folder, or "another drive"), or null when there is no conflict.
        /// </summary>
        public static string GetConflict(Volume volume) {
            var letter = volume.DriveLetter2; //as Windows assigned it, in the global namespace
            if (letter == null) { return null; }

            var network = GetNetworkPath(letter);
            if (network != null) { return network; } //also when not connected yet: it would fail to reconnect

            var seen = QueryTarget(letter); //this session's own drives take precedence over global ones
            var own = QueryTarget(volume.VolumeName.Substring(4).TrimEnd('\\')); //\\?\Volume{...}\ → \Device\HarddiskVolumeN
            if ((seen == null) || (own == null) || string.Equals(seen, own, StringComparison.OrdinalIgnoreCase)) { return null; }
            return seen.StartsWith(@"\??\", StringComparison.Ordinal) ? seen.Substring(4) : "another drive";
        }

        /// <summary>
        /// Returns the first drive letter from D: that this user doesn't use (e.g. "F:"), or null.
        /// </summary>
        public static string FindFreeLetter() {
            var used = GetUsedLetters();
            for (var letter = 'D'; letter <= 'Z'; letter++) {
                if (!used.Contains(letter)) { return letter + ":"; }
            }
            return null;
        }

        /// <summary>
        /// Letters in use for this user: everything visible in this session, plus remembered network drives,
        /// which reconnect at sign-in and are invisible to an elevated process.
        /// </summary>
        public static ISet<char> GetUsedLetters() {
            var used = new HashSet<char>();
            for (var letter = 'A'; letter <= 'Z'; letter++) {
                var drive = letter + ":";
                if ((QueryTarget(drive) != null) || (GetNetworkPath(drive) != null)) { used.Add(letter); }
            }
            return used;
        }


        private static IList<Volume> WaitForLetters(string attachedPath) {
            if (attachedPath == null) { return Array.Empty<Volume>(); }
            var volumes = Volume.GetVolumesOnPhysicalDrive(attachedPath);
            for (var i = 0; (i < 12) && !volumes.Any(v => v.DriveLetter2 != null); i++) { //Windows assigns letters shortly after the disk arrives
                Thread.Sleep(250);
                volumes = Volume.GetVolumesOnPhysicalDrive(attachedPath);
            }
            return volumes;
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
