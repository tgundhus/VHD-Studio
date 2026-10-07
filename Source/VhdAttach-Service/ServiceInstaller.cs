using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using VhdAttachCommon;

namespace VhdAttachService {

    /// <summary>
    /// Registers and removes the Windows service through the Service Control Manager.
    /// Replaces System.Configuration.Install, which is not available on .NET.
    /// </summary>
    internal static class ServiceInstaller {

        public static void Install(string executablePath) {
            RemoveLegacyService();
            StopIfRunning(Branding.ServiceName);

            var scm = NativeMethods.OpenSCManager(null, null, NativeMethods.SC_MANAGER_ALL_ACCESS);
            if (scm == IntPtr.Zero) { throw new Win32Exception(); }
            try {
                var binaryPath = "\"" + executablePath + "\"";
                var service = NativeMethods.OpenService(scm, Branding.ServiceName, NativeMethods.SERVICE_ALL_ACCESS);
                if (service == IntPtr.Zero) {
                    service = NativeMethods.CreateService(scm, Branding.ServiceName, Branding.ProductName,
                        NativeMethods.SERVICE_ALL_ACCESS, NativeMethods.SERVICE_WIN32_OWN_PROCESS, NativeMethods.SERVICE_AUTO_START,
                        NativeMethods.SERVICE_ERROR_NORMAL, binaryPath, null, IntPtr.Zero, null, null, null);
                    if (service == IntPtr.Zero) { throw new Win32Exception(); }
                } else {
                    if (!NativeMethods.ChangeServiceConfig(service, NativeMethods.SERVICE_WIN32_OWN_PROCESS, NativeMethods.SERVICE_AUTO_START,
                        NativeMethods.SERVICE_ERROR_NORMAL, binaryPath, null, IntPtr.Zero, null, null, null, Branding.ProductName)) {
                        throw new Win32Exception();
                    }
                }
                try {
                    var description = new NativeMethods.SERVICE_DESCRIPTION { lpDescription = "Attaches virtual disks for " + Branding.ProductName + " and re-attaches selected disks at startup." };
                    NativeMethods.ChangeServiceConfig2(service, NativeMethods.SERVICE_CONFIG_DESCRIPTION, ref description);
                } finally {
                    NativeMethods.CloseServiceHandle(service);
                }
            } finally {
                NativeMethods.CloseServiceHandle(scm);
            }

            using (var sc = new ServiceController(Branding.ServiceName)) {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
            }
        }

        /// <summary>
        /// Returns false if the service did not exist.
        /// </summary>
        public static bool Uninstall() {
            RemoveLegacyService();
            return Delete(Branding.ServiceName);
        }


        /// <summary>
        /// Removes the service left behind by VHD Attach 4.x so both do not fight over the same disks.
        /// </summary>
        private static void RemoveLegacyService() {
            try { Delete(Branding.LegacyServiceName); } catch (Win32Exception) { }
        }

        private static bool Delete(string serviceName) {
            StopIfRunning(serviceName);
            var scm = NativeMethods.OpenSCManager(null, null, NativeMethods.SC_MANAGER_ALL_ACCESS);
            if (scm == IntPtr.Zero) { throw new Win32Exception(); }
            try {
                var service = NativeMethods.OpenService(scm, serviceName, NativeMethods.SERVICE_ALL_ACCESS);
                if (service == IntPtr.Zero) { return false; }
                try {
                    if (!NativeMethods.DeleteService(service)) { throw new Win32Exception(); }
                    return true;
                } finally {
                    NativeMethods.CloseServiceHandle(service);
                }
            } finally {
                NativeMethods.CloseServiceHandle(scm);
            }
        }

        /// <summary>
        /// Stops the service and waits until its process has really exited (a "stopped" service can still hold its files).
        /// </summary>
        private static void StopIfRunning(string serviceName) {
            try {
                using (var sc = new ServiceController(serviceName)) {
                    if (sc.Status != ServiceControllerStatus.Stopped) {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                    }
                }
            } catch (InvalidOperationException) {
            } catch (System.ServiceProcess.TimeoutException) { }

            var self = Environment.ProcessId;
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Branding.ServiceExe))) {
                using (process) {
                    if (process.Id == self) { continue; }
                    try {
                        if (!process.WaitForExit(15000)) { process.Kill(); process.WaitForExit(5000); } //last resort; the service is already stopped
                    } catch (InvalidOperationException) {
                    } catch (System.ComponentModel.Win32Exception) { }
                }
            }
        }


        private static class NativeMethods {

            public const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
            public const uint SERVICE_ALL_ACCESS = 0xF01FF;
            public const uint SERVICE_WIN32_OWN_PROCESS = 0x00000010;
            public const uint SERVICE_AUTO_START = 0x00000002;
            public const uint SERVICE_ERROR_NORMAL = 0x00000001;
            public const uint SERVICE_CONFIG_DESCRIPTION = 1;

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            public struct SERVICE_DESCRIPTION {
                public string lpDescription;
            }

            [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern IntPtr OpenSCManager(string machineName, string databaseName, uint dwAccess);

            [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

            [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern IntPtr CreateService(IntPtr hSCManager, string lpServiceName, string lpDisplayName, uint dwDesiredAccess, uint dwServiceType, uint dwStartType, uint dwErrorControl, string lpBinaryPathName, string lpLoadOrderGroup, IntPtr lpdwTagId, string lpDependencies, string lpServiceStartName, string lpPassword);

            [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool ChangeServiceConfig(IntPtr hService, uint nServiceType, uint nStartType, uint nErrorControl, string lpBinaryPathName, string lpLoadOrderGroup, IntPtr lpdwTagId, string lpDependencies, string lpServiceStartName, string lpPassword, string lpDisplayName);

            [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool ChangeServiceConfig2(IntPtr hService, uint dwInfoLevel, ref SERVICE_DESCRIPTION lpInfo);

            [DllImport("advapi32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool DeleteService(IntPtr hService);

            [DllImport("advapi32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool CloseServiceHandle(IntPtr hSCObject);

        }

    }

}
