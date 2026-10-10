using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VhdAttach;
using VhdAttachCommon;
using VhdAttachService;

namespace VhdAttachTest {

    /// <summary>
    /// A drive letter can be in use only in the user's own logon session (mapped network drive, subst), where the
    /// service can't see it. A session-local DOS device on a free letter simulates that.
    /// </summary>
    [TestClass()]
    public class DriveLetterSessionTest {

        [TestMethod()]
        public void SubstDrive_IsSessionLetter() {
            var letter = DriveLetterMemory.FindFreeLetter(new HashSet<char>());
            Assert.IsNotNull(letter, "No free drive letter on this machine.");
            var drive = letter.Value + ":";
            var target = Environment.SystemDirectory;
            Assert.IsFalse(DriveLetters.GetSessionLetters().Contains(letter.Value));

            Assert.IsTrue(NativeMethods.DefineDosDevice(0, drive, target), "DefineDosDevice failed.");
            try {
                Assert.IsTrue(DriveLetters.GetSessionLetters().Contains(letter.Value));
                Assert.AreEqual(target, DriveLetters.Describe(letter.Value));
                Assert.IsTrue(DriveLetters.GetUsedLetters().Contains(letter.Value));
            } finally {
                NativeMethods.DefineDosDevice(NativeMethods.DDD_REMOVE_DEFINITION | NativeMethods.DDD_EXACT_MATCH_ON_REMOVE, drive, target);
            }
            Assert.IsFalse(DriveLetters.GetSessionLetters().Contains(letter.Value));
        }

        [TestMethod()]
        public void FormatNotices_ExplainsEachCase() {
            var messages = DriveLetters.FormatNotices("inuse:Q:G;taken:E:F;nofree:Q:-;bogus");
            Assert.AreEqual(3, messages.Count);
            StringAssert.Contains(messages[0], "was given G:");
            StringAssert.Contains(messages[1], "normally uses E:");
            StringAssert.Contains(messages[2], "no other letter is free");
            Assert.AreEqual(0, DriveLetters.FormatNotices(null).Count);
        }


        private static class NativeMethods {

            public const int DDD_REMOVE_DEFINITION = 0x00000002;
            public const int DDD_EXACT_MATCH_ON_REMOVE = 0x00000004;

            [DllImport("kernel32.dll", EntryPoint = "DefineDosDeviceW", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool DefineDosDevice(int dwFlags, string lpDeviceName, string lpTargetPath);

        }

    }


    /// <summary>
    /// Attaches like the service does and checks that a disk gets its last drive letter back, or a new unused one.
    /// </summary>
    [TestClass()]
    [TestCategory("Elevated")]
    public class DriveLetterTest {

        [TestInitialize]
        public void Init() => ScratchDisk.RequireElevation();

        [TestMethod()]
        public void Attach_ReturnsToLastLetter_AfterAnotherDriveHadIt() {
            using (var a = ScratchDisk.Create())
            using (var b = ScratchDisk.Create()) {
                try {
                    var last = GiveLetter(a);

                    Attach(b);
                    VolumeOf(b).ChangeLetter(last + ":"); //another drive gets the letter while a is detached; Windows forgets it for a
                    var notice = Attach(a);
                    var thisTime = LetterOf(a);
                    Assert.AreNotEqual(last, thisTime);
                    Assert.AreEqual("taken:" + last + ":" + thisTime, notice);
                    Detach(a);
                    Detach(b);

                    Assert.AreEqual("", Attach(a));
                    Assert.AreEqual(last, LetterOf(a), "The disk must get its last letter back once it is free.");
                } finally {
                    Cleanup(a);
                    Cleanup(b);
                }
            }
        }

        [TestMethod()]
        public void Attach_MovesOffLetterTheUserUses() {
            using (var a = ScratchDisk.Create()) {
                try {
                    var last = GiveLetter(a);

                    var notice = Attach(a, last); //the user now has a network or subst drive on that letter
                    var now = LetterOf(a);
                    Assert.AreNotEqual(last, now);
                    Assert.AreEqual("inuse:" + last + ":" + now, notice);
                    Detach(a);

                    Assert.AreEqual("", Attach(a));
                    Assert.AreEqual(now, LetterOf(a), "The new letter is the disk's letter from now on.");
                } finally {
                    Cleanup(a);
                }
            }
        }

        [TestMethod()]
        public void ChangeLetter_ToUsedLetter_KeepsCurrentLetter() {
            using (var disk = ScratchDisk.Create()) {
                disk.Attach(readOnly: false);
                var volume = new Volume(disk.WaitForVolume());
                var letter = DriveLetterMemory.FindFreeLetter(new HashSet<char>());
                Assert.IsNotNull(letter, "No free drive letter on this machine.");
                volume.ChangeLetter(letter.Value + ":");

                var systemDrive = Environment.SystemDirectory.Substring(0, 2); //always in use
                Assert.ThrowsExactly<InvalidOperationException>(() => volume.ChangeLetter(systemDrive));
                Assert.AreEqual(letter.Value + ":", volume.DriveLetter2, "A refused change must not remove the current letter.");
            }
        }


        /// <summary>
        /// Attaches, gives the volume a free letter the way Drive → Change drive letter does, and detaches.
        /// </summary>
        private static char GiveLetter(ScratchDisk disk) {
            Attach(disk);
            var volume = VolumeOf(disk);
            var letter = DriveLetterMemory.FindFreeLetter(new HashSet<char>());
            Assert.IsNotNull(letter, "No free drive letter on this machine.");
            volume.ChangeLetter(letter.Value + ":");
            DriveLetterMemory.Remember(disk.FileName, volume);
            Detach(disk);
            return letter.Value;
        }

        private static string Attach(ScratchDisk disk, params char[] avoid) {
            return AttachHelper.Attach(new FileWithOptions(disk.FileName), PipeCaller.ForService(), avoidLetters: new HashSet<char>(avoid));
        }

        private static void Detach(ScratchDisk disk) {
            AttachHelper.Detach(disk.FileName, false, PipeCaller.ForService());
        }

        private static void Cleanup(ScratchDisk disk) {
            try { Detach(disk); } catch (Exception) { } //already detached
            DriveLetterMemory.Forget(disk.FileName);
        }

        private static Volume VolumeOf(ScratchDisk disk) {
            string physicalPath;
            using (var vd = new Medo.IO.VirtualDisk(disk.FileName)) {
                vd.Open(Medo.IO.VirtualDiskAccessMask.GetInfo | Medo.IO.VirtualDiskAccessMask.Detach);
                physicalPath = vd.GetAttachedPath();
            }
            for (var i = 0; i < 60; i++) {
                var volume = Volume.GetVolumesOnPhysicalDrive(physicalPath).FirstOrDefault(v => Directory.Exists(v.VolumeName));
                if (volume != null) { return volume; }
                Thread.Sleep(250);
            }
            throw new AssertFailedException("Volume did not appear.");
        }

        private static char LetterOf(ScratchDisk disk) {
            var volume = VolumeOf(disk);
            for (var i = 0; i < 20; i++) {
                var letter = volume.DriveLetter2;
                if (letter != null) { return letter[0]; }
                Thread.Sleep(250);
            }
            throw new AssertFailedException("The volume got no drive letter.");
        }

    }

}
