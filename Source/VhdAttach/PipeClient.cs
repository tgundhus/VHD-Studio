using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace VhdAttach {
    internal static class PipeClient {

        /// <param name="avoidLetters">Letters the user uses for network or subst drives, which the service can't see.</param>
        public static PipeResponse Attach(string path, bool mountReadOnly, bool initializeDisk, bool noDriveLetter = false, string mountFolder = null, string avoidLetters = null) {
            var data = new Dictionary<string, string>();
            data.Add("Path", PathHelper.ToServicePath(path));
            data.Add("MountReadOnly", mountReadOnly.ToString(CultureInfo.InvariantCulture));
            data.Add("InitializeDisk", initializeDisk.ToString(CultureInfo.InvariantCulture));
            data.Add("NoDriveLetter", noDriveLetter.ToString(CultureInfo.InvariantCulture));
            data.Add("MountFolder", mountFolder ?? "");
            data.Add("AvoidLetters", avoidLetters ?? "");
            return Send("Attach", data, 30000); //log replay and mount folders can take a while
        }

        /// <param name="force">Detach even if files on the disk are open (unsaved data in them is lost).</param>
        public static PipeResponse Detach(string path, bool force = false) {
            var data = new Dictionary<string, string>();
            data.Add("Path", PathHelper.ToServicePath(path));
            data.Add("Force", force.ToString(CultureInfo.InvariantCulture));
            return Send("Detach", data, 60000); //locking volumes may wait for short-lived handles
        }

        public static PipeResponse DetachDrive(string path, bool force = false) {
            var data = new Dictionary<string, string>();
            data.Add("Path", path);
            data.Add("Force", force.ToString(CultureInfo.InvariantCulture));
            return Send("DetachDrive", data, 60000);
        }

        public static PipeResponse WriteContextMenuVhdSettings(bool open, bool attach, bool attachReadOnly, bool detach, bool detachDrive) {
            var data = new Dictionary<string, string>();
            data.Add("Open", open.ToString(CultureInfo.InvariantCulture));
            data.Add("Attach", attach.ToString(CultureInfo.InvariantCulture));
            data.Add("AttachReadOnly", attachReadOnly.ToString(CultureInfo.InvariantCulture));
            data.Add("Detach", detach.ToString(CultureInfo.InvariantCulture));
            data.Add("DetachDrive", detachDrive.ToString(CultureInfo.InvariantCulture));
            return Send("WriteContextMenuVhdSettings", data);
        }

        public static PipeResponse WriteContextMenuIsoSettings(bool open, bool attachReadOnly, bool detach) {
            var data = new Dictionary<string, string>();
            data.Add("Open", open.ToString(CultureInfo.InvariantCulture));
            data.Add("AttachReadOnly", attachReadOnly.ToString(CultureInfo.InvariantCulture));
            data.Add("Detach", detach.ToString(CultureInfo.InvariantCulture));
            return Send("WriteContextMenuIsoSettings", data);
        }

        public static PipeResponse WriteAutoAttachSettings(string[] autoAttachList) {
            var data = new Dictionary<string, string>();
            data.Add("AutoAttachList", string.Join("|", autoAttachList));
            return Send("WriteAutoAttachSettings", data);
        }

        public static PipeResponse ChangeDriveLetter(string volumeName, string newDriveLetter) {
            var data = new Dictionary<string, string>();
            data.Add("VolumeName", volumeName);
            data.Add("NewDriveLetter", newDriveLetter);
            return Send("ChangeDriveLetter", data);
        }

        public static PipeResponse RegisterExtensionVhd() {
            var data = new Dictionary<string, string>();
            return Send("RegisterExtensionVhd", data);
        }

        private static Medo.IO.NamedPipe Pipe = new Medo.IO.NamedPipe(VhdAttachCommon.Branding.PipeName);

        /// <summary>
        /// Refuses to talk to anything but the installed service process (protects against a rogue pipe server).
        /// </summary>
        private static void VerifyServer() {
            if (!NativeMethods.GetNamedPipeServerProcessId(Pipe.GetHandle(), out var serverPid)) { throw new System.IO.IOException("Cannot identify the service process."); }
            using (var searcher = new System.Management.ManagementObjectSearcher("SELECT ProcessId FROM Win32_Service WHERE Name = '" + VhdAttachCommon.Branding.ServiceName + "'")) {
                foreach (System.Management.ManagementObject service in searcher.Get()) {
                    using (service) {
                        if (System.Convert.ToUInt32(service["ProcessId"], CultureInfo.InvariantCulture) == serverPid) { return; }
                    }
                }
            }
            //interactive (/Interactive) instance of our own service executable, e.g. during development
            var expected = System.IO.Path.Combine(System.AppContext.BaseDirectory, VhdAttachCommon.Branding.ServiceExe);
            var process = NativeMethods.OpenProcess(0x1000 /*PROCESS_QUERY_LIMITED_INFORMATION*/, false, serverPid);
            if (process != System.IntPtr.Zero) {
                try {
                    var image = new System.Text.StringBuilder(1024);
                    var size = image.Capacity;
                    if (NativeMethods.QueryFullProcessImageName(process, 0, image, ref size) && string.Equals(image.ToString(), expected, System.StringComparison.OrdinalIgnoreCase)) { return; }
                } finally {
                    NativeMethods.CloseHandle(process);
                }
            }
            throw new System.IO.IOException("The command pipe is not served by the " + VhdAttachCommon.Branding.ProductName + " service.");
        }

        private static class NativeMethods {
            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
            [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
            public static extern bool GetNamedPipeServerProcessId(System.IntPtr pipe, out uint serverProcessId);

            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
            public static extern System.IntPtr OpenProcess(uint access, bool inherit, uint processId);

            [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
            [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
            public static extern bool QueryFullProcessImageName(System.IntPtr process, uint flags, System.Text.StringBuilder name, ref int size);

            [System.Runtime.InteropServices.DllImport("kernel32.dll")]
            [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
            public static extern bool CloseHandle(System.IntPtr handle);
        }

        private static PipeResponse Send(string operation, Dictionary<string, string> data, int timeout = 5000) {
            var packetOut = new Medo.Net.TinyPacket(VhdAttachCommon.Branding.PacketProduct, operation, data);
            try {
                Pipe.Open();
                VerifyServer();
                Pipe.Write(packetOut.GetBytes());
                var timer = new Stopwatch();
                timer.Start();
                while (timer.ElapsedMilliseconds < timeout) {
                    if (Pipe.HasBytesToRead) { break; }
                    Thread.Sleep(100);
                }
                timer.Stop();
                if (Pipe.HasBytesToRead) {
                    var buffer = Pipe.ReadAvailable();
                    var packetIn = Medo.Net.TinyPacket.Parse(buffer);
                    return new PipeResponse(bool.Parse(packetIn["IsError"]), packetIn["Message"], packetIn["ErrorCode"], packetIn["DriveLetters"]);
                } else {
                    return new PipeResponse(true, "The service did not answer in time. The operation may still finish; refresh before trying again.");
                }
            } finally {
                Pipe.Close();
            }
        }

    }
}
