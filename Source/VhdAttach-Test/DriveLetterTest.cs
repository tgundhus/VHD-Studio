using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VhdAttach;
using VhdAttachCommon;

namespace VhdAttachTest {

    /// <summary>
    /// Windows assigns drive letters in the global namespace, but a letter can also be in use in the user's own
    /// logon session (mapped network drive, subst). A session-local DOS device on the same letter simulates that.
    /// </summary>
    [TestClass()]
    [TestCategory("Elevated")]
    public class DriveLetterTest {

        [TestInitialize]
        public void Init() => ScratchDisk.RequireElevation();

        [TestMethod()]
        public void SessionDriveOnSameLetter_IsReported() {
            using (var disk = ScratchDisk.Create()) {
                disk.Attach(readOnly: false); //no letter yet
                var volume = new Volume(disk.WaitForVolume());
                var letter = DriveLetters.FindFreeLetter();
                Assert.IsNotNull(letter, "No free drive letter on this machine.");
                volume.ChangeLetter(letter);
                Assert.AreEqual(letter, volume.DriveLetter2);
                Assert.IsNull(DriveLetters.GetConflict(volume));

                var target = Environment.SystemDirectory;
                Assert.IsTrue(NativeMethods.DefineDosDevice(0, letter, target), "DefineDosDevice failed.");
                try {
                    Assert.AreEqual(target, DriveLetters.GetConflict(volume), "A session drive on the volume's letter must be reported.");
                } finally {
                    NativeMethods.DefineDosDevice(NativeMethods.DDD_REMOVE_DEFINITION | NativeMethods.DDD_EXACT_MATCH_ON_REMOVE, letter, target);
                }
                Assert.IsNull(DriveLetters.GetConflict(volume));
            }
        }

        [TestMethod()]
        public void ChangeLetter_ToUsedLetter_KeepsCurrentLetter() {
            using (var disk = ScratchDisk.Create()) {
                disk.Attach(readOnly: false);
                var volume = new Volume(disk.WaitForVolume());
                var letter = DriveLetters.FindFreeLetter();
                Assert.IsNotNull(letter, "No free drive letter on this machine.");
                volume.ChangeLetter(letter);

                var systemDrive = Environment.SystemDirectory.Substring(0, 2); //always in use
                Assert.ThrowsExactly<InvalidOperationException>(() => volume.ChangeLetter(systemDrive));
                Assert.AreEqual(letter, volume.DriveLetter2, "A refused change must not remove the current letter.");
            }
        }


        private static class NativeMethods {

            public const int DDD_REMOVE_DEFINITION = 0x00000002;
            public const int DDD_EXACT_MATCH_ON_REMOVE = 0x00000004;

            [DllImport("kernel32.dll", EntryPoint = "DefineDosDeviceW", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool DefineDosDevice(int dwFlags, string lpDeviceName, string lpTargetPath);

        }

    }
}
