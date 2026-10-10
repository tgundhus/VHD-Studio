using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.Win32;
using VhdAttachCommon;

namespace VhdAttachService {

    /// <summary>
    /// Gives each volume of a virtual disk the drive letter it had last time, or a new unused letter when that one
    /// is taken. Windows forgets a volume's letter as soon as another drive gets it, so the service keeps its own
    /// record per disk file and partition offset (HKLM, written only by the service).
    /// </summary>
    internal static class DriveLetterMemory {

        private const string SubkeyPath = Branding.SettingsSubkeyPath + @"\DriveLetters";

        /// <summary>
        /// Call right after attaching a disk that should get drive letters.
        /// </summary>
        /// <param name="fileName">Backing file of the disk.</param>
        /// <param name="physicalPath">Attached disk, e.g. \\.\PhysicalDrive3.</param>
        /// <param name="avoid">Letters the user uses for network or subst drives; they exist only in the user's logon session, which the service can't see.</param>
        /// <returns>
        /// What the user should be told, as "kind:letter:letter" entries separated by ';':
        /// "inuse:Z:G" moved off a letter the user uses, "taken:E:F" the last letter is used by another drive (this time only),
        /// "nofree:Z:-" no free letter to move to.
        /// </returns>
        public static string Apply(string fileName, string physicalPath, ISet<char> avoid) {
            if ((physicalPath == null) || !physicalPath.StartsWith(@"\\.\PhysicalDrive", StringComparison.OrdinalIgnoreCase)) { return ""; } //ISO images: Windows picks

            var remembered = Load(fileName);
            var notices = new List<string>();
            foreach (var volume in WaitForLetters(physicalPath)) {
                var offset = volume.PhysicalDriveExtentOffset;
                if (offset == null) { continue; }
                var hadLetter = remembered.TryGetValue(offset.Value, out var last);
                var known = hadLetter && !avoid.Contains(last); //a letter the user now uses for another drive is given up
                char? current = volume.DriveLetter2?[0];
                if ((current == null) && !hadLetter) { continue; } //never had a letter (e.g. a recovery partition) or the user removed it
                try {
                    var taken = false;
                    if (known && (current != last)) {
                        if (Volume.IsLetterInUse(last)) {
                            taken = true; //another drive has it: a new letter this time, the last one again next time
                        } else {
                            volume.ChangeLetter(last + ":");
                            current = last;
                        }
                    }
                    if ((current == null) || avoid.Contains(current.Value)) { //Windows gave none, or one the user already uses
                        var used = current ?? last;
                        var free = FindFreeLetter(avoid);
                        if (free == null) {
                            notices.Add("nofree:" + used + ":-");
                        } else {
                            volume.ChangeLetter(free.Value + ":");
                            if (!taken) { notices.Add("inuse:" + used + ":" + free.Value); }
                            current = free.Value;
                        }
                    }
                    if (taken && (current != null)) { notices.Add("taken:" + last + ":" + current.Value); }
                    if (!known && (current != null)) { remembered[offset.Value] = current.Value; }
                } catch (Exception ex) when ((ex is InvalidOperationException) || (ex is System.ComponentModel.Win32Exception)) {
                    Trace.TraceWarning("Drive letter of " + volume.VolumeName + " was not changed: " + ex.Message);
                }
            }
            Save(fileName, remembered);
            return string.Join(";", notices);
        }

        /// <summary>
        /// Records the volume's letter after the user changed it, or forgets it when the volume has none now.
        /// </summary>
        public static void Remember(string fileName, Volume volume) {
            var offset = volume.PhysicalDriveExtentOffset;
            if (offset == null) { return; }
            var remembered = Load(fileName);
            var letter = volume.DriveLetter2;
            if (letter == null) { remembered.Remove(offset.Value); } else { remembered[offset.Value] = letter[0]; }
            Save(fileName, remembered);
        }

        internal static void Forget(string fileName) {
            using (var key = Registry.LocalMachine.OpenSubKey(SubkeyPath, true)) {
                key?.DeleteValue(Key(fileName), false);
            }
        }

        /// <summary>
        /// First letter from D: that is free and not in <paramref name="avoid"/>, or null.
        /// </summary>
        public static char? FindFreeLetter(ISet<char> avoid) {
            for (var letter = 'D'; letter <= 'Z'; letter++) {
                if (!avoid.Contains(letter) && !Volume.IsLetterInUse(letter)) { return letter; }
            }
            return null;
        }


        private static IList<Volume> WaitForLetters(string physicalPath) {
            var volumes = Volume.GetVolumesOnPhysicalDrive(physicalPath);
            for (var i = 0; (i < 12) && !volumes.Any(v => v.DriveLetter2 != null); i++) { //Windows assigns letters shortly after the disk arrives
                Thread.Sleep(250);
                volumes = Volume.GetVolumesOnPhysicalDrive(physicalPath);
            }
            return volumes;
        }

        /// <summary>
        /// Registry value name for a file: the plain long path (\\?\ prefixes removed); value names ignore case.
        /// </summary>
        private static string Key(string fileName) {
            if (fileName.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) {
                fileName = @"\\" + fileName.Substring(8);
            } else if (fileName.StartsWith(@"\\?\", StringComparison.Ordinal)) {
                fileName = fileName.Substring(4);
            }
            return PathGuard.GetLongPath(fileName);
        }

        /// <summary>
        /// Partition offset → letter, stored as "1048576=E;135266304=F".
        /// </summary>
        private static Dictionary<long, char> Load(string fileName) {
            var letters = new Dictionary<long, char>();
            using (var key = Registry.LocalMachine.OpenSubKey(SubkeyPath, false)) {
                if (key?.GetValue(Key(fileName)) is string text) {
                    foreach (var entry in text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)) {
                        var parts = entry.Split('=');
                        if ((parts.Length == 2) && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset)
                            && (parts[1].Length == 1) && (parts[1][0] >= 'A') && (parts[1][0] <= 'Z')) {
                            letters[offset] = parts[1][0];
                        }
                    }
                }
            }
            return letters;
        }

        private static void Save(string fileName, IDictionary<long, char> letters) {
            using (var key = Registry.LocalMachine.CreateSubKey(SubkeyPath, RegistryKeyPermissionCheck.ReadWriteSubTree)) {
                if (letters.Count == 0) {
                    key.DeleteValue(Key(fileName), false);
                } else {
                    key.SetValue(Key(fileName), string.Join(";", letters.OrderBy(p => p.Key).Select(p => p.Key.ToString(CultureInfo.InvariantCulture) + "=" + p.Value)), RegistryValueKind.String);
                }
            }
        }

    }
}
