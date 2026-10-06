using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Medo.Net;
using VhdAttachCommon;

namespace VhdAttachService {
    internal static class PipeServer {

        public static Medo.IO.NamedPipe Pipe = new Medo.IO.NamedPipe(Branding.PipeName);

        public static void Start() {
            Pipe.CreateWithFullAccess();
        }

        public static TinyPacket Receive() {
            Pipe.Connect();

            while (Pipe.HasBytesToRead == false) { Thread.Sleep(100); }
            var buffer = Pipe.ReadAvailable();

            var packet = TinyPacket.Parse(buffer);
            if (packet != null) {
                if (packet.Product != Branding.PacketProduct) { return null; }
                PipeCaller caller = null;
                try {
                    caller = PipeCaller.FromPipe(Pipe.GetHandle());
                } catch (Win32Exception ex) {
                    return GetResponse(packet, new InvalidOperationException("Cannot identify the calling user.", ex));
                }
                using (caller)
                try {
                    switch (packet.Operation) {
                        case "Attach":
                            ReceivedAttach(packet, caller);
                            return GetResponse(packet);

                        case "Detach":
                            ReceivedDetach(packet, caller);
                            return GetResponse(packet);

                        case "DetachDrive":
                            ReceivedDetachDrive(packet, caller);
                            return GetResponse(packet);

                        case "WriteContextMenuVhdSettings":
                            ReceivedWriteContextMenuVhdSettings(packet);
                            return GetResponse(packet);

                        case "WriteContextMenuIsoSettings":
                            ReceivedWriteContextMenuIsoSettings(packet);
                            return GetResponse(packet);

                        case "WriteAutoAttachSettings":
                            ReceivedWriteAutoAttachSettings(packet, caller);
                            return GetResponse(packet);

                        case "RegisterExtensionVhd":
                            ReceivedRegisterExtensionVhd();
                            return GetResponse(packet);

                        case "RegisterExtensionIso":
                            ReceivedRegisterExtensionIso();
                            return GetResponse(packet);

                        case "ChangeDriveLetter":
                            ReceivedChangeDriveLetter(packet, caller);
                            return GetResponse(packet);

                        default: throw new InvalidOperationException("Unknown command.");
                    }
                } catch (InvalidOperationException ex) {
                    return GetResponse(packet, ex);
                }
            } else {
                return null;
            }
        }



        private static void ReceivedAttach(TinyPacket packet, PipeCaller caller) {
            try {
                var file = new FileWithOptions(packet["Path"]) {
                    ReadOnly = packet["MountReadOnly"].Equals("True", StringComparison.OrdinalIgnoreCase),
                    NoDriveLetter = string.Equals(packet["NoDriveLetter"], "True", StringComparison.OrdinalIgnoreCase),
                    MountFolder = string.IsNullOrEmpty(packet["MountFolder"]) ? null : packet["MountFolder"],
                };
                var shouldInitialize = packet["InitializeDisk"].Equals("True", StringComparison.OrdinalIgnoreCase);
                caller.DemandFileAccess(file.FileName, (file.ReadOnly && !shouldInitialize) ? FileAccess.Read : FileAccess.ReadWrite);
                AttachHelper.Attach(file, shouldInitialize);
            } catch (Exception ex) {
                throw new InvalidOperationException(string.Format("Virtual disk file \"{0}\" cannot be attached.", (new FileInfo(packet["Path"])).Name), ex);
            }
        }

        private static void ReceivedDetach(TinyPacket packet, PipeCaller caller) {
            try {
                var path = packet["Path"];
                caller.DemandFileAccess(path, FileAccess.Read);
                using (var disk = new Medo.IO.VirtualDisk(path)) {
                    disk.Open(Medo.IO.VirtualDiskAccessMask.Detach);
                    disk.Detach();
                }
            } catch (Exception ex) {
                throw new InvalidOperationException(string.Format("Virtual disk file \"{0}\" cannot be detached.", (new FileInfo(packet["Path"])).Name), ex);
            }
        }

        private static void ReceivedDetachDrive(TinyPacket packet, PipeCaller caller) {
            try {
                DetachDrive(packet["Path"], caller);
            } catch (Exception ex) {
                throw new InvalidOperationException(string.Format("Drive \"{0}\" cannot be detached.", packet["Path"]), ex);
            }
        }

        private static void ReceivedWriteContextMenuVhdSettings(TinyPacket packet) {
            try {
                ServiceSettings.ContextMenuVhdOpen = bool.Parse(packet["Open"]);
                ServiceSettings.ContextMenuVhdAttach = bool.Parse(packet["Attach"]);
                ServiceSettings.ContextMenuVhdAttachReadOnly = bool.Parse(packet["AttachReadOnly"]);
                ServiceSettings.ContextMenuVhdDetach = bool.Parse(packet["Detach"]);
                ServiceSettings.ContextMenuVhdDetachDrive = bool.Parse(packet["DetachDrive"]);
            } catch (Exception ex) {
                Medo.Diagnostics.ErrorReport.SaveToTemp(ex);
                throw new InvalidOperationException("Settings cannot be written.", ex);
            }
        }

        private static void ReceivedWriteContextMenuIsoSettings(TinyPacket packet) {
            try {
                ServiceSettings.ContextMenuIsoOpen = bool.Parse(packet["Open"]);
                ServiceSettings.ContextMenuIsoAttachReadOnly = bool.Parse(packet["AttachReadOnly"]);
                ServiceSettings.ContextMenuIsoDetach = bool.Parse(packet["Detach"]);
            } catch (Exception ex) {
                Medo.Diagnostics.ErrorReport.SaveToTemp(ex);
                throw new InvalidOperationException("Settings cannot be written.", ex);
            }
        }

        private static void ReceivedWriteAutoAttachSettings(TinyPacket packet, PipeCaller caller) {
            try {
                var oldList = ServiceSettings.AutoAttachVhdList;
                var newList = GetFwoArray(packet["AutoAttachList"]);
                foreach (var fwo in newList) { //anything new or changed must be accessible to the caller
                    if (!Array.Exists(oldList, x => string.Equals(x.ToString(), fwo.ToString(), StringComparison.OrdinalIgnoreCase))) {
                        caller.DemandFileAccess(fwo.FileName, fwo.ReadOnly ? FileAccess.Read : FileAccess.ReadWrite);
                    }
                }
                foreach (var fwo in oldList) { //cannot remove other users' entries for files that still exist
                    if (!Array.Exists(newList, x => string.Equals(x.FileName, fwo.FileName, StringComparison.OrdinalIgnoreCase)) && File.Exists(fwo.FileName)) {
                        caller.DemandFileAccess(fwo.FileName, FileAccess.Read);
                    }
                }
                ServiceSettings.AutoAttachVhdList = newList;
            } catch (UnauthorizedAccessException ex) {
                throw new InvalidOperationException("Auto-attach list cannot be written.", ex);
            } catch (Exception ex) {
                Medo.Diagnostics.ErrorReport.SaveToTemp(ex);
                throw new InvalidOperationException("Auto-attach list cannot be written.", ex);
            }
        }

        private static void ReceivedRegisterExtensionVhd() {
            try {
                ServiceSettings.ContextMenuVhd = true;
            } catch (Exception ex) {
                Medo.Diagnostics.ErrorReport.SaveToTemp(ex);
                throw new InvalidOperationException("Settings cannot be written.", ex);
            }
        }

        private static void ReceivedRegisterExtensionIso() {
            try {
                ServiceSettings.ContextMenuIso = true;
            } catch (Exception ex) {
                Medo.Diagnostics.ErrorReport.SaveToTemp(ex);
                throw new InvalidOperationException("Settings cannot be written.", ex);
            }
        }

        private static void ReceivedChangeDriveLetter(TinyPacket packet, PipeCaller caller) {
            try {
                var volume = new Volume(packet["VolumeName"]);
                if (!caller.IsAdministrator) { //non-admins may only re-letter volumes of virtual disks they can access
                    var backingFile = GetVirtualDiskFile(volume.PhysicalDriveNumber);
                    if (backingFile == null) { throw new UnauthorizedAccessException("Only volumes on virtual disks can be changed without administrator rights."); }
                    caller.DemandFileAccess(backingFile, FileAccess.Read);
                }
                var newDriveLetter = packet["NewDriveLetter"];
                if (string.IsNullOrEmpty(newDriveLetter)) {
                    volume.RemoveLetter();
                } else {
                    volume.ChangeLetter(newDriveLetter);
                }
            } catch (Exception ex) {
                Medo.Diagnostics.ErrorReport.SaveToTemp(ex);
                throw new InvalidOperationException("Cannot change drive letter.", ex);
            }
        }


        private static FileWithOptions[] GetFwoArray(string lines) {
            var files = new List<FileWithOptions>();
            foreach (var line in lines.Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries)) {
                files.Add(new FileWithOptions(line));
            }
            return files.ToArray();
        }

        public static void Reply(TinyPacket response) {
            var buffer = response.GetBytes();
            Pipe.Write(buffer);
            Pipe.Flush();
            Pipe.Disconnect();
        }

        public static void Stop() {
            Pipe.Close();
        }


        public static TinyPacket GetResponse(TinyPacket packet) {
            var data = new Dictionary<string, string>();
            data.Add("IsError", false.ToString(CultureInfo.InvariantCulture));
            data.Add("Message", "");
            return new TinyPacket(packet.Product, packet.Operation, data);
        }

        public static TinyPacket GetResponse(TinyPacket packet, Exception ex) {
            var data = new Dictionary<string, string>();
            data.Add("IsError", true.ToString(CultureInfo.InvariantCulture));
            if (ex.InnerException != null) {
                data.Add("Message", ex.Message + "\r\n" + ex.InnerException.Message);
            } else {
                data.Add("Message", ex.Message);
            }
            return new TinyPacket(packet.Product, packet.Operation, data);
        }



        /// <summary>
        /// Returns backing file of a virtual disk or null for physical disks.
        /// </summary>
        private static string GetVirtualDiskFile(int? diskNumber) {
            if (diskNumber == null) { return null; }
            var query = "SELECT BusType, Location FROM MSFT_Disk WHERE Number = " + diskNumber.Value.ToString(CultureInfo.InvariantCulture);
            using (var searcher = new System.Management.ManagementObjectSearcher(@"\\.\root\Microsoft\Windows\Storage", query)) {
                foreach (System.Management.ManagementObject disk in searcher.Get()) {
                    using (disk) {
                        if (Convert.ToInt32(disk["BusType"], CultureInfo.InvariantCulture) == 15) { return disk["Location"] as string; } //15 = file backed virtual
                    }
                }
            }
            return null;
        }

        private static void DetachDrive(string path, PipeCaller caller) {
            var device = DeviceFromPath.GetDevice(path);

            #region VDS COM

            FileInfo vhdFile = null;

            VdsServiceLoader loaderClass = new VdsServiceLoader();
            IVdsServiceLoader loader = (IVdsServiceLoader)loaderClass;

            IVdsService service;
            loader.LoadService(null, out service);

            service.WaitForServiceReady();

            IEnumVdsObject providerEnum;
            service.QueryProviders(VDS_QUERY_PROVIDER_FLAG.VDS_QUERY_VIRTUALDISK_PROVIDERS, out providerEnum);

            while (true) {
                uint fetchedProvider;
                object unknownProvider;
                providerEnum.Next(1, out unknownProvider, out fetchedProvider);

                if (fetchedProvider == 0) break;
                IVdsVdProvider provider = (IVdsVdProvider)unknownProvider;
                Console.WriteLine("Got VD Provider");

                IEnumVdsObject diskEnum;
                provider.QueryVDisks(out diskEnum);

                while (true) {
                    uint fetchedDisk;
                    object unknownDisk;
                    diskEnum.Next(1, out unknownDisk, out fetchedDisk);
                    if (fetchedDisk == 0) break;
                    IVdsVDisk vDisk = (IVdsVDisk)unknownDisk;

                    VDS_VDISK_PROPERTIES vdiskProperties;
                    vDisk.GetProperties(out vdiskProperties);

                    try {
                        IVdsDisk disk;
                        provider.GetDiskFromVDisk(vDisk, out disk);

                        VDS_DISK_PROP diskProperties;
                        disk.GetProperties(out diskProperties);

                        if (diskProperties.pwszName.Equals(device, StringComparison.OrdinalIgnoreCase)) {
                            vhdFile = new FileInfo(vdiskProperties.pPath);
                            break;
                        } else {
                            Trace.TraceError(diskProperties.pwszName + " = " + vdiskProperties.pPath);
                        }
                        Console.WriteLine("-> Disk Name=" + diskProperties.pwszName);
                        Console.WriteLine("-> Disk Friendly=" + diskProperties.pwszFriendlyName);
                    } catch (COMException) { }
                }
                if (vhdFile != null) { break; }
            }

            #endregion

            if (vhdFile != null) {
                caller.DemandFileAccess(vhdFile.FullName, FileAccess.Read);
                using (var disk = new Medo.IO.VirtualDisk(vhdFile.FullName)) {
                    disk.Open(Medo.IO.VirtualDiskAccessMask.Detach);
                    disk.Detach();
                }
            } else {
                throw new FormatException(string.Format("Drive \"{0}\" is not a virtual hard disk.", path));
            }
        }


        private static class NativeMethods {

            public const uint FILE_ATTRIBUTE_NORMAL = 0;
            public const uint GENERIC_READ = 0x80000000;
            public const uint GENERIC_WRITE = 0x40000000;
            public const int INVALID_HANDLE_VALUE = -1;
            public const uint NMPWAIT_USE_DEFAULT_WAIT = 0x00000000;
            public const uint OPEN_EXISTING = 3;
            public const uint PIPE_ACCESS_DUPLEX = 0x00000003;
            public const uint PIPE_READMODE_BYTE = 0x00000000;
            public const uint PIPE_TYPE_BYTE = 0x00000000;
            public const uint PIPE_UNLIMITED_INSTANCES = 255;
            public const uint PIPE_WAIT = 0x00000000;


            public class FileSafeHandle : SafeHandle {
                private static IntPtr minusOne = new IntPtr(-1);


                public FileSafeHandle()
                    : base(minusOne, true) { }


                public override bool IsInvalid {
                    get { return (this.IsClosed) || (base.handle == minusOne); }
                }

                protected override bool ReleaseHandle() {
                    return CloseHandle(this.handle);
                }

                public override string ToString() {
                    return this.handle.ToString();
                }

            }


            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool CloseHandle(System.IntPtr hObject);

        }


    }
}
