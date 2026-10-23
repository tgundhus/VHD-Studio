using System;
using System.IO;
using System.Management;
using VhdStudio.Vsux;

namespace VhdAttach {
    internal static class VsuxDriveRouting {
        // Resolve a drive through read-only storage metadata. Never turn a failed
        // lookup into permission to detach a possibly password-gated container.
        public static string FindContainer(string path) {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            uint? diskNumber = null;
            using (var partitions = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT DiskNumber, AccessPaths FROM MSFT_Partition")) {
                foreach (ManagementObject partition in partitions.Get()) {
                    using (partition) {
                        var paths = partition["AccessPaths"] as string[];
                        if (paths == null || !Array.Exists(paths, p => string.Equals(p, root, StringComparison.OrdinalIgnoreCase))) { continue; }
                        var number = Convert.ToUInt32(partition["DiskNumber"], System.Globalization.CultureInfo.InvariantCulture);
                        if (diskNumber.HasValue && diskNumber.Value != number) { throw new NotSupportedException("Select the VSUX file directly to detach a multi-disk volume safely."); }
                        diskNumber = number;
                    }
                }
            }
            if (!diskNumber.HasValue) { return null; }
            using (var disks = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT Location FROM MSFT_Disk WHERE Number = " + diskNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))) {
                foreach (ManagementObject disk in disks.Get()) {
                    using (disk) {
                        var location = disk["Location"] as string;
                        return VsuxOperations.IsVsuxPath(location) ? location : null;
                    }
                }
            }
            throw new IOException("The drive identity changed. Select the image file and retry.");
        }
    }
}
